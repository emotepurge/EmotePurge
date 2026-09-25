using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The ladder of <c>POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted</c> and
/// <c>…/sync-restored</c> (restore-per-set spec 5.1, AK 9, 14, 26–28): middleware (401) -&gt;
/// <c>EmoteSetIdValidationFilter</c> (400) -&gt; body (400) -&gt; the owner check (404/403 bare/503)
/// -&gt; the service call -&gt; one <c>channel.synced</c> per changed channel -&gt; one guarded resync
/// per hit channel and per <c>activeSetDiffers</c> mismatch -&gt; 200. Every case runs against both
/// routes: they share one ladder and differ only in the service method and the count's wire name.
/// </summary>
/// <remarks>
/// The owner check runs for real, as in <see cref="SevenTvEmoteSetSyncImportedEndpointTests"/>: each
/// case arranges the set lists and grants so the check produces its status, rather than stubbing it.
/// </remarks>
public class SevenTvEmoteSetSyncBookkeepingEndpointTests : IClassFixture<ApiFactory>
{
    private const string Deleted = "sync-deleted";
    private const string Restored = "sync-restored";
    private const string EmoteSetId = "01GV88A38G0006FW5TVZVMG508";
    private const string ActorSevenTvUserId = "actor-seven-tv-id";
    private const string OwnerSevenTvUserId = "owner-seven-tv-id";
    private const string OwnerTwitchId = "49140130";
    private const string OwnerTwitchLogin = "handofblood";

    private readonly ApiFactory _factory;

