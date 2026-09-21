using System.Net;
using System.Text;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="SevenTvEmoteSetListService"/> against the guard chain of spec 2026-09-20, 6.1
/// (AK 23, AK 24, AK 94): cache, single-flight, circuit breaker, provider budget, request — in that
/// order, with the collaborators the foreign-channel preview already uses.
/// </summary>
/// <remarks>
/// The two cases at the bottom are the cross test AK 94 asks for, and they are the reason the
/// breaker grew an operation name at all: a list query that fails five times must not answer for
/// the preview, while a confirmed 429 must answer for both. Everything above them would still pass
/// if both paths shared one failure counter, which is exactly why they are not the whole file.
/// </remarks>
public class SevenTvEmoteSetListServiceTests
{
    private const string TwitchId = "49140130";
    private const string ActiveSetId = "01GV88A38G0006FW5TVZVMG507";
    private const string Channel = "handofblood";

    [Fact]
    public async Task ACacheHit_AnswersWithoutAnyUpstreamRequest()
    {
        var client = ClientReturning(OkListing());
        var cache = new FakeListCache();
        await cache.SetAsync(TwitchId, EmoteSetListResult.Ok(new EmoteSetList(ActiveSetId, [])), TimeSpan.FromSeconds(60));
        var service = CreateService(client, cache);

        var result = await service.ListByTwitchIdAsync(TwitchId);

        Assert.Equal(EmoteSetListStatus.Ok, result.Status);
        Assert.Empty(result.List!.Sets);
        await client.DidNotReceive().GetEmoteSetListForTwitchUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AColdMiss_AsksUpstream_AndHoldsTheAnswerForAMinute()
    {
        var client = ClientReturning(OkListing());
        var cache = new FakeListCache();
        var service = CreateService(client, cache);

        var result = await service.ListByTwitchIdAsync(TwitchId);

        Assert.Equal(EmoteSetListStatus.Ok, result.Status);
        Assert.Equal(ActiveSetId, result.List!.SevenTvActiveEmoteSetId);
        await client.Received(1).GetEmoteSetListForTwitchUserAsync(TwitchId, Arg.Any<CancellationToken>());
        Assert.Equal(SevenTvEmoteSetListService.AnswerTimeToLive, cache.LastTimeToLive);

        // …and the second caller inside that minute is served from the hold, not from 7TV.
        Assert.Equal(EmoteSetListStatus.Ok, (await service.ListByTwitchIdAsync(TwitchId)).Status);
        await client.Received(1).GetEmoteSetListForTwitchUserAsync(TwitchId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AK 23: a Redis outage degrades this path to "always live", never to a 503. The cache is a
    /// cost optimisation, not a correctness boundary — every call simply pays for itself.
    /// </summary>
    [Fact]
    public async Task ARedisOutage_DegradesToAlwaysLive_NotToAFailure()
    {
        var client = ClientReturning(OkListing());
        var service = CreateService(client, new FakeListCache { Outage = true });

        Assert.Equal(EmoteSetListStatus.Ok, (await service.ListByTwitchIdAsync(TwitchId)).Status);
        Assert.Equal(EmoteSetListStatus.Ok, (await service.ListByTwitchIdAsync(TwitchId)).Status);

        await client.Received(2).GetEmoteSetListForTwitchUserAsync(TwitchId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AK 24/F14: every upstream request this path makes charges exactly one permit of the
    /// provider-wide budget — the same 60-a-minute budget the foreign-channel preview spends from.
    /// Run against the real client, because the permit is charged where the request actually leaves
    /// the process; a substituted client would prove the test, not the rule.
    /// </summary>
    [Fact]
    public async Task EveryUpstreamRequest_ChargesExactlyOnePermit()
    {
        var requestBudget = new RecordingForeignUpstreamRequestBudget();
        var service = CreateService(
            RealClient(_ => MeasuredAnswer(), requestBudget), new FakeListCache { Outage = true });

        await service.ListByTwitchIdAsync(TwitchId);
        await service.ListByTwitchIdAsync(TwitchId);

        Assert.Equal(2, requestBudget.Charges);
    }

    /// <summary>
    /// AK 23, the half a cache cannot cover: a cache only fills once the first call has
    /// <i>finished</i>, so without coalescing every concurrent cold miss goes upstream on its own.
    /// Ten people opening the picker on the same team at the same moment is the ordinary case, not
    /// the pathological one.
    /// </summary>
    [Fact]
    public async Task ParallelColdMisses_ShareExactlyOneUpstreamRequest()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var client = Substitute.For<ISevenTvApiClient>();
        client.GetEmoteSetListForTwitchUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref calls);
                await gate.Task;
                return OkListing();
            });
        var service = CreateService(client, new FakeListCache { Outage = true });

        var callers = Enumerable.Range(0, 8).Select(_ => service.ListByTwitchIdAsync(TwitchId)).ToList();
        gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.All(results, result => Assert.Equal(EmoteSetListStatus.Ok, result.Status));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    /// <summary>
    /// AK 23, the race the coalescer alone does not close: caller A can finish the whole guard chain,
    /// write the cache and leave the coalescer's in-flight table before caller B — who read a genuine
    /// cold miss on the outer cache check — ever reaches the coalesced factory. Without a second cache
    /// look inside that factory, B would run the whole guard chain again for an answer that has been
    /// sitting in the cache the entire time, which is exactly the extra upstream request AK 23
    /// forbids. <see cref="GatedListCache"/> holds B's outer miss open past A's entire run to force
    /// this exact interleaving instead of leaving it to chance.
    /// </summary>
    [Fact]
    public async Task ARaceWhereAFinishesBeforeBEntersTheCoalescer_SharesTheSingleUpstreamRequest()
    {
        var client = ClientReturning(OkListing());
        var cache = new GatedListCache();
        var service = CreateService(client, cache);

        // B's outer cache read happens now, while the cache is still empty — a genuine miss — but is
        // held from returning to ListByTwitchIdAsync until the gate below is released.
        cache.PauseFirstCallUntilReleased();
        var bTask = service.ListByTwitchIdAsync(TwitchId);

        // A runs the whole guard chain to completion: one upstream request, the cache filled with the
        // answer, and its in-flight coalescer entry removed — all of it finished before B is let go.
        var aResult = await service.ListByTwitchIdAsync(TwitchId);
        Assert.Equal(EmoteSetListStatus.Ok, aResult.Status);
        await client.Received(1).GetEmoteSetListForTwitchUserAsync(TwitchId, Arg.Any<CancellationToken>());

        cache.Release();
        var bResult = await bTask;

        Assert.Equal(EmoteSetListStatus.Ok, bResult.Status);
        Assert.Equal(aResult.List!.SevenTvActiveEmoteSetId, bResult.List!.SevenTvActiveEmoteSetId);
        // The fix under test: B's recheck inside the coalesced factory found A's answer, so the guard
        // chain — breaker, budget and client alike — never ran a second time for B.
        await client.Received(1).GetEmoteSetListForTwitchUserAsync(TwitchId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The same race, but A's answer is a held negative outcome rather than a hit — negative results
    /// live in the same key space (6.1) and the recheck has to see them too, not just successes.
    /// </summary>
    [Fact]
    public async Task ARaceWhereAFinishesBeforeBEntersTheCoalescer_SharesAHeldNegativeResultToo()
    {
        var client = ClientReturning(SevenTvEmoteSetListResult.Failed(SevenTvEmoteSetListLookupStatus.Unavailable));
        var cache = new GatedListCache();
        var service = CreateService(client, cache);

        cache.PauseFirstCallUntilReleased();
        var bTask = service.ListByTwitchIdAsync(TwitchId);

        var aResult = await service.ListByTwitchIdAsync(TwitchId);
        Assert.Equal(EmoteSetListStatus.Unavailable, aResult.Status);
        await client.Received(1).GetEmoteSetListForTwitchUserAsync(TwitchId, Arg.Any<CancellationToken>());

        cache.Release();
        var bResult = await bTask;

        Assert.Equal(EmoteSetListStatus.Unavailable, bResult.Status);
        await client.Received(1).GetEmoteSetListForTwitchUserAsync(TwitchId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The disguise that has cost this codebase a feature before (F17/E4a): 7TV answers an overload
    /// with HTTP 200 and a GraphQL error carrying <c>extensions.status: 429</c>. Read as a success
    /// it would look like an account with no sets at all.
    /// </summary>
    [Fact]
    public async Task ADisguisedRateLimit_IsRateLimited_NotAnEmptyOk()
    {
        var service = CreateService(
            RealClient(_ => RateLimitedPayload, new RecordingForeignUpstreamRequestBudget()),
            new FakeListCache());

        var result = await service.ListByTwitchIdAsync(TwitchId);

        Assert.Equal(EmoteSetListStatus.RateLimited, result.Status);
        Assert.Null(result.List);
    }

    [Fact]
    public async Task AnOpenBreaker_AnswersWithoutAnUpstreamRequest()
    {
        var client = ClientReturning(OkListing());
        var breaker = new ForeignSevenTvBreakerPolicy();
        breaker.RecordFailure(
            ForeignSevenTvBreakerOperations.EmoteSetList,
            ForeignSevenTvBreakerOutcome.RateLimited,
            TimeSpan.FromMinutes(10),
            breaker.TryAcquire(ForeignSevenTvBreakerOperations.EmoteSetList).Generation);
        var service = CreateService(client, new FakeListCache(), breaker);

        var result = await service.ListByTwitchIdAsync(TwitchId);

        Assert.Equal(EmoteSetListStatus.RateLimited, result.Status);
        await client.DidNotReceive().GetEmoteSetListForTwitchUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 6.1, "Fehler werden gecacht, nicht wiederholt": a failure is held for its own short while, so
    /// that reopening a dialog during an outage does not ask 7TV again — the moment the budget can
    /// least afford it.
    /// </summary>
    [Fact]
    public async Task AFailure_IsHeldForItsShelfLife_NotRetriedOnTheNextCall()
    {
        var client = ClientReturning(SevenTvEmoteSetListResult.Failed(SevenTvEmoteSetListLookupStatus.Unavailable));
        var cache = new FakeListCache();
        var service = CreateService(client, cache);

        Assert.Equal(EmoteSetListStatus.Unavailable, (await service.ListByTwitchIdAsync(TwitchId)).Status);
        Assert.Equal(SevenTvEmoteSetListService.UnavailableTimeToLive, cache.LastTimeToLive);

        Assert.Equal(EmoteSetListStatus.Unavailable, (await service.ListByTwitchIdAsync(TwitchId)).Status);
        await client.Received(1).GetEmoteSetListForTwitchUserAsync(TwitchId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// "7TV carries no account for this connection" is an answer, not a failure: it is held for the
    /// full minute a hit gets, and it counts as evidence 7TV is healthy rather than against the
    /// breaker.
    /// </summary>
    [Fact]
    public async Task NoSevenTvAccount_IsHeldLikeAnAnswer_AndDoesNotCountAgainstTheBreaker()
    {
        var client = ClientReturning(SevenTvEmoteSetListResult.Failed(SevenTvEmoteSetListLookupStatus.NoSevenTvAccount));
        var cache = new FakeListCache();
        var breaker = new ForeignSevenTvBreakerPolicy();
        var service = CreateService(client, cache, breaker);

        var result = await service.ListByTwitchIdAsync(TwitchId);

        Assert.Equal(EmoteSetListStatus.NoSevenTvAccount, result.Status);
        Assert.Equal(SevenTvEmoteSetListService.AnswerTimeToLive, cache.LastTimeToLive);
        Assert.True(breaker.TryAcquire(ForeignSevenTvBreakerOperations.EmoteSetList).Allowed);
    }

    /// <summary>
    /// <c>isPersonal</c> is the kind and nothing else (E7). It exists for the label alone — every
    /// kind other than <c>NORMAL</c> is equally unselectable (8.6) — which is also why an unknown
    /// kind 7TV adds later simply is not personal, rather than breaking anything.
    /// </summary>
    [Fact]
    public async Task OnlyThePersonalKind_IsFlaggedAsPersonal()
    {
        var client = ClientReturning(SevenTvEmoteSetListResult.Ok(new SevenTvEmoteSetListing(ActiveSetId, [
            new SevenTvEmoteSetListEntry(ActiveSetId, "Emotes", 1000, "NORMAL", "HandOfBlood"),
            new SevenTvEmoteSetListEntry("p", "Personal Emotes", 5, "PERSONAL", "HandOfBlood"),
            new SevenTvEmoteSetListEntry("g", "Global", null, "GLOBAL", null),
            new SevenTvEmoteSetListEntry("x", "Something New", null, "SOMETHING_7TV_ADDED", null),
        ])));
        var service = CreateService(client, new FakeListCache());

        var result = await service.ListByTwitchIdAsync(TwitchId);

        Assert.Equal([false, true, false, false], result.List!.Sets.Select(s => s.IsPersonal));
        Assert.Equal(["NORMAL", "PERSONAL", "GLOBAL", "SOMETHING_7TV_ADDED"], result.List.Sets.Select(s => s.Kind));
    }

    /// <summary>
    /// AK 94, first half. Five consecutive <c>Unavailable</c> of the list query — which, per F17, is
    /// what our own malformed query would look like — open the list's breaker and nothing else. The
    /// preview call that follows travels, and it does not have to take the probe out of a counter
    /// that was never its own.
    /// </summary>
    [Fact]
    public async Task RepeatedUnavailabilityOfTheList_DoesNotLockThePreviewPath()
    {
        var breaker = new ForeignSevenTvBreakerPolicy();
        var budget = new ForeignEmoteSetProviderBudget();
        var listClient = ClientReturning(SevenTvEmoteSetListResult.Failed(SevenTvEmoteSetListLookupStatus.Unavailable));
        var listService = CreateService(listClient, new FakeListCache { Outage = true }, breaker, budget);
        var preview = CreatePreview(breaker, budget, out var previewInner);

        for (var i = 0; i < ForeignSevenTvBreakerPolicy.FailureThreshold; i++)
        {
            Assert.Equal(EmoteSetListStatus.Unavailable, (await listService.ListByTwitchIdAsync(TwitchId)).Status);
        }

        // The list is locked out…
        await listService.ListByTwitchIdAsync(TwitchId);
        await listClient.Received(ForeignSevenTvBreakerPolicy.FailureThreshold)
            .GetEmoteSetListForTwitchUserAsync(TwitchId, Arg.Any<CancellationToken>());

        // …and the preview, which is fine, still answers.
        var previewResult = await preview.GetForeignEmoteSetAsync(Channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, previewResult.Status);
        await previewInner.Received(1).GetForeignEmoteSetAsync(Channel, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AK 94, second half — and the reason a second, keyed breaker instance was rejected. 7TV limits
    /// the bucket, not the query: a confirmed 429 on the list path locks the preview too, and with
    /// the same waiting time, because the preview would otherwise run straight into the same
    /// lockout to find out for itself.
    /// </summary>
    [Fact]
    public async Task AConfirmedRateLimitOnTheList_LocksThePreviewPathToo()
    {
        var breaker = new ForeignSevenTvBreakerPolicy();
        var budget = new ForeignEmoteSetProviderBudget();
        var listClient = ClientReturning(SevenTvEmoteSetListResult.Failed(
            SevenTvEmoteSetListLookupStatus.RateLimited, TimeSpan.FromMinutes(30)));
        var listService = CreateService(listClient, new FakeListCache { Outage = true }, breaker, budget);
        var preview = CreatePreview(breaker, budget, out var previewInner);

        Assert.Equal(EmoteSetListStatus.RateLimited, (await listService.ListByTwitchIdAsync(TwitchId)).Status);

        var previewResult = await preview.GetForeignEmoteSetAsync(Channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.SevenTvRateLimited, previewResult.Status);
        await previewInner.DidNotReceive()
            .GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        // 7TV's own waiting time, not the breaker's 60 s default — the preview is told what the
        // list was told. (Wall clock, hence the window rather than an exact figure.)
        var remaining = breaker.TryAcquire(ForeignSevenTvBreakerOperations.ForeignPreview).RemainingOpenTime;
        Assert.InRange(remaining, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));
    }

    private const string RateLimitedPayload =
        """{"data":null,"errors":[{"message":"too many requests","extensions":{"code":"RATE_LIMITED","status":429}}]}""";

    private static SevenTvEmoteSetListResult OkListing() => SevenTvEmoteSetListResult.Ok(
        new SevenTvEmoteSetListing(ActiveSetId, [
            new SevenTvEmoteSetListEntry(ActiveSetId, "HandOfBlood's Emotes", 1000, "NORMAL", "HandOfBlood")
        ]));

    private static ISevenTvApiClient ClientReturning(SevenTvEmoteSetListResult result)
    {
        var client = Substitute.For<ISevenTvApiClient>();
        client.GetEmoteSetListForTwitchUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
        return client;
    }

    private static SevenTvApiClient RealClient(Func<string, string> responseForTwitchId, IForeignUpstreamRequestBudget budget)
    {
        var httpClient = new HttpClient(new StubHandler(responseForTwitchId))
        {
            BaseAddress = new Uri("https://7tv.io/v3/"),
        };
        return new SevenTvApiClient(
            httpClient, new RecordingRateLimitTelemetry(), budget, new RecordingLogger<SevenTvApiClient>());
    }

    private static string MeasuredAnswer() =>
        """
        {"data":{"users":{"userByConnection":{"id":"01GQA28FCR0002Q9KS8SKQKVXX","style":{"activeEmoteSetId":"01GV88A38G0006FW5TVZVMG507"},"emoteSets":[{"id":"01GV88A38G0006FW5TVZVMG507","name":"HandOfBlood's Emotes","capacity":1000,"kind":"NORMAL","owner":{"id":"01GQA28FCR0002Q9KS8SKQKVXX","mainConnection":{"platformDisplayName":"HandOfBlood"}}}]}}}
        """;

    private static SevenTvEmoteSetListService CreateService(
        ISevenTvApiClient client,
        ISevenTvEmoteSetListCache cache,
        ForeignSevenTvBreakerPolicy? breaker = null,
        ForeignEmoteSetProviderBudget? budget = null) => new(
        client,
        cache,
        new ForeignEmoteSetRequestCoalescer<EmoteSetListResult>(),
        breaker ?? new ForeignSevenTvBreakerPolicy(),
        budget ?? new ForeignEmoteSetProviderBudget(),
        NullLogger<SevenTvEmoteSetListService>.Instance);

    // The preview path as production wires it, sharing this test's breaker and budget instance —
    // the only way the cross test can say anything about what one path does to the other.
    private static HardenedForeignEmoteSetService CreatePreview(
        ForeignSevenTvBreakerPolicy breaker, ForeignEmoteSetProviderBudget budget, out IForeignEmoteSetService inner)
    {
        inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ForeignEmoteSetLookupResult.Ok(
                new ForeignEmoteSet(Channel, "01FRY81K4800085N93FNKSBYXS", "set", 0, false, []))));

        return new HardenedForeignEmoteSetService(
            inner,
            Substitute.For<IForeignEmoteSetCache>(),
            new ForeignEmoteSetRequestCoalescer(),
            breaker,
            budget,
            new RecordingRateLimitTelemetry(),
            NullLogger<HardenedForeignEmoteSetService>.Instance);
    }

    /// <summary>
    /// The cache as the service sees it — a dictionary, plus the one failure mode that matters:
    /// <see cref="Outage"/> makes every read a miss and every write a no-op, which is exactly how
    /// the real, fail-open Redis cache behaves when Redis is gone.
    /// </summary>
    private sealed class FakeListCache : ISevenTvEmoteSetListCache
    {
        private readonly Dictionary<string, EmoteSetListResult> _entries = new(StringComparer.Ordinal);

        public bool Outage { get; init; }

        /// <summary>The shelf-life of the most recent write — the shape of the negative-hold rule.</summary>
        public TimeSpan? LastTimeToLive { get; private set; }

        public Task<EmoteSetListResult?> TryGetAsync(string twitchChannelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(!Outage && _entries.TryGetValue(twitchChannelId, out var entry) ? entry : null);

        public Task SetAsync(
            string twitchChannelId, EmoteSetListResult result, TimeSpan timeToLive, CancellationToken cancellationToken = default)
        {
            LastTimeToLive = timeToLive;
            if (!Outage)
            {
                _entries[twitchChannelId] = result;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A cache that can hold one caller's read open past an arbitrary amount of other work, to force
    /// the exact interleaving <see cref="ARaceWhereAFinishesBeforeBEntersTheCoalescer_SharesTheSingleUpstreamRequest"/>
    /// needs instead of leaving it to scheduler luck. <see cref="PauseFirstCallUntilReleased"/> arms a
    /// one-shot gate: the very next <see cref="TryGetAsync"/> call reads the dictionary immediately
    /// (so it sees a genuine miss if the dictionary is still empty at that moment) but does not return
    /// to its caller until <see cref="Release"/> is called — every call after that first one, armed or
    /// not, resolves synchronously, exactly like <see cref="FakeListCache"/>.
    /// </summary>
    private sealed class GatedListCache : ISevenTvEmoteSetListCache
    {
        private readonly Dictionary<string, EmoteSetListResult> _entries = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _pauseNextCall;

        public void PauseFirstCallUntilReleased() => _pauseNextCall = true;

        public void Release() => _gate.SetResult();

        public Task<EmoteSetListResult?> TryGetAsync(string twitchChannelId, CancellationToken cancellationToken = default)
        {
            var captured = _entries.TryGetValue(twitchChannelId, out var entry) ? entry : null;
            if (!_pauseNextCall)
            {
                return Task.FromResult(captured);
            }

            _pauseNextCall = false;
            return AwaitGateThenReturn(captured);
        }

        public Task SetAsync(
            string twitchChannelId, EmoteSetListResult result, TimeSpan timeToLive, CancellationToken cancellationToken = default)
        {
            _entries[twitchChannelId] = result;
            return Task.CompletedTask;
        }

        private async Task<EmoteSetListResult?> AwaitGateThenReturn(EmoteSetListResult? captured)
        {
            await _gate.Task;
            return captured;
        }
    }

    private sealed class StubHandler(Func<string, string> responseForTwitchId) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseForTwitchId(body), Encoding.UTF8, "application/json"),
            };
        }
    }
}
