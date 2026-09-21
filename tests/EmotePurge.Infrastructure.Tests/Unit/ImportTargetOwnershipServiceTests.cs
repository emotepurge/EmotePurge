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
/// Pins <see cref="ImportTargetOwnershipService"/> — step 4 of the set-centric import's ladder as
/// amended by spec 2026-09-20, section 32: the owner check answers from the cached set lists of the
/// actor and every <c>editor_of</c> account, and asks 7TV directly only for a set that is in none
/// of them, under the provider budget and its own breaker operation.
/// </summary>
/// <remarks>
/// The two cases at the bottom run the real chain — list service, editor service and 7TV client —
/// with only the two caches and the HTTP handler faked. They are the evidence for the Codex finding
/// this class answers: an ordinary report whose lists are cached costs no upstream request, and the
/// one case that does costs exactly one, charged against the shared budget.
/// </remarks>
public class ImportTargetOwnershipServiceTests
{
    private const string ActorTwitchId = "100";
    private const string ActorLogin = "actor";
    private const string ActorSevenTvId = "01ACTOR";
    private const string EditedTwitchId = "200";
    private const string EditedLogin = "editedchannel";
    private const string EditedSevenTvId = "01EDITED";
    private const string StrangerSevenTvId = "01STRANGER";
    private const string EmoteSetId = "01TARGETSET";

