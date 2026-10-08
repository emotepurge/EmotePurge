using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Microsoft.AspNetCore.Hosting;
using NSubstitute;
using Xunit;
using static EmotePurge.Api.Tests.OwnershipLadderArrangements;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The registration and the two reports of a tag run (#201 T-C, spec 6.1/6.4): channel filters (401,
/// 400 channel name, 403) -&gt; the handler's form step (400) -&gt; the actor -&gt; the 7TV ownership
/// ladder (404/403 bare/503) -&gt; the service -&gt; its status mapped to HTTP. Every case runs against
/// all three routes where the rule is shared; the ladder runs for real, each case stages the set
/// lists and grants underneath it (as <see cref="SevenTvEmoteSetSyncBookkeepingEndpointTests"/> does).
/// </summary>
public class EmoteTagReportLadderTests : IClassFixture<ApiFactory>
{
    private const string Operations = "operations";
    private const string Placements = "placements";
    private const string Removed = "placements/removed";

    private const string Channel = "tagchannel";
    private const long TagId = 7;
    private const string EmoteSetId = "01GV88A38G0006FW5TVZVMG508";
    private const string ActorSevenTvUserId = "actor-seven-tv-id";
    private const string OwnerSevenTvUserId = "owner-seven-tv-id";
    private const string OperationId = "7c3f1d2e-0a4b-4c5d-8e6f-1a2b3c4d5e6f";
    private const string ActivationOperationId = "11111111-2222-4333-8444-555555555555";
    private const string RevisionId = "99999999-8888-4777-8666-555555555555";

    private static readonly object Missing = new();

    private readonly ApiFactory _factory;

    public EmoteTagReportLadderTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.ChannelAccess.ClearReceivedCalls();
        _factory.EmoteTags.ClearReceivedCalls();
        _factory.EmoteSetList.ClearReceivedCalls();
        _factory.GuardedEditorGrants.ClearReceivedCalls();
        _factory.SevenTvApi.ClearReceivedCalls();
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    [Theory]
    [InlineData(Operations)]
    [InlineData(Placements)]
    [InlineData(Removed)]
    public async Task AnonymousCaller_Gets401_BeforeAnything(string route)
    {
        var response = await SendAsync(route, userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNothingAskedAsync();
    }

    [Theory]
    [InlineData(Operations)]
    [InlineData(Placements)]
    [InlineData(Removed)]
    public async Task WithoutUsageStatsAccess_Gets403_BeforeTheLadder_EvenWhenTheLadderWouldAnswer404(string route)
    {
        // The channel filter outranks the ladder: otherwise a 403/404 from the ladder would tell a
        // stranger whether a set exists. The fakes below would give 404 emote_set_not_found.
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var userId = NewUserId();
        ArrangeSetUnknown(userId);

        var response = await SendAsync(route, userId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNothingAskedAsync();
    }

    [Fact]
    public async Task ACallerWhoMayViewButNotManage_IsNotRefused_ByTheChannelFilter()
    {
        // E9: running a tag needs no channel-management right of its own — the 7TV ownership of the
        // set is the gate. View without manage reaches the service.
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var userId = NewUserId();
        ArrangeOwner(userId);
        _factory.EmoteTags.RegisterOperationAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<RegisterTagOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TagOperationRegistrationResult(TagOperationRegistrationStatus.Ok, new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc)));

        var response = await SendAsync(Operations, userId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData(Operations)]
    [InlineData(Placements)]
    [InlineData(Removed)]
    public async Task OperationIdThatIsNotAUuid_Gets400_BeforeTheLadder(string route)
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);

        var response = await SendAsync(route, userId, body: Body(route, ("operationId", "not-a-uuid")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.TagOperationIdInvalid, await ReadErrorCodeAsync(response));
        await AssertNothingAskedAsync();
    }

    [Theory]
    [InlineData(Operations)]
    [InlineData(Placements)]
    [InlineData(Removed)]
    public async Task TheNilOperationId_Gets400_BeforeTheLadder(string route)
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);

        var response = await SendAsync(route, userId, body: Body(route, ("operationId", Guid.Empty.ToString())));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.TagOperationIdInvalid, await ReadErrorCodeAsync(response));
        await AssertNothingAskedAsync();
    }

    [Fact]
    public async Task ANilSnapshotRevision_Gets400TagOperationIdInvalid_BeforeTheLadder()
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);
        var snapshot = new JsonArray(new JsonObject { ["sevenTvEmoteId"] = "emote1", ["placementOperationId"] = Guid.Empty.ToString() });

        var response = await SendAsync(Removed, userId, body: Body(Removed, ("snapshot", snapshot)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.TagOperationIdInvalid, await ReadErrorCodeAsync(response));
        await AssertNothingAskedAsync();
    }

    [Fact]
    public async Task ANilActivationOperationId_Gets400TagOperationIdInvalid_BeforeTheLadder()
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);

        var response = await SendAsync(Removed, userId, body: Body(Removed, ("activationOperationId", Guid.Empty.ToString())));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.TagOperationIdInvalid, await ReadErrorCodeAsync(response));
        await AssertNothingAskedAsync();
    }

    [Fact]
    public async Task ASnapshotOverTheRequestLimit_Gets400EmoteIdsInvalid_BeforeTheLadder()
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);
        var tooMany = new JsonArray([.. Enumerable.Range(0, EmoteTagLimits.MaxIdsPerRequest + 1)
            .Select(i => (JsonNode?)new JsonObject { ["sevenTvEmoteId"] = $"e{i}", ["placementOperationId"] = RevisionId })]);

        var response = await SendAsync(Removed, userId, body: Body(Removed, ("snapshot", tooMany)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.EmoteIdsInvalid, await ReadErrorCodeAsync(response));
        await AssertNothingAskedAsync();
    }

    [Theory]
    [InlineData(Operations)]
    [InlineData(Placements)]
    [InlineData(Removed)]
    public async Task AMissingBody_Gets400TagOperationIdInvalid(string route)
    {
        var response = await SendAsync(route, NewUserId(), body: "null");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.TagOperationIdInvalid, await ReadErrorCodeAsync(response));
        await AssertNothingAskedAsync();
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("")]
    public async Task ASnapshotRevisionThatIsNotAUuid_Gets400TagOperationIdInvalid(string revision)
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);
        var snapshot = new JsonArray(new JsonObject { ["sevenTvEmoteId"] = "emote1", ["placementOperationId"] = revision });

        var response = await SendAsync(Removed, userId, body: Body(Removed, ("snapshot", snapshot)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.TagOperationIdInvalid, await ReadErrorCodeAsync(response));
        await AssertNothingAskedAsync();
    }

    [Fact]
    public async Task AnActivationOperationIdThatIsPresentButNotAUuid_Gets400TagOperationIdInvalid()
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);

        var response = await SendAsync(Removed, userId, body: Body(Removed, ("activationOperationId", "x")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.TagOperationIdInvalid, await ReadErrorCodeAsync(response));
        await AssertNothingAskedAsync();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("PlayIn")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnUnknownOrMissingKind_Gets400TagOperationKindInvalid_OnTheRegistrationOnly(string? kind)
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);

        var response = await SendAsync(Operations, userId, body: Body(Operations, ("kind", kind)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.TagOperationKindInvalid, await ReadErrorCodeAsync(response));
        await AssertNothingAskedAsync();
    }

    [Theory]
    [InlineData(Operations, "../x")]
    [InlineData(Placements, "../x")]
    [InlineData(Removed, "../x")]
    [InlineData(Operations, null)]
    [InlineData(Placements, null)]
    [InlineData(Removed, null)]
    public async Task AMalformedOrMissingSetId_Gets400InvalidEmoteSetId_BeforeTheLadder(string route, string? emoteSetId)
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);

        var response = await SendAsync(route, userId, body: Body(route, ("emoteSetId", emoteSetId)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidEmoteSetId, await ReadErrorCodeAsync(response));
        await AssertNothingAskedAsync();
    }

    // Every id list is checked in the form step, before the actor and the 7TV ladder — a null or
    // missing list included (docs/DECISIONS.md, #201 T-C).

    [Theory]
    [InlineData(Placements, "sevenTvEmoteIds")]
    [InlineData(Removed, "removedIds")]
    [InlineData(Removed, "keptIds")]
    [InlineData(Removed, "snapshot")]
    public async Task ANullOrMissingList_Gets400EmoteIdsInvalid_BeforeTheLadder(string route, string list)
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);

        var asNull = await SendAsync(route, userId, body: Body(route, (list, null)));
        var asMissing = await SendAsync(route, userId, body: Body(route, (list, Missing)));

        foreach (var response in new[] { asNull, asMissing })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ApiErrorCodes.EmoteIdsInvalid, await ReadErrorCodeAsync(response));
        }

        await AssertNothingAskedAsync();
    }

    [Theory]
    [InlineData(Placements, "sevenTvEmoteIds")]
    [InlineData(Removed, "removedIds")]
    [InlineData(Removed, "keptIds")]
    public async Task AListWithAMalformedOrNullId_Gets400EmoteIdsInvalid_BeforeTheLadder(string route, string list)
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);

        foreach (var ids in new JsonArray[] { new("ok1", "not valid!"), new("ok1", null), new(new string('a', 33)) })
        {
            var response = await SendAsync(route, userId, body: Body(route, (list, ids)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ApiErrorCodes.EmoteIdsInvalid, await ReadErrorCodeAsync(response));
        }

        await AssertNothingAskedAsync();
    }

    [Theory]
    [InlineData(Placements, "sevenTvEmoteIds")]
    [InlineData(Removed, "removedIds")]
    [InlineData(Removed, "keptIds")]
    public async Task AListOverTheRequestLimit_Gets400EmoteIdsInvalid_BeforeTheLadder(string route, string list)
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);
        var tooMany = new JsonArray([.. Enumerable.Range(0, EmoteTagLimits.MaxIdsPerRequest + 1).Select(i => (JsonNode?)JsonValue.Create($"e{i}"))]);

        var response = await SendAsync(route, userId, body: Body(route, (list, tooMany)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.EmoteIdsInvalid, await ReadErrorCodeAsync(response));
        await AssertNothingAskedAsync();
    }

    [Fact]
    public async Task ASnapshotWithANullElementOrAMalformedId_Gets400EmoteIdsInvalid_BeforeTheLadder()
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);
        var bad = new[]
        {
            new JsonArray(JsonValue.Create<string?>(null)),
            new JsonArray(new JsonObject { ["sevenTvEmoteId"] = "not valid!", ["placementOperationId"] = RevisionId }),
            new JsonArray(new JsonObject { ["sevenTvEmoteId"] = null, ["placementOperationId"] = RevisionId }),
            new JsonArray(new JsonObject { ["placementOperationId"] = RevisionId }),
        };

        foreach (var snapshot in bad)
        {
            var response = await SendAsync(Removed, userId, body: Body(Removed, ("snapshot", snapshot)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ApiErrorCodes.EmoteIdsInvalid, await ReadErrorCodeAsync(response));
        }

        await AssertNothingAskedAsync();
    }

    [Theory]
    [InlineData(Operations)]
    [InlineData(Placements)]
    [InlineData(Removed)]
    public async Task SetNotKnownTo7Tv_Gets404EmoteSetNotFound_AndTheServiceIsNotCalled(string route)
    {
        var userId = NewUserId();
        ArrangeSetUnknown(userId);

        var response = await SendAsync(route, userId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.EmoteSetNotFound, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.EmoteTags.ReceivedCalls());
    }

    [Theory]
    [InlineData(Operations)]
    [InlineData(Placements)]
    [InlineData(Removed)]
    public async Task NeitherOwnerNorEditor_GetsBareForbid_AndTheServiceIsNotCalled(string route)
    {
        var userId = NewUserId();
        _factory.ArrangeActorWithoutGrants(userId, SetList(ActorSevenTvUserId, (EmoteSetId, OwnerSevenTvUserId)));

        var response = await SendAsync(route, userId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(string.IsNullOrEmpty(responseBody), $"Expected an empty 403 body, got: {responseBody}");
        Assert.Empty(_factory.EmoteTags.ReceivedCalls());
    }

    [Theory]
    [InlineData(Operations)]
    [InlineData(Placements)]
    [InlineData(Removed)]
    public async Task SevenTvUnavailable_Gets503_AndTheServiceIsNotCalled(string route)
    {
        var userId = NewUserId();
        _factory.ArrangeActorWithoutGrants(userId, SetList(ActorSevenTvUserId));
        _factory.SevenTvApi.LookUpEmoteSetOwnerAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetOwnerLookupResult.Failed(SevenTvEmoteSetOwnerLookupStatus.Unavailable));

        var response = await SendAsync(route, userId);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ForeignChannelSevenTvUnavailable, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.EmoteTags.ReceivedCalls());
    }

    [Fact]
    public async Task Registration_ForTheOwner_CallsTheServiceWithTheNormalizedChannel_AndAnswers201WithTheRegistrationTime()
    {
        var userId = NewUserId();
        ArrangeOwner(userId);
        var registeredAt = new DateTime(2026, 10, 5, 12, 30, 0, DateTimeKind.Utc);
        _factory.EmoteTags.RegisterOperationAsync(Channel, TagId, Arg.Any<RegisterTagOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TagOperationRegistrationResult(TagOperationRegistrationStatus.Ok, registeredAt));

        var response = await SendAsync(Operations, userId, channel: "TagChannel");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(registeredAt, ReadJson(await response.Content.ReadAsStringAsync()).GetProperty("registeredAtUtc").GetDateTime().ToUniversalTime());
        await _factory.EmoteTags.Received(1).RegisterOperationAsync(
            Channel,
            TagId,
            Arg.Is<RegisterTagOperationRequest>(r => r.OperationId == Guid.Parse(OperationId) && r.Kind == EmoteTagOperationKind.PlayIn && r.EmoteSetId == EmoteSetId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Registration_OfAnAlreadyRegisteredOperation_Answers200WithTheSameTime()
    {
        var userId = NewUserId();
        ArrangeOwner(userId);
        var registeredAt = new DateTime(2026, 10, 5, 12, 30, 0, DateTimeKind.Utc);
        _factory.EmoteTags.RegisterOperationAsync(Channel, TagId, Arg.Any<RegisterTagOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TagOperationRegistrationResult(TagOperationRegistrationStatus.Replayed, registeredAt));

        var response = await SendAsync(Operations, userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(registeredAt, ReadJson(await response.Content.ReadAsStringAsync()).GetProperty("registeredAtUtc").GetDateTime().ToUniversalTime());
    }

    [Theory]
    [InlineData(TagOperationRegistrationStatus.ChannelNotFound, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotFound)]
    [InlineData(TagOperationRegistrationStatus.TagNotFound, HttpStatusCode.NotFound, ApiErrorCodes.TagNotFound)]
    [InlineData(TagOperationRegistrationStatus.Conflict, HttpStatusCode.Conflict, ApiErrorCodes.TagOperationConflict)]
    public async Task Registration_MapsTheServiceFailures(TagOperationRegistrationStatus status, HttpStatusCode expected, string errorCode)
    {
        var userId = NewUserId();
        ArrangeOwner(userId);
        _factory.EmoteTags.RegisterOperationAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<RegisterTagOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TagOperationRegistrationResult(status, null));

        var response = await SendAsync(Operations, userId);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(errorCode, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task PlacementReport_ForTheOwner_CallsTheServiceWithTheReport_AndAnswersTheCounters()
    {
        var userId = NewUserId();
        ArrangeOwner(userId);
        _factory.EmoteTags.ReportPlacementsAsync(Channel, TagId, Arg.Any<TagPlacementReport>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new TagPlacementReportResult(TagReportStatus.Ok, false, 2, 1, ["nt1"], ["st1"]));

        var response = await SendAsync(Placements, userId, channel: "TagChannel");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = ReadJson(await response.Content.ReadAsStringAsync());
        Assert.False(json.GetProperty("replayed").GetBoolean());
        Assert.Equal(2, json.GetProperty("recordedCount").GetInt32());
        Assert.Equal(1, json.GetProperty("alreadyRecordedCount").GetInt32());
        Assert.Equal(["nt1"], json.GetProperty("notTaggedIds").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["st1"], json.GetProperty("discardedStaleIds").EnumerateArray().Select(e => e.GetString()));
        await _factory.EmoteTags.Received(1).ReportPlacementsAsync(
            Channel,
            TagId,
            Arg.Is<TagPlacementReport>(r =>
                r.OperationId == Guid.Parse(OperationId) && r.EmoteSetId == EmoteSetId && r.SevenTvEmoteIds.SequenceEqual(new[] { "emote1", "emote2" })),
            Arg.Is<AuditActor>(actor => actor.TwitchUserId == userId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlacementReport_WithAnEmptyList_IsLegal_AndReachesTheService()
    {
        // A play-in that added nothing is a report like any other (E26): the activation still has to be recorded.
        var userId = NewUserId();
        ArrangeOwner(userId);
        _factory.EmoteTags.ReportPlacementsAsync(Channel, TagId, Arg.Any<TagPlacementReport>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new TagPlacementReportResult(TagReportStatus.Ok, false, 0, 0, [], []));

        var response = await SendAsync(Placements, userId, body: Body(Placements, ("sevenTvEmoteIds", new JsonArray())));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.EmoteTags.Received(1).ReportPlacementsAsync(
            Channel, TagId, Arg.Is<TagPlacementReport>(r => r.SevenTvEmoteIds.Count == 0), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovalReport_ForTheOwner_CallsTheServiceWithTheParsedReport_AndAnswersTheCounters()
    {
        var userId = NewUserId();
        ArrangeOwner(userId);
        _factory.EmoteTags.ReportRemovalAsync(Channel, TagId, Arg.Any<TagRemovalReport>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new TagRemovalReportResult(TagReportStatus.Ok, false, 3, 2, 1, 4, true));

        var response = await SendAsync(Removed, userId, channel: "TagChannel");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = ReadJson(await response.Content.ReadAsStringAsync());
        Assert.False(json.GetProperty("replayed").GetBoolean());
        Assert.Equal((3, 2, 1, 4), (
            json.GetProperty("deletedCount").GetInt32(), json.GetProperty("transferredCount").GetInt32(),
            json.GetProperty("droppedCount").GetInt32(), json.GetProperty("sweptCount").GetInt32()));
        Assert.True(json.GetProperty("deactivated").GetBoolean());
        await _factory.EmoteTags.Received(1).ReportRemovalAsync(
            Channel,
            TagId,
            Arg.Is<TagRemovalReport>(r =>
                r.OperationId == Guid.Parse(OperationId)
                && r.EmoteSetId == EmoteSetId
                && r.ActivationOperationId == Guid.Parse(ActivationOperationId)
                && r.Snapshot.Count == 1 && r.Snapshot[0] == new TagPlacementSnapshotEntry("emote1", Guid.Parse(RevisionId))
                && r.RemovedIds.SequenceEqual(new[] { "emote1" })
                && r.KeptIds.Count == 0),
            Arg.Is<AuditActor>(actor => actor.TwitchUserId == userId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovalReport_WithoutAnActivationOperationId_ForwardsNull_AndEmptyListsAreLegal()
    {
        var userId = NewUserId();
        ArrangeOwner(userId);
        _factory.EmoteTags.ReportRemovalAsync(Channel, TagId, Arg.Any<TagRemovalReport>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new TagRemovalReportResult(TagReportStatus.Ok, false, 0, 0, 0, 0, false));
        var body = Body(
            Removed,
            ("activationOperationId", null),
            ("snapshot", new JsonArray()),
            ("removedIds", new JsonArray()),
            ("keptIds", new JsonArray()));

        var response = await SendAsync(Removed, userId, body: body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.EmoteTags.Received(1).ReportRemovalAsync(
            Channel, TagId, Arg.Is<TagRemovalReport>(r => r.ActivationOperationId == null && r.Snapshot.Count == 0), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovalReport_OfAnAppliedOperation_Answers200ReplayedWithNoOutcomeFields()
    {
        // On a replay the service's counters and Deactivated describe no outcome. The handler
        // answers the documented replay body itself instead of forwarding them.
        var userId = NewUserId();
        ArrangeOwner(userId);
        _factory.EmoteTags.ReportRemovalAsync(Channel, TagId, Arg.Any<TagRemovalReport>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new TagRemovalReportResult(TagReportStatus.Ok, true, 5, 5, 5, 5, true));

        var response = await SendAsync(Removed, userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = ReadJson(await response.Content.ReadAsStringAsync());
        Assert.True(json.GetProperty("replayed").GetBoolean());
        Assert.Equal(0, json.GetProperty("deletedCount").GetInt32());
        Assert.Equal(0, json.GetProperty("transferredCount").GetInt32());
        Assert.Equal(0, json.GetProperty("droppedCount").GetInt32());
        Assert.Equal(0, json.GetProperty("sweptCount").GetInt32());
        Assert.False(json.GetProperty("deactivated").GetBoolean());
    }

    [Fact]
    public async Task PlacementReport_OfAnAppliedOperation_Answers200Replayed()
    {
        var userId = NewUserId();
        ArrangeOwner(userId);
        _factory.EmoteTags.ReportPlacementsAsync(Channel, TagId, Arg.Any<TagPlacementReport>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new TagPlacementReportResult(TagReportStatus.Ok, true, 0, 0, [], []));

        var response = await SendAsync(Placements, userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(ReadJson(await response.Content.ReadAsStringAsync()).GetProperty("replayed").GetBoolean());
    }

    [Theory]
    [InlineData(Placements, TagReportStatus.OperationUnknown, HttpStatusCode.NotFound, ApiErrorCodes.TagOperationUnknown)]
    [InlineData(Placements, TagReportStatus.OperationConflict, HttpStatusCode.Conflict, ApiErrorCodes.TagOperationConflict)]
    [InlineData(Placements, TagReportStatus.TagNotFound, HttpStatusCode.NotFound, ApiErrorCodes.TagNotFound)]
    [InlineData(Placements, TagReportStatus.ChannelNotFound, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotFound)]
    [InlineData(Removed, TagReportStatus.OperationUnknown, HttpStatusCode.NotFound, ApiErrorCodes.TagOperationUnknown)]
    [InlineData(Removed, TagReportStatus.OperationConflict, HttpStatusCode.Conflict, ApiErrorCodes.TagOperationConflict)]
    [InlineData(Removed, TagReportStatus.TagNotFound, HttpStatusCode.NotFound, ApiErrorCodes.TagNotFound)]
    [InlineData(Removed, TagReportStatus.ChannelNotFound, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotFound)]
    public async Task Reports_MapTheServiceFailures(string route, TagReportStatus status, HttpStatusCode expected, string errorCode)
    {
        var userId = NewUserId();
        ArrangeOwner(userId);
        if (route == Placements)
        {
            _factory.EmoteTags.ReportPlacementsAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<TagPlacementReport>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
                .Returns(new TagPlacementReportResult(status, false, 0, 0, [], []));
        }
        else
        {
            _factory.EmoteTags.ReportRemovalAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<TagRemovalReport>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
                .Returns(new TagRemovalReportResult(status, false, 0, 0, 0, 0, false));
        }

        var response = await SendAsync(route, userId);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(errorCode, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Permissions_CarryTheRunsFlag_FalseByDefault()
    {
        var response = await SendPermissionsAsync(_factory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(ReadJson(await response.Content.ReadAsStringAsync()).GetProperty("tagRunsEnabled").GetBoolean());
    }

    [Fact]
    public async Task Permissions_CarryTheRunsFlag_FromTheTagsConfiguration()
    {
        using var enabled = _factory.WithWebHostBuilder(builder => builder.UseSetting("Tags:RunsEnabled", "true"));

        var response = await SendPermissionsAsync(enabled);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(ReadJson(await response.Content.ReadAsStringAsync()).GetProperty("tagRunsEnabled").GetBoolean());
    }

    // The ladder answers 404 emote_set_not_found: the set is in no checked list and 7TV knows no owner.
    private void ArrangeSetUnknown(string userId)
    {
        _factory.ArrangeActorWithoutGrants(userId, SetList(ActorSevenTvUserId));
        _factory.SevenTvApi.LookUpEmoteSetOwnerAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetOwnerLookupResult.Failed(SevenTvEmoteSetOwnerLookupStatus.NotFound));
    }

    // The ladder answers Owner: the set sits in the actor's own list, owned by the actor.
    private void ArrangeOwner(string userId) =>
        _factory.ArrangeActorWithoutGrants(userId, SetList(ActorSevenTvUserId, (EmoteSetId, ActorSevenTvUserId)));

    // Neither the ladder (set lists, grants, 7TV owner lookup) nor the service was asked anything.
    private async Task AssertNothingAskedAsync()
    {
        await _factory.EmoteSetList.DidNotReceive().ListByTwitchIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.GuardedEditorGrants.DidNotReceive().GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.SevenTvApi.DidNotReceive().LookUpEmoteSetOwnerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Empty(_factory.EmoteTags.ReceivedCalls());
    }

    // Marks a property to be left out of the body entirely (as opposed to JSON null).
    private static string Body(string route, params (string Name, object? Value)[] overrides)
    {
        var body = route switch
        {
            Operations => new JsonObject
            {
                ["operationId"] = OperationId,
                ["kind"] = EmoteTagOperationKind.PlayIn,
                ["emoteSetId"] = EmoteSetId,
                ["targetOwnerTwitchId"] = null,
            },
            Placements => new JsonObject
            {
                ["operationId"] = OperationId,
                ["emoteSetId"] = EmoteSetId,
                ["targetOwnerTwitchId"] = null,
                ["sevenTvEmoteIds"] = new JsonArray("emote1", "emote2"),
            },
            _ => new JsonObject
            {
                ["operationId"] = OperationId,
                ["emoteSetId"] = EmoteSetId,
                ["targetOwnerTwitchId"] = null,
                ["activationOperationId"] = ActivationOperationId,
                ["snapshot"] = new JsonArray(new JsonObject { ["sevenTvEmoteId"] = "emote1", ["placementOperationId"] = RevisionId }),
                ["removedIds"] = new JsonArray("emote1"),
                ["keptIds"] = new JsonArray(),
            },
        };

        foreach (var (name, value) in overrides)
        {
            body.Remove(name);
            if (!ReferenceEquals(value, Missing))
            {
                body[name] = value switch
                {
                    null => null,
                    JsonNode node => node,
                    string text => JsonValue.Create(text),
                    _ => throw new ArgumentException($"Unsupported override type {value.GetType()}.", nameof(overrides)),
                };
            }
        }

        return body.ToJsonString();
    }

    private async Task<HttpResponseMessage> SendAsync(string route, string? userId, string? body = null, string channel = Channel)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/channels/{channel}/tags/{TagId}/{route}")
        {
            Content = new StringContent(body ?? Body(route), Encoding.UTF8, "application/json"),
        };
        if (userId is not null)
        {
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendPermissionsAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/channels/{Channel}/permissions");
        request.Headers.Add(TestAuthHandler.UserIdHeader, NewUserId());
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return await client.SendAsync(request);
    }

    private static string NewUserId() => Guid.NewGuid().ToString("N");

    private static JsonElement ReadJson(string body) => JsonDocument.Parse(body).RootElement;

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response) =>
        ReadJson(await response.Content.ReadAsStringAsync()).GetProperty("errorCode").GetString();
}
