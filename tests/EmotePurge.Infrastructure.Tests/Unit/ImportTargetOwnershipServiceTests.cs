using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
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
/// <para>
/// The real-chain cases run list service, editor service and 7TV client with only the caches and
/// the HTTP handler faked. They are the evidence for the Codex finding this class answers: a report
/// whose lists are cached costs no upstream request, and a set in no list costs exactly one,
/// charged against the shared budget.
/// </para>
/// <para>
/// The owner-hint cases run the same chain cold (empty list cache): with a hint on the owner — the
/// last of five grants — two list requests in one round trip, without one <c>1 + k</c>; the hint
/// never widens what is admissible, and the pre-check mode (<c>ResolveEditableAsync</c>) never
/// looks a set up.
/// </para>
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
    private const int GrantCount = 5;
    private const string NoSevenTvAccountJson = """{"data":{"users":{"userByConnection":null}}}""";

    private const string IdentityJson =
        """{"data":{"user_by_connection":{"id":"01ACTOR","connections":[{"platform":"TWITCH","id":"100","emote_set_id":null}]}}}""";

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
        Assert.Equal(ActorTwitchId, result.OwnerTwitchUserId);
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
        Assert.Equal(EditedTwitchId, result.OwnerTwitchUserId);
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
        Assert.Equal(EditedTwitchId, result.OwnerTwitchUserId);
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
        Assert.Equal(ActorTwitchId, result.OwnerTwitchUserId);
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
        Assert.Equal(EditedTwitchId, result.OwnerTwitchUserId);
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
        Assert.Equal(EditedTwitchId, result.OwnerTwitchUserId);
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

    // ---- The owner hint (the set-centric reports' and the pre-check's shared walk) ----------------
    //
    // The cases below run the real chain cold: list service, guarded grants service and 7TV client,
    // with an empty list cache and a handler that answers the v4 set list per platformId. The grant
    // cache is warm (five grants, the owner last) unless a case says otherwise.

    /// <summary>
    /// The core of the owner hint: a cold check whose owner is the last of five grants costs two
    /// list requests with a hint on that owner — the actor's own and the owner's, in flight together
    /// — and none of the other four.
    /// </summary>
    [Fact]
    public async Task AColdCheck_WithAHintOnTheLastOfFiveGrants_ReadsTwoListsInOneRoundTrip()
    {
        var chain = CreateColdChain(OwnerIsLastGrant());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chain.Handler.Gate = gate;

        SevenTvEmoteSetOwnershipCheckResult result;
        try
        {
            var check = chain.Service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: new EmoteSetOwnerHint(GrantTwitchId(GrantCount)));

            // Both requests reach the handler while neither has been answered: one round trip.
            await WaitUntilAsync(() => chain.Handler.CountOf(SevenTvGqlRouteHandler.List) == 2);
            Assert.False(check.IsCompleted);
            gate.SetResult();
            result = await check;
        }
        finally
        {
            gate.TrySetResult();
        }

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(GrantSevenTvId(GrantCount), result.OwnerSevenTvUserId);
        Assert.Equal(GrantLogin(GrantCount), result.OwnerTwitchLogin);
        Assert.Equal(GrantTwitchId(GrantCount), result.OwnerTwitchUserId);
        Assert.Equal(EmoteSetId, result.EmoteSet?.Id);
        Assert.Equal(EmoteSetId, result.SevenTvActiveEmoteSetId);
        Assert.Equal(2, chain.Handler.CountOf(SevenTvGqlRouteHandler.List));
        Assert.Equal(0, chain.Handler.CountOf(SevenTvGqlRouteHandler.Owner));
        Assert.Equal(2, chain.RequestBudget.Charges);
        Assert.Equal(new[] { ActorTwitchId, GrantTwitchId(GrantCount) }, chain.ListedTwitchIds.Order().ToArray());
    }

    /// <summary>The same check without a hint: today's walk, 1 + k lists, serially.</summary>
    [Fact]
    public async Task AColdCheck_WithoutAHint_WalksAllOnePlusKLists()
    {
        var chain = CreateColdChain(OwnerIsLastGrant());

        var result = await chain.Service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(GrantTwitchId(GrantCount), result.OwnerTwitchUserId);
        Assert.Equal(1 + GrantCount, chain.Handler.CountOf(SevenTvGqlRouteHandler.List));
        Assert.Equal(0, chain.Handler.CountOf(SevenTvGqlRouteHandler.Owner));
        Assert.Equal(AllAccountsInWalkOrder(), chain.ListedTwitchIds.ToArray());
    }

    /// <summary>
    /// A hint on an account that is neither the actor nor a grant — a revoked grant, a forged value —
    /// is dropped before any list is read: the same requests, in the same order, and the same answer
    /// as without a hint.
    /// </summary>
    [Fact]
    public async Task AForeignHint_IsDropped_WithTheSameRequestsAndResultAsWithoutOne()
    {
        var withoutHint = CreateColdChain(OwnerIsLastGrant());
        var withForeignHint = CreateColdChain(OwnerIsLastGrant());

        var expected = await withoutHint.Service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);
        var actual = await withForeignHint.Service.CheckAsync(
            ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: new EmoteSetOwnerHint("999", "stranger"));

        Assert.Equal(Describe(expected), Describe(actual));
        Assert.Equal(withoutHint.ListedTwitchIds.ToArray(), withForeignHint.ListedTwitchIds.ToArray());
        Assert.Equal(withoutHint.Handler.Requests, withForeignHint.Handler.Requests);
    }

    /// <summary>
    /// Codex finding 1 (high): the grant cache outlives the actor's 7TV connection by up to ten
    /// minutes. With the actor's own list saying "no 7TV account", a stale positive grant and a
    /// valid hint on it — even with the set right there in the hinted list, owned by that grant —
    /// must not widen anything: the answer is the one without a hint, the owner lookup runs exactly
    /// as often, and no other grant's list is read. The only extra cost is the hinted list itself,
    /// already in flight beside the own one when the own one comes back.
    /// </summary>
    [Fact]
    public async Task AStalePositiveGrant_WithAnActorWithoutASevenTvAccount_NeverWidensTheReport()
    {
        var answers = OwnerIsLastGrant();
        answers[ActorTwitchId] = NoSevenTvAccountJson;
        var withoutHint = CreateColdChain(answers, ownerLookupAnswer: GrantSevenTvId(GrantCount));
        var withHint = CreateColdChain(answers, ownerLookupAnswer: GrantSevenTvId(GrantCount));

        var expected = await withoutHint.Service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);
        var actual = await withHint.Service.CheckAsync(
            ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: new EmoteSetOwnerHint(GrantTwitchId(GrantCount)));

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Forbidden, expected.Status);
        Assert.Equal(Describe(expected), Describe(actual));
        Assert.Equal(1, withoutHint.Handler.CountOf(SevenTvGqlRouteHandler.Owner));
        Assert.Equal(1, withHint.Handler.CountOf(SevenTvGqlRouteHandler.Owner));
        Assert.Equal(new[] { ActorTwitchId }, withoutHint.ListedTwitchIds.ToArray());
        Assert.Equal(new[] { ActorTwitchId, GrantTwitchId(GrantCount) }, withHint.ListedTwitchIds.Order().ToArray());
    }

    /// <summary>The same guard in the pre-check's mode: "not found", and no owner lookup either way.</summary>
    [Fact]
    public async Task AStalePositiveGrant_WithAnActorWithoutASevenTvAccount_NeverWidensThePreCheck()
    {
        var answers = OwnerIsLastGrant();
        answers[ActorTwitchId] = NoSevenTvAccountJson;
        var withoutHint = CreateColdChain(answers, ownerLookupAnswer: GrantSevenTvId(GrantCount));
        var withHint = CreateColdChain(answers, ownerLookupAnswer: GrantSevenTvId(GrantCount));

        var expected = await withoutHint.Service.ResolveEditableAsync(ActorTwitchId, ActorLogin, EmoteSetId);
        var actual = await withHint.Service.ResolveEditableAsync(
            ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: new EmoteSetOwnerHint(GrantTwitchId(GrantCount)));

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.SetNotFound, expected.Status);
        Assert.Equal(Describe(expected), Describe(actual));
        Assert.Equal(0, withoutHint.Handler.CountOf(SevenTvGqlRouteHandler.Owner));
        Assert.Equal(0, withHint.Handler.CountOf(SevenTvGqlRouteHandler.Owner));
        Assert.Equal(new[] { ActorTwitchId }, withoutHint.ListedTwitchIds.ToArray());
        Assert.Equal(new[] { ActorTwitchId, GrantTwitchId(GrantCount) }, withHint.ListedTwitchIds.Order().ToArray());
    }

    /// <summary>
    /// An own list that could not be read is the partial outage it always was: a find in the hinted
    /// list is admissible, and without one the walk goes on and ends "unavailable", never "forbidden".
    /// </summary>
    [Fact]
    public async Task AnUnreadableOwnList_WithAValidGrantHint_IsAdmissibleOnAFind_AndUnavailableWithout()
    {
        var withFind = OwnerIsLastGrant();
        withFind.Remove(ActorTwitchId);
        var withoutFind = NoSetAnywhere();
        withoutFind.Remove(ActorTwitchId);
        var hint = new EmoteSetOwnerHint(GrantTwitchId(GrantCount));

        var found = await CreateColdChain(withFind).Service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: hint);
        var reported = await CreateColdChain(withoutFind, ownerLookupAnswer: StrangerSevenTvId).Service
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: hint);
        var preChecked = await CreateColdChain(withoutFind).Service
            .ResolveEditableAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: hint);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, found.Status);
        Assert.Equal(GrantTwitchId(GrantCount), found.OwnerTwitchUserId);
        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, reported.Status);
        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, preChecked.Status);
    }

    /// <summary>
    /// A hint on the actor walks exactly as today: the own list first, and with the set found there
    /// the grants are never asked — also when the actor's own account is among their grants.
    /// </summary>
    [Fact]
    public async Task AHintOnTheActor_WithTheSetInTheOwnList_NeverAsksForTheGrants()
    {
        var lists = ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId, Set(EmoteSetId, ActorSevenTvId))));
        var editors = EditorsReturning(Grants((ActorLogin, ActorTwitchId), (EditedLogin, EditedTwitchId)));

        var result = await CreateService(lists, editors, Substitute.For<ISevenTvApiClient>())
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: new EmoteSetOwnerHint(ActorTwitchId));

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(ActorTwitchId, result.OwnerTwitchUserId);
        await editors.DidNotReceive().GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await lists.Received(1).ListByTwitchIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The same by login, cold: one list request, and the grant cache is never touched.</summary>
    [Fact]
    public async Task ALoginHintOnTheActor_Cold_CostsOneListRequest()
    {
        var answers = NoSetAnywhere();
        answers[ActorTwitchId] = ListJson(ActorSevenTvId, null, new ListedSet(EmoteSetId, ActorSevenTvId));
        var chain = CreateColdChain(answers);

        var result = await chain.Service.CheckAsync(
            ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: new EmoteSetOwnerHint(TwitchLogin: "  Actor "));

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(1, chain.Handler.CountOf(SevenTvGqlRouteHandler.List));
        await chain.GrantsCache.DidNotReceive().TryGetSevenTvEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A hinted list that cannot be read is noted as unreadable and the walk goes on: a find further
    /// down is admissible; with none, the outcome is "unavailable" — the unread list may have been
    /// the owner's.
    /// </summary>
    [Fact]
    public async Task AnUnreadableHintedList_DoesNotEndTheWalk()
    {
        var foundLater = NoSetAnywhere();
        foundLater.Remove(GrantTwitchId(1));
        foundLater[GrantTwitchId(3)] = ListJson(GrantSevenTvId(3), null, new ListedSet(EmoteSetId, GrantSevenTvId(3)));
        var nowhere = NoSetAnywhere();
        nowhere.Remove(GrantTwitchId(1));
        var hint = new EmoteSetOwnerHint(GrantTwitchId(1));

        var chain = CreateColdChain(foundLater);
        var found = await chain.Service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: hint);
        var reported = await CreateColdChain(nowhere, ownerLookupAnswer: StrangerSevenTvId).Service
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: hint);
        var preChecked = await CreateColdChain(nowhere).Service
            .ResolveEditableAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: hint);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, found.Status);
        Assert.Equal(GrantTwitchId(3), found.OwnerTwitchUserId);
        Assert.Equal(4, chain.Handler.CountOf(SevenTvGqlRouteHandler.List));
        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, reported.Status);
        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, preChecked.Status);
    }

    /// <summary>
    /// Owner changed hands: the hinted list carries the set, but under another checked account.
    /// No early "forbidden" — the walk goes on and finds the owner later, under the owner's identity.
    /// </summary>
    [Fact]
    public async Task ASetInTheHintedList_OwnedByAnotherCheckedAccount_IsThatAccounts_WithoutAnEarlyForbidden()
    {
        var answers = NoSetAnywhere();
        answers[GrantTwitchId(2)] = ListJson(GrantSevenTvId(2), null, new ListedSet(EmoteSetId, GrantSevenTvId(4)));
        var chain = CreateColdChain(answers);

        var result = await chain.Service.CheckAsync(
            ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: new EmoteSetOwnerHint(GrantTwitchId(2)));

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(GrantSevenTvId(4), result.OwnerSevenTvUserId);
        Assert.Equal(GrantLogin(4), result.OwnerTwitchLogin);
        Assert.Equal(GrantTwitchId(4), result.OwnerTwitchUserId);
        // The owner's own list did not carry the set: the set comes from the listing account, the
        // active flag from nobody.
        Assert.Equal(EmoteSetId, result.EmoteSet?.Id);
        Assert.Null(result.SevenTvActiveEmoteSetId);
        Assert.Equal(0, chain.Handler.CountOf(SevenTvGqlRouteHandler.Owner));
    }

    /// <summary>
    /// Listed in the hinted list under a stranger and nowhere else: "forbidden", but only after the
    /// whole walk — any later account might have been that stranger — and without a lookup.
    /// </summary>
    [Fact]
    public async Task ASetInTheHintedList_UnderAForeignOwner_IsForbiddenOnlyAfterTheFullWalk()
    {
        var answers = NoSetAnywhere();
        answers[GrantTwitchId(2)] = ListJson(GrantSevenTvId(2), null, new ListedSet(EmoteSetId, StrangerSevenTvId));
        var hint = new EmoteSetOwnerHint(GrantTwitchId(2));
        var reportChain = CreateColdChain(answers);

        var reported = await reportChain.Service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: hint);
        var preChecked = await CreateColdChain(answers).Service.ResolveEditableAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: hint);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Forbidden, reported.Status);
        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Forbidden, preChecked.Status);
        Assert.Equal(1 + GrantCount, reportChain.Handler.CountOf(SevenTvGqlRouteHandler.List));
        Assert.Equal(0, reportChain.Handler.CountOf(SevenTvGqlRouteHandler.Owner));
    }

    /// <summary>
    /// The hint takes no way around the guards: with the list breaker open, no list request leaves
    /// the process, hinted or not, and the answer is "unavailable" — fail-closed.
    /// </summary>
    [Fact]
    public async Task AnOpenListBreaker_IsUnavailable_WithoutAListRequest_WithOrWithoutAHint()
    {
        var hint = new EmoteSetOwnerHint(GrantTwitchId(GrantCount));
        foreach (var ownerHint in new[] { (EmoteSetOwnerHint?)null, hint })
        {
            var preCheckChain = CreateColdChain(OwnerIsLastGrant(), breaker: BreakerWithOpenListOperation());
            var reportChain = CreateColdChain(OwnerIsLastGrant(), breaker: BreakerWithOpenListOperation());

            var preChecked = await preCheckChain.Service.ResolveEditableAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: ownerHint);
            var reported = await reportChain.Service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: ownerHint);

            Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, preChecked.Status);
            Assert.Equal(0, preCheckChain.Handler.Requests);
            // The report's owner lookup has a breaker operation of its own; its 503 keeps the
            // answer "unavailable". The lists stay closed either way.
            Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, reported.Status);
            Assert.Equal(0, reportChain.Handler.CountOf(SevenTvGqlRouteHandler.List));
        }
    }

    [Fact]
    public async Task ALoginHint_MatchingAGrantInAnyCase_ResolvesToThatGrantsId()
    {
        var chain = CreateColdChain(OwnerIsLastGrant());

        var result = await chain.Service.CheckAsync(
            ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: new EmoteSetOwnerHint(TwitchLogin: $" {GrantLogin(GrantCount).ToUpperInvariant()} "));

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(GrantTwitchId(GrantCount), result.OwnerTwitchUserId);
        Assert.Equal(new[] { ActorTwitchId, GrantTwitchId(GrantCount) }, chain.ListedTwitchIds.Order().ToArray());
    }

    /// <summary>A renamed login matches no grant: dropped, and the walk is today's.</summary>
    [Fact]
    public async Task ALoginHint_MatchingNoGrant_IsDropped()
    {
        var chain = CreateColdChain(OwnerIsLastGrant());

        var result = await chain.Service.CheckAsync(
            ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: new EmoteSetOwnerHint(TwitchLogin: "renamedchannel"));

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(AllAccountsInWalkOrder(), chain.ListedTwitchIds.ToArray());
    }

    /// <summary>The id wins: a valid login beside it is never consulted, whether the id is valid or not.</summary>
    [Fact]
    public async Task AnIdHint_WinsOverALoginHint()
    {
        var validId = CreateColdChain(OwnerIsLastGrant());
        var foreignId = CreateColdChain(OwnerIsLastGrant());

        await validId.Service.CheckAsync(
            ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: new EmoteSetOwnerHint(GrantTwitchId(GrantCount), GrantLogin(1)));
        await foreignId.Service.CheckAsync(
            ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: new EmoteSetOwnerHint("999", GrantLogin(GrantCount)));

        Assert.Equal(new[] { ActorTwitchId, GrantTwitchId(GrantCount) }, validId.ListedTwitchIds.Order().ToArray());
        Assert.Equal(AllAccountsInWalkOrder(), foreignId.ListedTwitchIds.ToArray());
    }

    /// <summary>
    /// The pre-check never asks 7TV about a set directly (F16: never looser than the list rule): a set
    /// in no list is "not found", where the report would have looked it up.
    /// </summary>
    [Fact]
    public async Task ThePreCheck_ASetInNoList_IsNotFound_WithoutAnOwnerLookup()
    {
        var client = ClientAnsweringOwner(SevenTvEmoteSetOwnerLookupResult.Ok(ActorSevenTvId));

        var result = await CreateService(ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId))), EditorsReturning(Grants()), client)
            .ResolveEditableAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.SetNotFound, result.Status);
        await client.DidNotReceive().LookUpEmoteSetOwnerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A set listed without an owner id is not editable on the list (F16) — "forbidden", no lookup.</summary>
    [Fact]
    public async Task ThePreCheck_ASetListedWithoutAnOwnerId_IsForbidden()
    {
        var client = ClientAnsweringOwner(SevenTvEmoteSetOwnerLookupResult.Ok(ActorSevenTvId));
        var lists = ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId, Set(EmoteSetId, null))));

        var result = await CreateService(lists, EditorsReturning(Grants()), client)
            .ResolveEditableAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Forbidden, result.Status);
        await client.DidNotReceive().LookUpEmoteSetOwnerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A held grant failure makes the pre-check "unavailable" — stricter than the picker's list, never looser.</summary>
    [Fact]
    public async Task ThePreCheck_WithUnreadableGrants_AndNoFind_IsUnavailable()
    {
        var editors = Substitute.For<IGuardedSevenTvEditorGrantsService>();
        editors.GetEditorGrantsAsync(ActorTwitchId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Failed(SevenTvLookupStatus.Unavailable));

        var result = await CreateService(ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId))), editors, Substitute.For<ISevenTvApiClient>())
            .ResolveEditableAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Unavailable, result.Status);
    }

    /// <summary>
    /// What the pre-check route needs besides the verdict: the set as the list named it (whatever its
    /// kind — telling PERSONAL apart is the route's job) and the owner list's active set.
    /// </summary>
    [Fact]
    public async Task ThePreCheck_CarriesTheSetAndTheOwnersActiveSet()
    {
        var set = new EmoteSetSummary(EmoteSetId, "Personal Emotes", 5, "PERSONAL", true, "Actor Display", ActorSevenTvId);
        var lists = ListsReturning((ActorTwitchId, EmoteSetListResult.Ok(new EmoteSetList(EmoteSetId, [set], ActorSevenTvId))));

        var result = await CreateService(lists, EditorsReturning(Grants()), Substitute.For<ISevenTvApiClient>())
            .ResolveEditableAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(ActorLogin, result.OwnerTwitchLogin);
        Assert.Equal(set, result.EmoteSet);
        Assert.Equal(EmoteSetId, result.SevenTvActiveEmoteSetId);
    }

    /// <summary>The report's owner-lookup fallback knows no list entry, so it carries none.</summary>
    [Fact]
    public async Task TheReportsLookupFallback_CarriesNoSet()
    {
        var client = ClientAnsweringOwner(SevenTvEmoteSetOwnerLookupResult.Ok(ActorSevenTvId));

        var result = await CreateService(ListsReturning((ActorTwitchId, ListOf(ActorSevenTvId))), EditorsReturning(Grants()), client)
            .CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Null(result.EmoteSet);
        Assert.Null(result.SevenTvActiveEmoteSetId);
    }

    /// <summary>
    /// A cold grant cache is the one case the hint cannot skip: the grants vouch for it, at two
    /// budgeted requests (identity and editor_of), before the two lists — four permits in all.
    /// </summary>
    [Fact]
    public async Task AColdGrantCache_WithAHintOnAGrant_CostsTwoGrantRequestsAndTwoListRequests()
    {
        var chain = CreateColdChain(OwnerIsLastGrant(), grantsCached: false);
        chain.Handler
            .Answer(SevenTvGqlRouteHandler.Identity, HttpStatusCode.OK, IdentityJson)
            .Answer(SevenTvGqlRouteHandler.EditorOf, HttpStatusCode.OK, EditorOfJson());

        var result = await chain.Service.CheckAsync(
            ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: new EmoteSetOwnerHint(GrantTwitchId(GrantCount)));

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(GrantTwitchId(GrantCount), result.OwnerTwitchUserId);
        Assert.Equal(1, chain.Handler.CountOf(SevenTvGqlRouteHandler.Identity));
        Assert.Equal(1, chain.Handler.CountOf(SevenTvGqlRouteHandler.EditorOf));
        Assert.Equal(2, chain.Handler.CountOf(SevenTvGqlRouteHandler.List));
        Assert.Equal(4, chain.RequestBudget.Charges);
    }

    /// <summary>
    /// Codex finding 2: listed under grant A, owned by grant B. The hint is B (the client resolves
    /// the owner, never the listing account) and costs two lists; both modes name B — its login, its
    /// Twitch id, its own list's active set — and never A. Without a hint the walk reaches B too.
    /// </summary>
    [Fact]
    public async Task ASetListedUnderGrantA_OwnedByGrantB_IsBs_InBothModes()
    {
        var answers = NoSetAnywhere();
        answers[GrantTwitchId(2)] = ListJson(GrantSevenTvId(2), null, new ListedSet(EmoteSetId, GrantSevenTvId(4)));
        answers[GrantTwitchId(4)] = ListJson(GrantSevenTvId(4), EmoteSetId, new ListedSet(EmoteSetId, GrantSevenTvId(4)));
        var hintOnB = new EmoteSetOwnerHint(GrantTwitchId(4));
        var reportChain = CreateColdChain(answers);

        var reported = await reportChain.Service.CheckAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: hintOnB);
        var preChecked = await CreateColdChain(answers).Service.ResolveEditableAsync(ActorTwitchId, ActorLogin, EmoteSetId, ownerHint: hintOnB);
        var preCheckedWithoutHint = await CreateColdChain(answers).Service.ResolveEditableAsync(ActorTwitchId, ActorLogin, EmoteSetId);

        Assert.Equal(new[] { ActorTwitchId, GrantTwitchId(4) }, reportChain.ListedTwitchIds.Order().ToArray());
        foreach (var result in new[] { reported, preChecked, preCheckedWithoutHint })
        {
            Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
            Assert.Equal(GrantSevenTvId(4), result.OwnerSevenTvUserId);
            Assert.Equal(GrantLogin(4), result.OwnerTwitchLogin);
            Assert.Equal(GrantTwitchId(4), result.OwnerTwitchUserId);
            Assert.Equal(EmoteSetId, result.SevenTvActiveEmoteSetId);
        }
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

    private static EmoteSetSummary Set(string id, string? ownerSevenTvId) =>
        new(id, "Some Set", 1000, "NORMAL", false, "Some Owner", ownerSevenTvId);

    // The cold chain of the owner-hint cases: the real list service with an empty cache, the real
    // guarded grants service over a grant cache that holds the five grants (or nothing), and one
    // handler answering the v4 set list per platformId — an id without an answer gets a 503.
    private static ColdChain CreateColdChain(
        IReadOnlyDictionary<string, string> listAnswers,
        bool grantsCached = true,
        string? ownerLookupAnswer = null,
        ForeignSevenTvBreakerPolicy? breaker = null)
    {
        var listedTwitchIds = new ConcurrentQueue<string>();
        var handler = new SevenTvGqlRouteHandler().Answer(SevenTvGqlRouteHandler.List, body =>
        {
            var twitchId = PlatformIdOf(body);
            listedTwitchIds.Enqueue(twitchId);
            return listAnswers.TryGetValue(twitchId, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(string.Empty) };
        });
        if (ownerLookupAnswer is not null)
        {
            handler.Answer(
                SevenTvGqlRouteHandler.Owner, HttpStatusCode.OK, "{\"data\":{\"emote_set\":{\"owner_id\":\"" + ownerLookupAnswer + "\"}}}");
        }

        var requestBudget = new RecordingForeignUpstreamRequestBudget();
        var client = new SevenTvApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") },
            new RecordingRateLimitTelemetry(),
            requestBudget,
            new RecordingLogger<SevenTvApiClient>());

        var grantsCache = Substitute.For<IModRoleCache>();
        if (grantsCached)
        {
            grantsCache.TryGetSevenTvEditorGrantsAsync(ActorTwitchId, Arg.Any<CancellationToken>()).Returns(FiveGrants());
        }

        breaker ??= new ForeignSevenTvBreakerPolicy();
        var budget = new ForeignEmoteSetProviderBudget();
        var lists = new SevenTvEmoteSetListService(
            client, new InMemoryListCache(), new ForeignEmoteSetRequestCoalescer<EmoteSetListResult>(), breaker, budget,
            NullLogger<SevenTvEmoteSetListService>.Instance);
        var editors = new GuardedSevenTvEditorGrantsService(
            client, grantsCache, new InMemoryEditorGrantsHoldCache(), breaker, budget, new RecordingRateLimitTelemetry(),
            NullLogger<GuardedSevenTvEditorGrantsService>.Instance);

        return new ColdChain(CreateService(lists, editors, client, breaker, budget), handler, requestBudget, grantsCache, listedTwitchIds);
    }

    private static ForeignSevenTvBreakerPolicy BreakerWithOpenListOperation()
    {
        var breaker = new ForeignSevenTvBreakerPolicy();
        for (var failure = 0; failure < ForeignSevenTvBreakerPolicy.FailureThreshold; failure++)
        {
            var decision = breaker.TryAcquire(ForeignSevenTvBreakerOperations.EmoteSetList);
            breaker.RecordFailure(
                ForeignSevenTvBreakerOperations.EmoteSetList, ForeignSevenTvBreakerOutcome.OtherFailure, null, decision.Generation);
        }

        return breaker;
    }

    /// <summary>Every account's list without the set; the owner-hint cases put it where they need it.</summary>
    private static Dictionary<string, string> NoSetAnywhere()
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal) { [ActorTwitchId] = ListJson(ActorSevenTvId, null) };
        for (var grant = 1; grant <= GrantCount; grant++)
        {
            answers[GrantTwitchId(grant)] = ListJson(GrantSevenTvId(grant), null);
        }

        return answers;
    }

    /// <summary>The set in the last grant's list only, owned by that grant and active there.</summary>
    private static Dictionary<string, string> OwnerIsLastGrant()
    {
        var answers = NoSetAnywhere();
        answers[GrantTwitchId(GrantCount)] = ListJson(
            GrantSevenTvId(GrantCount), EmoteSetId, new ListedSet(EmoteSetId, GrantSevenTvId(GrantCount)));
        return answers;
    }

    private static string[] AllAccountsInWalkOrder() =>
        [ActorTwitchId, .. Enumerable.Range(1, GrantCount).Select(GrantTwitchId)];

    private static SevenTvEditorGrants FiveGrants() =>
        Grants([.. Enumerable.Range(1, GrantCount).Select(grant => (GrantLogin(grant), GrantTwitchId(grant)))]);

    private static string GrantTwitchId(int grant) => $"20{grant}";

    private static string GrantLogin(int grant) => $"grant{grant}";

    private static string GrantSevenTvId(int grant) => $"01GRANT{grant}";

    // The v4 answer shape of SevenTvApiClient's set-list query (measured 2026-09-20).
    private static string ListJson(string accountSevenTvId, string? activeEmoteSetId, params ListedSet[] sets) =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                users = new
                {
                    userByConnection = new
                    {
                        id = accountSevenTvId,
                        style = new { activeEmoteSetId },
                        emoteSets = sets.Select(set => new
                        {
                            id = set.Id,
                            name = "Some Set",
                            capacity = 1000,
                            kind = "NORMAL",
                            owner = set.OwnerSevenTvId is null
                                ? null
                                : new { id = set.OwnerSevenTvId, mainConnection = new { platformDisplayName = "Some Owner" } },
                        }),
                    },
                },
            },
        });

    private static string EditorOfJson() =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                user = new
                {
                    editor_of = Enumerable.Range(1, GrantCount).Select(grant => new
                    {
                        user = new { connections = new[] { new { platform = "TWITCH", id = GrantTwitchId(grant), username = GrantLogin(grant) } } },
                    }),
                },
            },
        });

    private static string PlatformIdOf(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        return document.RootElement.GetProperty("variables").GetProperty("pid").GetString()!;
    }

    private static (SevenTvEmoteSetOwnershipStatus, string?, string?, string?, EmoteSetSummary?, string?) Describe(
        SevenTvEmoteSetOwnershipCheckResult result) =>
        (result.Status, result.OwnerSevenTvUserId, result.OwnerTwitchLogin, result.OwnerTwitchUserId, result.EmoteSet, result.SevenTvActiveEmoteSetId);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached within 10 s.");
            await Task.Delay(10);
        }
    }

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

    private sealed record ColdChain(
        ImportTargetOwnershipService Service,
        SevenTvGqlRouteHandler Handler,
        RecordingForeignUpstreamRequestBudget RequestBudget,
        IModRoleCache GrantsCache,
        ConcurrentQueue<string> ListedTwitchIds);

    private sealed record ListedSet(string Id, string? OwnerSevenTvId);
}