    [Fact]
    public async Task ASetOwnedByTheActor_InTheActorsOwnList_IsAdmissible()
    {
        var lists = ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId, Set(EmoteSetId, ActorSevenTvId))));
        var editors = EditorsReturning(Grants());
        var client = Substitute.For<ISevenTvApiClient>();

        var result = await CreateService(lists, editors, client).CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(ActorSevenTvId, result.OwnerSevenTvUserId);
        Assert.Equal(ActorLogin, result.OwnerTwitchLogin);
        // The actor's own set needs nothing beyond the actor's own list.
        await editors.DidNotReceive().GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().LookUpEmoteSetOwnerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASetOwnedByAnEditorOfAccount_InThatAccountsList_IsAdmissible_UnderTheGrantsLogin()
    {
        var lists = ListsReturning(
            (ActorTwitchId, ListOf(ActorSevenTvId)),
            (EditedTwitchId, ListOf(EditedSevenTvId, Set(EmoteSetId, EditedSevenTvId))));
        var client = Substitute.For<ISevenTvApiClient>();

        var result = await CreateService(lists, EditorsReturning(Grants((EditedLogin, EditedTwitchId))), client)
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(EditedSevenTvId, result.OwnerSevenTvUserId);
        Assert.Equal(EditedLogin, result.OwnerTwitchLogin);
        await client.DidNotReceive().LookUpEmoteSetOwnerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The half of the rule that makes it E22's rule and not just "the set is on some list": 7TV
    /// can list a set under an account that does not own it, and being listed proves nothing then.
    /// No lookup is needed to say no — the list already named the owner.
    /// </summary>
    [Fact]
    public async Task ASetListedUnderAForeignOwner_IsForbidden_WithoutAnyLookup()
    {
        var lists = ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId, Set(EmoteSetId, StrangerSevenTvId))));
        var client = Substitute.For<ISevenTvApiClient>();

        var result = await CreateService(lists, EditorsReturning(Grants()), client)
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Forbidden, result.Status);
        await client.DidNotReceive().LookUpEmoteSetOwnerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Listed under account A, owned by account B, both of them checked — and B's list, which is
    /// what contributes B's id, only comes after A's. Admissible, under B's login.
    /// </summary>
    [Fact]
    public async Task ASetListedUnderOneCheckedAccount_ButOwnedByALaterOne_IsAdmissible()
    {
        var lists = ListsReturning(
            (ActorTwitchId, ListOf(ActorSevenTvId, Set(EmoteSetId, EditedSevenTvId))),
            (EditedTwitchId, ListOf(EditedSevenTvId)));

        var result = await CreateService(lists, EditorsReturning(Grants((EditedLogin, EditedTwitchId))), Substitute.For<ISevenTvApiClient>())
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(EditedLogin, result.OwnerTwitchLogin);
    }

    [Fact]
    public async Task ASetInNoList_ThatSevenTvDoesNotKnow_IsNotFound()
    {
        var client = ClientAnsweringOwner(SevenTvEmoteSetOwnerLookupResult.Failed(SevenTvEmoteSetOwnerLookupStatus.NotFound));

        var result = await CreateService(ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId))), EditorsReturning(Grants()), client)
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.SetNotFound, result.Status);
        await client.Received(1).LookUpEmoteSetOwnerAsync(EmoteSetId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASetInNoList_OwnedBySomeoneElse_IsForbidden()
    {
        var client = ClientAnsweringOwner(SevenTvEmoteSetOwnerLookupResult.Ok(StrangerSevenTvId));

        var result = await CreateService(ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId))), EditorsReturning(Grants()), client)
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Forbidden, result.Status);
    }

    /// <summary>
    /// A set created after the picker cached its lists is in no list yet — but its owner is a
    /// checked account, and the one lookup says so.
    /// </summary>
    [Fact]
    public async Task ASetInNoList_OwnedByACheckedAccount_IsAdmissible()
    {
        var client = ClientAnsweringOwner(SevenTvEmoteSetOwnerLookupResult.Ok(ActorSevenTvId));

        var result = await CreateService(ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId))), EditorsReturning(Grants()), client)
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(ActorLogin, result.OwnerTwitchLogin);
    }

    [Fact]
    public async Task ARefusedBudgetInTheFallback_IsUnavailable()
    {
        var client = ClientAnsweringOwner(SevenTvEmoteSetOwnerLookupResult.Failed(SevenTvEmoteSetOwnerLookupStatus.BudgetExhausted));

        var result = await CreateService(ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId))), EditorsReturning(Grants()), client)
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, result.Status);
    }

    /// <summary>The fallback does not reach 7TV at all while its breaker operation is open.</summary>
    [Fact]
    public async Task AnOpenBreaker_IsUnavailable_WithoutAnUpstreamRequest()
    {
        var breaker = new ForeignSevenTvBreakerPolicy();
        var decision = breaker.TryAcquire(ForeignSevenTvBreakerOperations.EmoteSetOwner);
        breaker.RecordFailure(
            ForeignSevenTvBreakerOperations.EmoteSetOwner, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromMinutes(5), decision.Generation);
        var client = ClientAnsweringOwner(SevenTvEmoteSetOwnerLookupResult.Ok(ActorSevenTvId));

        var result = await CreateService(ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId))), EditorsReturning(Grants()), client, breaker)
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, result.Status);
        await client.DidNotReceive().LookUpEmoteSetOwnerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APartialOutage_WithTheSetFoundAdmissibleElsewhere_IsStillAdmissible()
    {
        var lists = ListsReturning(
            (ActorTwitchId, EmoteSetListResult.Failed(EmoteSetListStatus.Unavailable)),
            (EditedTwitchId, ListOf(EditedSevenTvId, Set(EmoteSetId, EditedSevenTvId))));

        var result = await CreateService(lists, EditorsReturning(Grants((EditedLogin, EditedTwitchId))), Substitute.For<ISevenTvApiClient>())
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(EditedLogin, result.OwnerTwitchLogin);
    }

    /// <summary>
    /// The unreadable list may be the owner's — "we do not know" is 503, never 403. Holds on both
    /// ways to an answer: the set listed under a foreign owner, and the fallback naming one.
    /// </summary>
    [Fact]
    public async Task APartialOutage_WithoutAnAdmissibleFind_IsUnavailable_NotForbidden()
    {
        var lists = ListsReturning(
            (ActorTwitchId, ListOf(ActorSevenTvId, Set(EmoteSetId, StrangerSevenTvId))),
            (EditedTwitchId, EmoteSetListResult.Failed(EmoteSetListStatus.RateLimited)));
        var client = ClientAnsweringOwner(SevenTvEmoteSetOwnerLookupResult.Ok(StrangerSevenTvId));
        var service = CreateService(lists, EditorsReturning(Grants((EditedLogin, EditedTwitchId))), client);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, (await service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId)).Status);
        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, (await service.CheckAsync(ActorTwitchId, ActorLogin, "01OTHERSET")).Status);
    }

    [Fact]
    public async Task AnUnreadableGrantsLookup_WithoutAnAdmissibleFind_IsUnavailable()
    {
        var client = ClientAnsweringOwner(SevenTvEmoteSetOwnerLookupResult.Ok(StrangerSevenTvId));
        var editors = Substitute.For<IGuardedSevenTvEditorGrantsService>();
        editors.GetEditorGrantsAsync(ActorTwitchId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Failed(SevenTvLookupStatus.Unavailable));

        var result = await CreateService(ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId))), editors, client)
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, result.Status);
    }

    /// <summary>
    /// The core of the Codex finding (P1): the ordinary report — the set the user just picked, from
    /// lists the picker has just cached, grants the auth path has just cached — reaches 7TV zero
    /// times. Counted at the HTTP handler and at the provider budget of a real client, behind the
    /// real list and editor services: a substituted client would only prove the test.
    /// </summary>
    [Fact]
    public async Task TheOrdinaryReport_WithItsListsCached_CostsNoUpstreamRequest()
    {
        var (service, handler, requestBudget) = await CreateRealChainAsync(
            actorList: ListOf(ActorSevenTvId),
            editedList: ListOf(EditedSevenTvId, Set(EmoteSetId, EditedSevenTvId)));

        var result = await service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(0, requestBudget.Charges);
    }

    /// <summary>
    /// The one case that does reach 7TV — a set in none of the cached lists — does so exactly once,
    /// and pays for it from the shared budget like every other foreign read.
    /// </summary>
    [Fact]
    public async Task ASetInNoCachedList_CostsExactlyOneBudgetedRequest()
    {
        var (service, handler, requestBudget) = await CreateRealChainAsync(
            actorList: ListOf(ActorSevenTvId),
            editedList: ListOf(EditedSevenTvId));

        var result = await service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(EditedLogin, result.OwnerTwitchLogin);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(1, requestBudget.Charges);
    }

    /// <summary>
    /// The second-round finding (P1): repeated reports against an empty grant cache during a 7TV
    /// outage. Before the guard, each one cost an unbudgeted identity request (20 reports, 20
    /// requests, 0 permits). Now the first failure is held, and the rest send nothing — every report
    /// still answers 503, and none writes an entry.
    /// </summary>
    [Fact]
    public async Task RepeatedReports_DuringAnOutage_CostNoMoreThanTheHoldAllows()
    {
        var (service, handler, requestBudget, _) = CreateOutageChain();

        for (var report = 0; report < 20; report++)
        {
            Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, (await service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId)).Status);
        }

        Assert.Equal(1, handler.Requests);
        Assert.Equal(1, requestBudget.Charges);
    }

    [Fact]
    public async Task ARefusedPermitForTheGrants_IsUnavailable_WithoutAnUpstreamRequest()
    {
        var (service, handler, _, _) = CreateOutageChain(grantCount: 0);

        var result = await service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, result.Status);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task AnOpenBreakerForTheGrants_IsUnavailable_WithoutAnUpstreamRequest()
    {
        var (service, handler, requestBudget, breaker) = CreateOutageChain();
        var decision = breaker.TryAcquire(ForeignSevenTvBreakerOperations.EditorGrants);
        breaker.RecordFailure(
            ForeignSevenTvBreakerOperations.EditorGrants, ForeignSevenTvBreakerOutcome.OtherFailure, null, decision.Generation);
        for (var failure = 1; failure < ForeignSevenTvBreakerPolicy.FailureThreshold; failure++)
        {
            var next = breaker.TryAcquire(ForeignSevenTvBreakerOperations.EditorGrants);
            breaker.RecordFailure(
                ForeignSevenTvBreakerOperations.EditorGrants, ForeignSevenTvBreakerOutcome.OtherFailure, null, next.Generation);
        }

        var result = await service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, result.Status);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(0, requestBudget.Charges);
    }

    // The actor's list is readable and names the set under a stranger, so the owner check needs the
    // grants to say no — and needs no owner lookup either way. Grant cache empty; 7TV answers every
    // request with 503 (nothing is configured on the handler).
    private static (ImportTargetOwnershipService Service, SevenTvGqlRouteHandler Handler, RecordingForeignUpstreamRequestBudget RequestBudget, ForeignSevenTvBreakerPolicy Breaker)
        CreateOutageChain(int grantCount = int.MaxValue)
    {
        var handler = new SevenTvGqlRouteHandler();
        var requestBudget = new RecordingForeignUpstreamRequestBudget(grantCount);
        var client = new SevenTvApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") },
            new RecordingRateLimitTelemetry(),
            requestBudget,
            new RecordingLogger<SevenTvApiClient>());
        var breaker = new ForeignSevenTvBreakerPolicy();
        var budget = new ForeignEmoteSetProviderBudget();
        var grants = new GuardedSevenTvEditorGrantsService(
            client, Substitute.For<IModRoleCache>(), new InMemoryEditorGrantsHoldCache(), breaker, budget,
            new RecordingRateLimitTelemetry(), NullLogger<GuardedSevenTvEditorGrantsService>.Instance);
        var lists = ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId, Set(EmoteSetId, StrangerSevenTvId))));

        return (CreateService(lists, grants, client, breaker, budget), handler, requestBudget, breaker);
    }

    /// <summary>
    /// The second-round P2 at the level it was reported: an owner answer that carries only a GraphQL
    /// error is 503, not 404 — and it counts against the owner lookup's breaker as a failure, so a
    /// run of them opens it instead of each one "proving" 7TV healthy.
    /// </summary>
    [Fact]
    public async Task AnErrorsOnlyOwnerAnswer_IsUnavailable_AndCountsAsABreakerFailure()
    {
        var handler = new SevenTvGqlRouteHandler().Answer(
            SevenTvGqlRouteHandler.Owner, HttpStatusCode.OK, """{"data":null,"errors":[{"message":"internal server error"}]}""");
        var client = new SevenTvApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") },
            new RecordingRateLimitTelemetry(),
            new RecordingForeignUpstreamRequestBudget(),
            new RecordingLogger<SevenTvApiClient>());
        var breaker = new ForeignSevenTvBreakerPolicy();
        var service = CreateService(
            ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId))), EditorsReturning(Grants()), client, breaker);

        for (var report = 0; report < ForeignSevenTvBreakerPolicy.FailureThreshold; report++)
        {
            Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, (await service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId)).Status);
        }

        Assert.False(breaker.TryAcquire(ForeignSevenTvBreakerOperations.EmoteSetOwner).Allowed);
    }

    private static async Task<(ImportTargetOwnershipService Service, CountingOwnerHandler Handler, RecordingForeignUpstreamRequestBudget RequestBudget)>
        CreateRealChainAsync(EmoteSetListResult actorList, EmoteSetListResult editedList)
    {
        var handler = new CountingOwnerHandler(EditedSevenTvId);
        var requestBudget = new RecordingForeignUpstreamRequestBudget();
        var client = new SevenTvApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") },
            new RecordingRateLimitTelemetry(),
            requestBudget,
            new RecordingLogger<SevenTvApiClient>());

        var listCache = new InMemoryListCache();
        await listCache.SetAsync(ActorTwitchId, actorList, TimeSpan.FromSeconds(60));
        await listCache.SetAsync(EditedTwitchId, editedList, TimeSpan.FromSeconds(60));

        var grantsCache = Substitute.For<IModRoleCache>();
        grantsCache.TryGetSevenTvEditorGrantsAsync(ActorTwitchId, Arg.Any<CancellationToken>())
            .Returns(Grants((EditedLogin, EditedTwitchId)));

        var breaker = new ForeignSevenTvBreakerPolicy();
        var budget = new ForeignEmoteSetProviderBudget();
        var lists = new SevenTvEmoteSetListService(
            client, listCache, new ForeignEmoteSetRequestCoalescer<EmoteSetListResult>(), breaker, budget,
            NullLogger<SevenTvEmoteSetListService>.Instance);
        var editors = new GuardedSevenTvEditorGrantsService(
            client, grantsCache, new InMemoryEditorGrantsHoldCache(), breaker, budget, new RecordingRateLimitTelemetry(),
            NullLogger<GuardedSevenTvEditorGrantsService>.Instance);

        return (CreateService(lists, editors, client, breaker, budget), handler, requestBudget);
    }

    private static ImportTargetOwnershipService CreateService(
        ISevenTvEmoteSetListService lists,
        IGuardedSevenTvEditorGrantsService editors,
        ISevenTvApiClient client,
        ForeignSevenTvBreakerPolicy? breaker = null,
        ForeignEmoteSetProviderBudget? budget = null) => new(
        lists,
        editors,
        client,
        breaker ?? new ForeignSevenTvBreakerPolicy(),
        budget ?? new ForeignEmoteSetProviderBudget(),
        NullLogger<ImportTargetOwnershipService>.Instance);

    private static ISevenTvEmoteSetListService ListsReturning(params (string TwitchId, EmoteSetListResult Result)[] answers)
    {
        var lists = Substitute.For<ISevenTvEmoteSetListService>();
        lists.ListByTwitchIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(EmoteSetListResult.Failed(EmoteSetListStatus.NoSevenTvAccount));
        foreach (var (twitchId, result) in answers)
        {
            lists.ListByTwitchIdAsync(twitchId, Arg.Any<CancellationToken>()).Returns(result);
        }

        return lists;
    }

    private static IGuardedSevenTvEditorGrantsService EditorsReturning(SevenTvEditorGrants grants)
    {
        var editors = Substitute.For<IGuardedSevenTvEditorGrantsService>();
        editors.GetEditorGrantsAsync(ActorTwitchId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Ok(grants));
        return editors;
    }

    private static ISevenTvApiClient ClientAnsweringOwner(SevenTvEmoteSetOwnerLookupResult answer)
    {
        var client = Substitute.For<ISevenTvApiClient>();
        client.LookUpEmoteSetOwnerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(answer);
        return client;
    }

    private static SevenTvEditorGrants Grants(params (string Login, string TwitchId)[] grants) => new(
        new HashSet<string>(grants.Select(grant => grant.Login), StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(grants.Select(grant => grant.TwitchId), StringComparer.Ordinal),
        [.. grants.Select(grant => new SevenTvEditorGrantEntry(grant.Login, grant.TwitchId))]);

    private static EmoteSetListResult ListOf(string accountSevenTvId, params EmoteSetSummary[] sets) =>
        EmoteSetListResult.Ok(new EmoteSetList(null, sets, accountSevenTvId));

    private static EmoteSetSummary Set(string id, string ownerSevenTvId) =>
        new(id, "Some Set", 1000, "NORMAL", false, "Some Owner", ownerSevenTvId);

    private sealed class InMemoryListCache : ISevenTvEmoteSetListCache
    {
        private readonly Dictionary<string, EmoteSetListResult> _entries = new(StringComparer.Ordinal);

        public Task<EmoteSetListResult?> TryGetAsync(string twitchChannelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.TryGetValue(twitchChannelId, out var entry) ? entry : null);

        public Task SetAsync(
            string twitchChannelId, EmoteSetListResult result, TimeSpan timeToLive, CancellationToken cancellationToken = default)
        {
            _entries[twitchChannelId] = result;
            return Task.CompletedTask;
        }
    }

    /// <summary>Counts every request that leaves the client, and answers each as the v3 owner query would.</summary>
    private sealed class CountingOwnerHandler(string ownerSevenTvId) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":{\"emote_set\":{\"owner_id\":\"" + ownerSevenTvId + "\"}}}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
