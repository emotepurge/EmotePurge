using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// The hardening decorator (spec 2026-09-09, section 6/T2) against real Redis: the 60 s cache (E3),
/// <c>refresh=true</c> bypassing it, request coalescing, and the provider-wide budget (E5b) actually
/// being shared across different source channels rather than partitioned like the per-user ASP.NET
/// policy. The breaker's own rules (E4) live in a pure, container-free suite
/// (<c>ForeignSevenTvBreakerPolicyTests</c>); what belongs here instead is how the decorator feeds it
/// under real concurrency — a straggler success arriving after a parallel 429, and a budget refusal
/// that must never be mistaken for a 7TV failure. Most tests only need it out of the way, and get a
/// fresh, always-allowing one.
/// </summary>
[Collection("Redis")]
public class HardenedForeignEmoteSetServiceTests(RedisFixture fixture)
{
    [Fact]
    public async Task ASuccessfulLookup_IsCached_AndTheSecondCallNeverReachesTheInnerChain()
    {
        var channel = NewChannel();
        var inner = InnerReturning(channel, callCount: out var calls);
        var service = CreateService(inner);

        var first = await service.GetForeignEmoteSetAsync(channel);
        var second = await service.GetForeignEmoteSetAsync(channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, first.Status);
        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, second.Status);
        Assert.Equal(1, calls());
    }

    [Fact]
    public async Task ACacheEntry_CarriesRoughlyTheSpecdSixtySecondTtl()
    {
        var channel = NewChannel();
        var inner = InnerReturning(channel, callCount: out _);
        var service = CreateService(inner);

        await service.GetForeignEmoteSetAsync(channel);

        var ttl = await fixture.Connection.GetDatabase().KeyTimeToLiveAsync($"7tvforeign:{ChannelName.Normalize(channel)}");
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value.TotalSeconds, 1, 60);
    }

    [Fact]
    public async Task Refresh_BypassesTheCache_AndOverwritesItWithTheFreshAnswer()
    {
        var channel = NewChannel();
        var callCount = 0;
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var n = Interlocked.Increment(ref callCount);
                return Task.FromResult(ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: n)));
            });
        var service = CreateService(inner);

        var first = await service.GetForeignEmoteSetAsync(channel);
        var refreshed = await service.GetForeignEmoteSetAsync(channel, refresh: true);
        var thirdPlain = await service.GetForeignEmoteSetAsync(channel);

        Assert.Equal(1, first.EmoteSet!.TotalCount);
        // refresh=true reached the inner chain a second time instead of answering from the cache
        // primed by the first call.
        Assert.Equal(2, refreshed.EmoteSet!.TotalCount);
        Assert.Equal(2, callCount);
        // And the refresh's answer overwrote the cache — a later plain call sees it, not the stale
        // first answer, and does not cost a third inner call.
        Assert.Equal(2, thirdPlain.EmoteSet!.TotalCount);
        Assert.Equal(2, callCount);
    }

    /// <summary>
    /// Parallel identical lookups for the same channel share one upstream execution (spec section 6,
    /// "Koaleszierung") — proven with an inner chain that blocks until released, so every concurrent
    /// caller is genuinely in flight at once rather than serialized by accident.
    /// </summary>
    [Fact]
    public async Task ConcurrentLookupsForTheSameChannel_ShareOneUpstreamExecution()
    {
        var channel = NewChannel();
        var gate = new TaskCompletionSource();
        var callCount = 0;
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                Interlocked.Increment(ref callCount);
                await gate.Task;
                return ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1));
            });
        var service = CreateService(inner);

        var tasks = Enumerable.Range(0, 5).Select(_ => service.GetForeignEmoteSetAsync(channel)).ToArray();
        await Task.Delay(200); // let every caller genuinely arrive and coalesce before releasing
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, callCount);
        Assert.All(results, r => Assert.Equal(ForeignEmoteSetLookupStatus.Ok, r.Status));
    }

    /// <summary>
    /// AK 9 / E5b: the provider-wide budget is shared across *different* channels — the dimension the
    /// per-user ASP.NET policy cannot see at all. Three distinct channels (standing in for three
    /// different users' lookups) against a two-slot budget: the third genuinely waits instead of
    /// reaching the inner chain immediately.
    /// </summary>
    [Fact]
    public async Task ThirdConcurrentChannel_WaitsForABudgetSlot_InsteadOfCallingTheInnerChainImmediately()
    {
        var channelA = NewChannel();
        var channelB = NewChannel();
        var channelC = NewChannel();
        var gate = new TaskCompletionSource();
        var inFlight = 0;
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                Interlocked.Increment(ref inFlight);
                await gate.Task;
                return ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1));
            });
        var service = CreateService(inner);

        var taskA = service.GetForeignEmoteSetAsync(channelA);
        var taskB = service.GetForeignEmoteSetAsync(channelB);
        await Task.Delay(200); // let A and B genuinely occupy both budget slots

        Assert.Equal(2, Volatile.Read(ref inFlight));
        var taskC = service.GetForeignEmoteSetAsync(channelC);
        await Task.Delay(200); // C must still be waiting for a slot, not calling the inner chain
        Assert.Equal(2, Volatile.Read(ref inFlight));

        gate.SetResult();
        var results = await Task.WhenAll(taskA, taskB, taskC);

        Assert.Equal(3, Volatile.Read(ref inFlight));
        Assert.All(results, r => Assert.Equal(ForeignEmoteSetLookupStatus.Ok, r.Status));
    }

    /// <summary>The other half of AK 9: once a slot frees, the waiting channel actually proceeds and
    /// completes successfully rather than timing out just because it had to wait at all.</summary>
    [Fact]
    public async Task AfterASlotFrees_TheWaitingChannel_CompletesSuccessfully()
    {
        var channelA = NewChannel();
        var channelB = NewChannel();
        var releaseFirstOnly = new TaskCompletionSource();
        var neverReleased = new TaskCompletionSource();
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(channelA, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await releaseFirstOnly.Task;
                return ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1));
            });
        inner.GetForeignEmoteSetAsync(channelB, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await neverReleased.Task; // occupies the second slot for the whole test
                return ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1));
            });
        var service = CreateService(inner);

        var taskA = service.GetForeignEmoteSetAsync(channelA);
        var taskB = service.GetForeignEmoteSetAsync(channelB);
        await Task.Delay(200);
        releaseFirstOnly.SetResult();

        var resultA = await taskA;
        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, resultA.Status);

        neverReleased.SetResult();
        await taskB;
    }


    /// <summary>
    /// E4, with the two lookups genuinely overlapping rather than merely described as overlapping:
    /// one channel's lookup is admitted while the breaker is still closed and then blocks; a second
    /// channel's lookup meets a 429 and opens the breaker for the hour 7TV asked for; only then does
    /// the first finish, successfully. That success predates the incident and must not reopen the
    /// gates — a third lookup afterwards still has to be turned away, or the feature would resume
    /// hammering a provider that has just locked us out.
    /// </summary>
    [Fact]
    public async Task ASuccessThatStartedBeforeAConcurrent429_DoesNotReopenTheBreaker()
    {
        var slowChannel = NewChannel();
        var rateLimitedChannel = NewChannel();
        var laterChannel = NewChannel();
        var releaseSlow = new TaskCompletionSource();
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(slowChannel, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await releaseSlow.Task;
                return ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1));
            });
        inner.GetForeignEmoteSetAsync(rateLimitedChannel, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(
                ForeignEmoteSetLookupStatus.SevenTvRateLimited, TimeSpan.FromHours(1)));
        inner.GetForeignEmoteSetAsync(laterChannel, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1))));
        var service = CreateService(inner);

        var slowTask = service.GetForeignEmoteSetAsync(slowChannel);
        await Task.Delay(200); // the slow lookup is genuinely admitted and in flight by now

        var rateLimited = await service.GetForeignEmoteSetAsync(rateLimitedChannel);
        Assert.Equal(ForeignEmoteSetLookupStatus.SevenTvRateLimited, rateLimited.Status);

        releaseSlow.SetResult();
        var slow = await slowTask;
        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, slow.Status);

        var later = await service.GetForeignEmoteSetAsync(laterChannel);

        Assert.Equal(ForeignEmoteSetLookupStatus.SevenTvRateLimited, later.Status);
        await inner.DidNotReceive().GetForeignEmoteSetAsync(laterChannel, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Coalescing must not hand the shared work over to whoever happened to arrive first. Two callers
    /// wait on one lookup; the first abandons its request — a browser navigating away is entirely
    /// routine — and the second still gets its answer from the same execution. The interleaving is
    /// real: the inner chain blocks on the token it was handed, so if that token were the first
    /// caller's, cancelling it would end the lookup for everyone.
    /// </summary>
    [Fact]
    public async Task OneCallerCancelling_DoesNotCancelTheSharedLookupForTheOthers()
    {
        var channel = NewChannel();
        var gate = new TaskCompletionSource();
        var callCount = 0;
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                Interlocked.Increment(ref callCount);
                await gate.Task.WaitAsync(ci.ArgAt<CancellationToken>(2));
                return ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1));
            });
        var service = CreateService(inner);

        using var firstCallerAborts = new CancellationTokenSource();
        var first = service.GetForeignEmoteSetAsync(channel, cancellationToken: firstCallerAborts.Token);
        var second = service.GetForeignEmoteSetAsync(channel);
        await Task.Delay(200); // both callers are genuinely coalesced onto one execution

        await firstCallerAborts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        gate.SetResult();
        var result = await second;

        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, result.Status);
        Assert.Equal(1, Volatile.Read(ref callCount));
    }

    /// <summary>
    /// Our own budget refusing a permit says nothing about 7TV, so it must not accumulate toward the
    /// breaker's five-failure threshold: five of them in a row leave the breaker closed, and the next
    /// lookup still reaches the inner chain. Counting them would let a burst of local congestion cut
    /// the feature off from a provider that never failed at all.
    /// </summary>
    [Fact]
    public async Task ProviderBudgetExhaustion_DoesNotCountTowardTheBreakersFailureStreak()
    {
        var channel = NewChannel();
        var calls = 0;
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(Interlocked.Increment(ref calls) <= ForeignSevenTvBreakerPolicy.FailureThreshold
                ? ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.ProviderBudgetExhausted)
                : ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1))));
        var service = CreateService(inner);

        for (var i = 0; i < ForeignSevenTvBreakerPolicy.FailureThreshold; i++)
        {
            var refused = await service.GetForeignEmoteSetAsync(channel);
            Assert.Equal(ForeignEmoteSetLookupStatus.ProviderBudgetExhausted, refused.Status);
        }

        var afterwards = await service.GetForeignEmoteSetAsync(channel);

        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, afterwards.Status);
        Assert.Equal(ForeignSevenTvBreakerPolicy.FailureThreshold + 1, Volatile.Read(ref calls));
    }

    /// <summary>
    /// AK 26 / E12: the set-ID mode reads and writes a second Redis key space
    /// (<c>7tvforeign:set:{setId}</c>), never the channel-login key space
    /// <c>7tvforeign:{login}</c> the other method above uses — an entry for a channel's currently
    /// non-active set must not collide with, or be shadowed by, the entry for that same channel's
    /// active set.
    /// </summary>
    [Fact]
    public async Task BySetId_UsesItsOwnKeySpace_SeparateFromTheChannelLoginCache()
    {
        var channel = NewChannel();
        var setId = $"set-{Guid.NewGuid():N}";
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetBySetIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ForeignEmoteSetLookupResult.Ok(
                NewEmoteSetBySetId(ci.ArgAt<string>(0), ci.ArgAt<string>(1)))));
        var service = CreateService(inner);

        var result = await service.GetForeignEmoteSetBySetIdAsync(channel, setId);

        Assert.Equal(ForeignEmoteSetLookupStatus.Ok, result.Status);
        var db = fixture.Connection.GetDatabase();
        Assert.True(await db.KeyExistsAsync($"7tvforeign:set:{setId}"));
        // The login-keyed space this channel would use for its active set stays untouched — the
        // set-ID lookup never wrote there.
        Assert.False(await db.KeyExistsAsync($"7tvforeign:{ChannelName.Normalize(channel)}"));
    }

    /// <summary>
    /// The other half of AK 26: an entry written by the plain, login-based lookup does not shadow —
    /// and is not shadowed by — an entry for the same channel's own currently-inactive set. Both
    /// caches are primed for the same channel, then both are read back independently.
    /// </summary>
    [Fact]
    public async Task AnEntryWithoutASetId_DoesNotOverwriteTheSetIdEntry_ForTheSameChannel_AndViceVersa()
    {
        var channel = NewChannel();
        var setId = $"set-{Guid.NewGuid():N}";
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(channel, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Ok(NewEmoteSet(channel, totalCount: 1)));
        inner.GetForeignEmoteSetBySetIdAsync(channel, setId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Ok(NewEmoteSetBySetId(channel, setId, totalCount: 2)));
        var service = CreateService(inner);

        var bySetId = await service.GetForeignEmoteSetBySetIdAsync(channel, setId);
        var byLogin = await service.GetForeignEmoteSetAsync(channel);

        // Both answer from their own live resolution, not from each other's cache entry.
        Assert.Equal(2, bySetId.EmoteSet!.TotalCount);
        Assert.Equal(1, byLogin.EmoteSet!.TotalCount);

        // And reading each a second time answers from its own cache entry, unaffected by the other's
        // write — a plain cache hit costs no further inner call.
        var bySetIdAgain = await service.GetForeignEmoteSetBySetIdAsync(channel, setId);
        var byLoginAgain = await service.GetForeignEmoteSetAsync(channel);
        Assert.Equal(2, bySetIdAgain.EmoteSet!.TotalCount);
        Assert.Equal(1, byLoginAgain.EmoteSet!.TotalCount);
        await inner.Received(1).GetForeignEmoteSetBySetIdAsync(channel, setId, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await inner.Received(1).GetForeignEmoteSetAsync(channel, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Spec 19's Prüfaufgabe for T2.2, proven directly rather than asserted: the coalescer's
    /// in-flight table is keyed by plain string (<c>ForeignEmoteSetRequestCoalescer</c>), and the two
    /// read modes use disjoint key namespaces — the bare normalized login for
    /// <see cref="HardenedForeignEmoteSetService.GetForeignEmoteSetAsync"/>, and <c>"set:{id}"</c> for
    /// <see cref="HardenedForeignEmoteSetService.GetForeignEmoteSetBySetIdAsync"/>, which a Twitch
    /// login can never collide with (no login can contain a colon). Proof: trigger a blocked, genuine
    /// set-ID lookup and a blocked, genuine login lookup for the very same string at once — if they
    /// coalesced onto one shared execution, only one of the two distinct inner calls below would ever
    /// fire, and the other caller would receive that call's (wrong) result instead of its own.
    /// </summary>
    [Fact]
    public async Task TheCoalescerKeepsTheSetIdAndTheLoginKeySpaces_Separate()
    {
        var sharedString = $"same-{Guid.NewGuid():N}"[..20];
        var gate = new TaskCompletionSource();
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(sharedString, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await gate.Task;
                return ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 111));
            });
        inner.GetForeignEmoteSetBySetIdAsync(Arg.Any<string>(), sharedString, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await gate.Task;
                return ForeignEmoteSetLookupResult.Ok(NewEmoteSetBySetId(ci.ArgAt<string>(0), ci.ArgAt<string>(1), totalCount: 222));
            });
        var service = CreateService(inner);

        var loginTask = service.GetForeignEmoteSetAsync(sharedString);
        var setIdTask = service.GetForeignEmoteSetBySetIdAsync(sharedString, sharedString);
        await Task.Delay(200); // let both callers genuinely arrive before releasing either
        gate.SetResult();
        var loginResult = await loginTask;
        var setIdResult = await setIdTask;

        // Each caller got its own execution's answer, not the other's — if the coalescer had merged
        // them onto one shared key, one of these two would carry the other's TotalCount instead.
        Assert.Equal(111, loginResult.EmoteSet!.TotalCount);
        Assert.Equal(222, setIdResult.EmoteSet!.TotalCount);
        await inner.Received(1).GetForeignEmoteSetAsync(sharedString, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await inner.Received(1).GetForeignEmoteSetBySetIdAsync(Arg.Any<string>(), sharedString, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Codex P2 (PR #215): the set-ID cache is keyed on the set alone (E12), so a cache entry a first
    /// channel's lookup wrote is reused, unchanged, for a second channel asking about the same set —
    /// but the interface's contract is that <c>ChannelName</c> always echoes the <i>current</i> route
    /// channel (spec 6.4), not whichever channel happened to populate the shared entry. Channel A
    /// primes the cache; channel B then reads the same set and must see its own channel echoed back,
    /// from a single upstream call.
    /// </summary>
    [Fact]
    public async Task CacheHitForASetId_EchoesTheCurrentCallersChannel_NotTheFirstCallersChannel()
    {
        var channelA = NewChannel();
        var channelB = NewChannel();
        var setId = $"set-{Guid.NewGuid():N}";
        var calls = 0;
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetBySetIdAsync(Arg.Any<string>(), setId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(ForeignEmoteSetLookupResult.Ok(NewEmoteSetBySetId(ci.ArgAt<string>(0), setId)));
            });
        var service = CreateService(inner);

        var first = await service.GetForeignEmoteSetBySetIdAsync(channelA, setId);
        var second = await service.GetForeignEmoteSetBySetIdAsync(channelB, setId);

        Assert.Equal(ChannelName.Normalize(channelA), first.EmoteSet!.ChannelName);
        Assert.Equal(ChannelName.Normalize(channelB), second.EmoteSet!.ChannelName);
        Assert.Equal(1, calls);
    }

    /// <summary>
    /// The other half of the same finding: concurrent misses for the same set coalesce onto one
    /// shared execution (coalescing key <c>set:{id}</c>, channel-free by design), so without the fix
    /// both callers would receive whichever channel the shared execution happened to resolve for —
    /// the first caller's. Genuinely overlapping, proven with an inner chain that blocks until both
    /// callers have arrived.
    /// </summary>
    [Fact]
    public async Task CoalescedMissForASetId_EchoesEachCallersOwnChannel()
    {
        var channelA = NewChannel();
        var channelB = NewChannel();
        var setId = $"set-{Guid.NewGuid():N}";
        var gate = new TaskCompletionSource();
        var calls = 0;
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetBySetIdAsync(Arg.Any<string>(), setId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                Interlocked.Increment(ref calls);
                await gate.Task;
                return ForeignEmoteSetLookupResult.Ok(NewEmoteSetBySetId(ci.ArgAt<string>(0), setId));
            });
        var service = CreateService(inner);

        var taskA = service.GetForeignEmoteSetBySetIdAsync(channelA, setId);
        var taskB = service.GetForeignEmoteSetBySetIdAsync(channelB, setId);
        await Task.Delay(200); // let both callers genuinely coalesce before releasing
        gate.SetResult();
        var resultA = await taskA;
        var resultB = await taskB;

        Assert.Equal(ChannelName.Normalize(channelA), resultA.EmoteSet!.ChannelName);
        Assert.Equal(ChannelName.Normalize(channelB), resultB.EmoteSet!.ChannelName);
        Assert.Equal(1, calls);
    }

    /// <summary>
    /// The echoed channel is the normalized route channel (Regel 9), not whatever casing the caller
    /// happened to type — proven on the cache-hit path, where the echo actually has work to do.
    /// </summary>
    [Fact]
    public async Task SetIdEcho_NormalizesTheRouteChannel()
    {
        var channelLower = NewChannel();
        var setId = $"set-{Guid.NewGuid():N}";
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetBySetIdAsync(Arg.Any<string>(), setId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ForeignEmoteSetLookupResult.Ok(NewEmoteSetBySetId(ci.ArgAt<string>(0), setId))));
        var service = CreateService(inner);

        await service.GetForeignEmoteSetBySetIdAsync(channelLower, setId);
        var second = await service.GetForeignEmoteSetBySetIdAsync(channelLower.ToUpperInvariant(), setId);

        Assert.Equal(ChannelName.Normalize(channelLower), second.EmoteSet!.ChannelName);
    }

    /// <summary>
    /// K3's source-set list (spec 2026-09-20, 6.3) deliberately gets none of this decorator's own
    /// hardening — its 7TV-facing half already runs behind <c>ISevenTvEmoteSetListService</c>'s own
    /// full guard chain (see the method's own doc). Proven here rather than assumed: two calls for
    /// the same channel both reach <c>inner</c>, which a cache (like every other method in this
    /// class) would have prevented after the first.
    /// </summary>
    [Fact]
    public async Task GetForeignEmoteSetListAsync_ForwardsEveryCallDirectlyToInner_NoCachingOfItsOwn()
    {
        var channel = NewChannel();
        var calls = 0;
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(ForeignEmoteSetListLookupResult.Ok(new EmoteSetList(null, [])));
            });
        var service = CreateService(inner);

        var first = await service.GetForeignEmoteSetListAsync(channel);
        var second = await service.GetForeignEmoteSetListAsync(channel);

        Assert.Equal(ForeignEmoteSetListLookupStatus.Ok, first.Status);
        Assert.Equal(ForeignEmoteSetListLookupStatus.Ok, second.Status);
        Assert.Equal(2, calls);
        await inner.Received(2).GetForeignEmoteSetListAsync(channel, Arg.Any<CancellationToken>());
    }

    private HardenedForeignEmoteSetService CreateService(IForeignEmoteSetService inner) => new(
        inner,
        new ForeignEmoteSetCache(fixture.Connection, NullLogger<ForeignEmoteSetCache>.Instance),
        new ForeignEmoteSetRequestCoalescer(),
        new ForeignSevenTvBreakerPolicy(),
        new ForeignEmoteSetProviderBudget(),
        new RecordingRateLimitTelemetry(),
        NullLogger<HardenedForeignEmoteSetService>.Instance);

    private static IForeignEmoteSetService InnerReturning(string channel, out Func<int> callCount)
    {
        var calls = 0;
        var inner = Substitute.For<IForeignEmoteSetService>();
        inner.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(ForeignEmoteSetLookupResult.Ok(NewEmoteSet(ci.ArgAt<string>(0), totalCount: 1)));
            });
        callCount = () => Volatile.Read(ref calls);
        return inner;
    }

    private static ForeignEmoteSet NewEmoteSet(string channelName, int totalCount) => new(
        ChannelName.Normalize(channelName),
        "01FRY81K4800085N93FNKSBYXS",
        "01FRY81K4800085N93FNKSBYXS-set",
        totalCount,
        false,
        []);

    // The set-ID mode's shape (E8): SevenTvUserId always null, EmoteSetId is whatever the caller
    // asked for rather than a fixed constant — set-ID lookups for different ids must not collide on
    // the emoteSetId a plain NewEmoteSet(...) would otherwise share across every call.
    private static ForeignEmoteSet NewEmoteSetBySetId(string channelName, string emoteSetId, int totalCount = 1) => new(
        ChannelName.Normalize(channelName),
        null,
        emoteSetId,
        totalCount,
        false,
        []);

    // A fresh, random channel per test so tests sharing one Redis container can never collide on the
    // same cache key.
    private static string NewChannel() => $"foreign-{Guid.NewGuid():N}"[..24];
}