    public SevenTvEmoteSetSyncBookkeepingEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.GuardedEditorGrants.ClearReceivedCalls();
        _factory.EmoteSetList.ClearReceivedCalls();
        _factory.SevenTvApi.ClearReceivedCalls();
        _factory.Emotes.ClearReceivedCalls();
        _factory.RedisPublisher.ClearReceivedCalls();
        _factory.ResyncCooldown.ClearReceivedCalls();
        _factory.Channels.ClearReceivedCalls();
    }

    [Theory]
    [InlineData(Deleted)]
    [InlineData(Restored)]
    public async Task AnonymousCaller_Gets401(string route)
    {
        var response = await SendAsync(route, EmoteSetId, userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertOwnerCheckDidNotRunAsync();
        await AssertNoReportAsync();
    }

    [Theory]
    [InlineData(Deleted)]
    [InlineData(Restored)]
    public async Task MalformedRouteEmoteSetId_Gets400_BeforeAnyServiceCall(string route)
    {
        // 33 characters, one over the filter's limit — see the sync-imported twin for why not "../x".
        var response = await SendAsync(route, new string('a', 33), NewUserId());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidEmoteSetId, await ReadErrorCodeAsync(response));
        await AssertOwnerCheckDidNotRunAsync();
        await AssertNoReportAsync();
    }

    [Theory]
    [InlineData(Deleted, """{"sevenTvEmoteIds": [], "expectedChannelName": null}""")]
    [InlineData(Restored, """{"sevenTvEmoteIds": [], "expectedChannelName": null}""")]
    [InlineData(Deleted, """{"expectedChannelName": null}""")]
    [InlineData(Restored, """{"expectedChannelName": null}""")]
    public async Task EmptyOrMissingEmoteIds_Gets400EmoteIdsEmpty_BeforeTheOwnerCheckRuns(string route, string body)
    {
        var response = await SendAsync(route, EmoteSetId, NewUserId(), body: body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.EmoteIdsEmpty, await ReadErrorCodeAsync(response));
        await AssertOwnerCheckDidNotRunAsync();
        await AssertNoReportAsync();
    }

    [Theory]
    [InlineData(Deleted)]
    [InlineData(Restored)]
    public async Task InvalidExpectedChannelName_Gets400InvalidChannelName_BeforeTheOwnerCheckRuns(string route)
    {
        var response = await SendAsync(
            route, EmoteSetId, NewUserId(), body: """{"sevenTvEmoteIds": ["7tv-x1"], "expectedChannelName": "not a channel!"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidChannelName, await ReadErrorCodeAsync(response));
        await AssertOwnerCheckDidNotRunAsync();
        await AssertNoReportAsync();
    }

    [Theory]
    [InlineData(Deleted)]
    [InlineData(Restored)]
    public async Task UnknownSet_Gets404EmoteSetNotFound_WithoutReportOrResync(string route)
    {
        var userId = NewUserId();
        ArrangeActorWithoutGrants(userId, SetList(ActorSevenTvUserId));
        _factory.SevenTvApi.LookUpEmoteSetOwnerAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetOwnerLookupResult.Failed(SevenTvEmoteSetOwnerLookupStatus.NotFound));

        var response = await SendAsync(route, EmoteSetId, userId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.EmoteSetNotFound, await ReadErrorCodeAsync(response));
        await AssertNoReportAsync();
    }

    [Theory]
    [InlineData(Deleted)]
    [InlineData(Restored)]
    public async Task NeitherOwnerNorEditor_GetsBareForbid_WithoutReportOrResync(string route)
    {
        var userId = NewUserId();
        ArrangeActorWithoutGrants(userId, SetList(ActorSevenTvUserId, (EmoteSetId, OwnerSevenTvUserId)));

        var response = await SendAsync(route, EmoteSetId, userId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(string.IsNullOrEmpty(responseBody), $"Expected an empty 403 body, got: {responseBody}");
        await AssertNoReportAsync();
    }

    [Theory]
    [InlineData(Deleted)]
    [InlineData(Restored)]
    public async Task SevenTvUnavailable_Gets503_WithoutReportOrResync(string route)
    {
        var userId = NewUserId();
        ArrangeActorWithoutGrants(userId, SetList(ActorSevenTvUserId));
        _factory.SevenTvApi.LookUpEmoteSetOwnerAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetOwnerLookupResult.Failed(SevenTvEmoteSetOwnerLookupStatus.Unavailable));

        var response = await SendAsync(route, EmoteSetId, userId);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ForeignChannelSevenTvUnavailable, await ReadErrorCodeAsync(response));
        await AssertNoReportAsync();
    }

    [Theory]
    [InlineData(Deleted, "archivedCount")]
    [InlineData(Restored, "restoredCount")]
    public async Task ConfirmedOwnership_Gets200_ForwardsOwnerAndNormalizedExpectedChannel_AndAnswersTheWireShape(string route, string countField)
    {
        var userId = NewUserId();
        ArrangeConfirmedOwnership(userId);
        ArrangeReport(route, new InSetOutcome(
            2,
            [new SyncInSetChannelResultDto("shapechan", 1, 0, ["7tv-x2"])],
            new UnresolvedChannelDto("shapeexpected", UnresolvedChannelReasons.NotTracked)));
        ArrangeCooldown("shapechan", acquired: true);
        _factory.Channels.TriggerResyncAsync("shapechan", Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChannelResyncResult.Triggered);

        var response = await SendAsync(
            route, EmoteSetId, userId, body: """{"sevenTvEmoteIds": ["7tv-x1", "7tv-x2"], "expectedChannelName": "ShapeExpected"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertReportReceivedAsync(route, "shapeexpected");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("reportedCount").GetInt32());
        var channel = Assert.Single(root.GetProperty("channels").EnumerateArray());
        Assert.Equal("shapechan", channel.GetProperty("channelName").GetString());
        Assert.Equal(1, channel.GetProperty(countField).GetInt32());
        Assert.Equal(["7tv-x2"], channel.GetProperty("notFoundIds").EnumerateArray().Select(id => id.GetString()));
        // NewlyChangedCount only steers the live event (spec 5.3) — it never goes on the wire.
        Assert.False(channel.TryGetProperty("newlyChangedCount", out _));
        Assert.Equal("shapeexpected", root.GetProperty("unresolvedChannel").GetProperty("channelName").GetString());
        Assert.Equal("notTracked", root.GetProperty("unresolvedChannel").GetProperty("reason").GetString());
        Assert.Equal(["shapechan"], root.GetProperty("resyncTriggered").EnumerateArray().Select(c => c.GetString()));
    }

    [Theory]
    [InlineData(Deleted)]
    [InlineData(Restored)]
    public async Task ActorOwnsTheSet_ForwardsTheActorsOwnTwitchIdAsTheOwners(string route)
    {
        // N3 (5.1 stage 4/5): the owner's Twitch id reaches the service on the own-account path too —
        // there it is the actor's, since the actor is the owner. The grant path is pinned above.
        var userId = NewUserId();
        ArrangeActorWithoutGrants(userId, SetList(ActorSevenTvUserId, (EmoteSetId, ActorSevenTvUserId)));
        ArrangeReport(route, new InSetOutcome(2, [], null));

        var response = await SendAsync(route, EmoteSetId, userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (route == Deleted)
        {
            await _factory.Emotes.Received(1).MarkDeletedInSetAsync(
                EmoteSetId, ActorSevenTvUserId, "someuser", userId, Arg.Any<IReadOnlyList<string>>(),
                null, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await _factory.Emotes.Received(1).MarkRestoredInSetAsync(
                EmoteSetId, ActorSevenTvUserId, "someuser", userId, Arg.Any<IReadOnlyList<string>>(),
                null, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(Deleted)]
    [InlineData(Restored)]
    public async Task PaperOnlyAnswer_HasEmptyChannels_NullUnresolvedChannel_AndNoLiveEventOrResync(string route)
    {
        var userId = NewUserId();
        ArrangeConfirmedOwnership(userId);
        ArrangeReport(route, new InSetOutcome(1, [], null));

        var response = await SendAsync(route, EmoteSetId, userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(json.RootElement.GetProperty("channels").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("unresolvedChannel").ValueKind);
        Assert.Empty(json.RootElement.GetProperty("resyncTriggered").EnumerateArray());
        await _factory.RedisPublisher.DidNotReceive().PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.ResyncCooldown.DidNotReceive().TryBeginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(Deleted)]
    [InlineData(Restored)]
    public async Task LiveEvent_IsPublishedOncePerChannelWithChangedRows_AndNeverForAnUnchangedOne(string route)
    {
        // AK 14: "livechanged" really changed rows; "livesame" only reached a goal state the live
        // sync had already written — no event for it.
        var userId = NewUserId();
        ArrangeConfirmedOwnership(userId);
        ArrangeReport(route, new InSetOutcome(
            1,
            [
                new SyncInSetChannelResultDto("livechanged", 1, 1, []),
                new SyncInSetChannelResultDto("livesame", 1, 0, []),
            ],
            null));

        var response = await SendAsync(route, EmoteSetId, userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.RedisPublisher.Received(1).PublishAsync(
            LiveEvents.Channel,
            new LiveEvent(LiveEvents.ChannelSynced, "livechanged").Serialize(),
            Arg.Any<CancellationToken>());
        await _factory.RedisPublisher.Received(1).PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(Deleted)]
    [InlineData(Restored)]
    public async Task Resync_RunsOncePerHitChannel_AndForAnActiveSetDiffersMismatch_ResyncTriggeredNamesExactlyTheTriggeredOnes(string route)
    {
        // AK 26/28 and F15 in one answer: two hits and a lagging expected channel each claim the
        // cooldown once; "rsbusy" does not get it (a resync ran within 60 s — no error, no second
        // resync); "rsgone" is claimed but no longer active, so its slot is handed back.
        var userId = NewUserId();
        ArrangeConfirmedOwnership(userId);
        ArrangeReport(route, new InSetOutcome(
            1,
            [
                new SyncInSetChannelResultDto("rsbusy", 1, 0, []),
                new SyncInSetChannelResultDto("rsgone", 1, 0, []),
                new SyncInSetChannelResultDto("rshit", 0, 0, ["7tv-x1"]),
            ],
            new UnresolvedChannelDto("rslagging", UnresolvedChannelReasons.ActiveSetDiffers)));
        ArrangeCooldown("rsbusy", acquired: false);
        ArrangeCooldown("rsgone", acquired: true);
        ArrangeCooldown("rshit", acquired: true);
        ArrangeCooldown("rslagging", acquired: true);
        _factory.Channels.TriggerResyncAsync("rsgone", Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChannelResyncResult.NotActive);
        _factory.Channels.TriggerResyncAsync("rshit", Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChannelResyncResult.Triggered);
        _factory.Channels.TriggerResyncAsync("rslagging", Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChannelResyncResult.Triggered);

        var response = await SendAsync(route, EmoteSetId, userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["rshit", "rslagging"], json.RootElement.GetProperty("resyncTriggered").EnumerateArray().Select(c => c.GetString()));

        foreach (var channel in new[] { "rsbusy", "rsgone", "rshit", "rslagging" })
        {
            await _factory.ResyncCooldown.Received(1).TryBeginAsync(channel, Arg.Any<CancellationToken>());
        }

        await _factory.ResyncCooldown.Received(4).TryBeginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.Channels.DidNotReceive().TriggerResyncAsync("rsbusy", Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
        // The resync runs under the reporter's own account (spec 5.1, stage 7).
        await _factory.Channels.Received(1).TriggerResyncAsync(
            "rshit", Arg.Is<AuditActor>(actor => actor.TwitchUserId == userId), Arg.Any<CancellationToken>());
        await _factory.ResyncCooldown.Received(1).ReleaseAsync("rsgone", Arg.Any<CancellationToken>());
        await _factory.ResyncCooldown.Received(1).ReleaseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(Deleted)]
    [InlineData(Restored)]
    public async Task Resync_StillReturns200_WhenTriggerResyncThrows_AndReleasesTheCooldown(string route)
    {
        // Stage 7 claims the cooldown before calling TriggerResyncAsync — a throw there must not
        // leave the slot claimed forever, and must not turn an already-committed report into a 500.
        var userId = NewUserId();
        ArrangeConfirmedOwnership(userId);
        ArrangeReport(route, new InSetOutcome(1, [new SyncInSetChannelResultDto("rsthrows", 1, 0, [])], null));
        ArrangeCooldown("rsthrows", acquired: true);
        _factory.Channels.TriggerResyncAsync("rsthrows", Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChannelResyncResult>>(_ => throw new InvalidOperationException("Provoked for the cooldown-release regression test."));

        var response = await SendAsync(route, EmoteSetId, userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(json.RootElement.GetProperty("resyncTriggered").EnumerateArray());
        await _factory.ResyncCooldown.Received(1).ReleaseAsync("rsthrows", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(Deleted)]
    [InlineData(Restored)]
    public async Task NotTrackedMismatch_TriggersNoResync_AndAChannelThatIsGoneReleasesItsSlot(string route)
    {
        // AK 28: a notTracked expected channel has nothing to resync, and must not reveal a block by
        // behaving differently from a channel nobody ever joined. The hit's resync answers NotFound
        // (purged between report and resync): the slot goes back, the channel is not named.
        var userId = NewUserId();
        ArrangeConfirmedOwnership(userId);
        ArrangeReport(route, new InSetOutcome(
            1,
            [new SyncInSetChannelResultDto("ntpurged", 1, 1, [])],
            new UnresolvedChannelDto("ntexpected", UnresolvedChannelReasons.NotTracked)));
        ArrangeCooldown("ntpurged", acquired: true);
        _factory.Channels.TriggerResyncAsync("ntpurged", Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChannelResyncResult.NotFound);

        var response = await SendAsync(route, EmoteSetId, userId, body: """{"sevenTvEmoteIds": ["7tv-x1"], "expectedChannelName": "ntexpected"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(json.RootElement.GetProperty("resyncTriggered").EnumerateArray());
        await _factory.ResyncCooldown.DidNotReceive().TryBeginAsync("ntexpected", Arg.Any<CancellationToken>());
        await _factory.Channels.DidNotReceive().TriggerResyncAsync("ntexpected", Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
        await _factory.ResyncCooldown.Received(1).ReleaseAsync("ntpurged", Arg.Any<CancellationToken>());
    }

    private void ArrangeActorWithoutGrants(string userId, EmoteSetListResult actorList)
    {
        _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>()).Returns(actorList);
        _factory.GuardedEditorGrants.GetEditorGrantsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Ok(new SevenTvEditorGrants(new HashSet<string>(), new HashSet<string>())));
    }

    // The ordinary case: the set sits in the list of an account the actor edits, owned by it.
    private void ArrangeConfirmedOwnership(string userId)
    {
        _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SetList(ActorSevenTvUserId));
        _factory.EmoteSetList.ListByTwitchIdAsync(OwnerTwitchId, Arg.Any<CancellationToken>())
            .Returns(SetList(OwnerSevenTvUserId, (EmoteSetId, OwnerSevenTvUserId)));
        _factory.GuardedEditorGrants.GetEditorGrantsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Ok(new SevenTvEditorGrants(
                new HashSet<string> { OwnerTwitchLogin },
                new HashSet<string> { OwnerTwitchId },
                [new SevenTvEditorGrantEntry(OwnerTwitchLogin, OwnerTwitchId)])));
    }

    private void ArrangeReport(string route, InSetOutcome outcome)
    {
        if (route == Deleted)
        {
            _factory.Emotes.MarkDeletedInSetAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                    Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
                .Returns(new SyncDeletedInSetResultDto(outcome.ReportedCount, outcome.Channels, outcome.UnresolvedChannel));
        }
        else
        {
            _factory.Emotes.MarkRestoredInSetAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                    Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
                .Returns(new SyncRestoredInSetResultDto(outcome.ReportedCount, outcome.Channels, outcome.UnresolvedChannel));
        }
    }

    private void ArrangeCooldown(string channelName, bool acquired) =>
        _factory.ResyncCooldown.TryBeginAsync(channelName, Arg.Any<CancellationToken>())
            .Returns(new ResyncCooldownState(acquired, acquired ? 0 : 42));

    private async Task AssertReportReceivedAsync(string route, string? expectedChannelName)
    {
        if (route == Deleted)
        {
            await _factory.Emotes.Received(1).MarkDeletedInSetAsync(
                EmoteSetId, OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchId, Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 2 && ids[0] == "7tv-x1" && ids[1] == "7tv-x2"),
                expectedChannelName, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await _factory.Emotes.Received(1).MarkRestoredInSetAsync(
                EmoteSetId, OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchId, Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 2 && ids[0] == "7tv-x1" && ids[1] == "7tv-x2"),
                expectedChannelName, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
        }

        // The report reads its grants the guarded way only (same as sync-imported).
        await _factory.EditorService.DidNotReceive().GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private async Task AssertOwnerCheckDidNotRunAsync()
    {
        await _factory.EmoteSetList.DidNotReceive().ListByTwitchIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.GuardedEditorGrants.DidNotReceive().GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.SevenTvApi.DidNotReceive().LookUpEmoteSetOwnerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // No service call (and therefore no audit entry), no live event, no cooldown claim, no resync.
    private async Task AssertNoReportAsync()
    {
        await _factory.Emotes.DidNotReceive().MarkDeletedInSetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
        await _factory.Emotes.DidNotReceive().MarkRestoredInSetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
        await _factory.RedisPublisher.DidNotReceive().PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.ResyncCooldown.DidNotReceive().TryBeginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.Channels.DidNotReceive().TriggerResyncAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    private async Task<HttpResponseMessage> SendAsync(string route, string emoteSetId, string? userId, string? body = null)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/seventv/emote-sets/{emoteSetId}/{route}")
        {
            Content = new StringContent(
                body ?? """{"sevenTvEmoteIds": ["7tv-x1", "7tv-x2"], "expectedChannelName": null}""", Encoding.UTF8, "application/json"),
        };
        if (userId is not null)
        {
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        }

        return await client.SendAsync(request);
    }

    private static EmoteSetListResult SetList(string accountSevenTvUserId, params (string Id, string OwnerSevenTvUserId)[] sets) =>
        EmoteSetListResult.Ok(new EmoteSetList(
            null,
            [.. sets.Select(set => new EmoteSetSummary(set.Id, "Some Set", 1000, "NORMAL", false, "Some Owner", set.OwnerSevenTvUserId))],
            accountSevenTvUserId));

    private static string NewUserId() => Guid.NewGuid().ToString("N");

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }

    private sealed record InSetOutcome(int ReportedCount, IReadOnlyList<SyncInSetChannelResultDto> Channels, UnresolvedChannelDto? UnresolvedChannel);
}
