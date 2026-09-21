using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The five-step ladder of <c>POST /api/seventv/emote-sets/{emoteSetId}/sync-imported</c> (spec 6.7,
/// AK 30, T2.4): middleware (401/429) -&gt; <c>EmoteSetIdValidationFilter</c> (400) -&gt; the shared
/// body vocabulary table (400) -&gt; the owner check (404/403 bare/503) -&gt; the service call (204,
/// <c>ChannelName = null</c>).
/// </summary>
/// <remarks>
/// The owner check runs for real (spec section 32): it answers from the substituted set lists and
/// grants, and its fallback owner lookup reaches the substituted 7TV client — so each case below is
/// set up the way the list answers would actually produce its status, not by stubbing the status.
/// </remarks>
public class SevenTvEmoteSetSyncImportedEndpointTests : IClassFixture<ApiFactory>
{
    private const string EmoteSetId = "01GV88A38G0006FW5TVZVMG507";
    private const string ActorSevenTvUserId = "actor-seven-tv-id";
    private const string OwnerSevenTvUserId = "owner-seven-tv-id";
    private const string OwnerTwitchId = "49140130";
    private const string OwnerTwitchLogin = "handofblood";

    private readonly ApiFactory _factory;

    public SevenTvEmoteSetSyncImportedEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.EditorService.ClearReceivedCalls();
        _factory.GuardedEditorGrants.ClearReceivedCalls();
        _factory.EmoteSetList.ClearReceivedCalls();
        _factory.SevenTvApi.ClearReceivedCalls();
        _factory.Emotes.ClearReceivedCalls();
    }

    [Fact]
    public async Task AnonymousCaller_Gets401()
    {
        var response = await SendAsync(EmoteSetId, userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertOwnerCheckDidNotRunAsync();
    }

    [Fact]
    public async Task MalformedRouteEmoteSetId_Gets400_BeforeAnyServiceCall()
    {
        // EmoteSetIdValidationFilter's route-value branch (spec 6.7) — the route carries the set id
        // here, unlike every other route this filter already guarded, which only ever read it from
        // the query string. 33 characters (one over E14's limit), not "../x": a path-traversal
        // sequence splits into extra URL segments before routing ever matches this route template,
        // which would test routing, not the filter.
        var response = await SendAsync(new string('a', 33), NewUserId());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidEmoteSetId, await ReadErrorCodeAsync(response));
        await AssertOwnerCheckDidNotRunAsync();
    }

    // The shared vocabulary table (EmoteEndpoints.ValidateSyncImportedVocabulary), mirrored from the
    // channel-scoped endpoint's own AuthFilterMatrixTests cases (spec 6.7's "sechs Fälle... gespiegelt").
    [Theory]
    [InlineData("""{"sevenTvEmoteIds": [], "sourceChannelName": null, "sourceKind": "file"}""", ApiErrorCodes.EmoteIdsEmpty)]
    [InlineData("""{"sevenTvEmoteIds": ["7tv-x1"], "sourceChannelName": null, "sourceKind": "not-a-kind"}""", ApiErrorCodes.InvalidSourceKind)]
    [InlineData("""{"sevenTvEmoteIds": ["7tv-x1"], "sourceChannelName": "not a channel!", "sourceKind": "file"}""", ApiErrorCodes.InvalidChannelName)]
    [InlineData("""{"sevenTvEmoteIds": ["7tv-x1"], "sourceChannelName": null, "sourceKind": "channel"}""", ApiErrorCodes.InvalidSourceKind)]
    [InlineData("""{"sevenTvEmoteIds": ["7tv-x1"], "sourceChannelName": "somechannel", "sourceKind": "file"}""", ApiErrorCodes.InvalidSourceKind)]
    [InlineData("""{"sevenTvEmoteIds": ["7tv-x1"], "sourceChannelName": null, "sourceKind": "seventv-leaderboard", "leaderboardSort": "TRENDING_WEEKLY"}""", ApiErrorCodes.InvalidLeaderboardSort)]
    public async Task VocabularyViolation_Gets400_BeforeTheOwnerCheckRuns(string body, string expectedErrorCode)
    {
        var response = await SendAsync(EmoteSetId, NewUserId(), body: body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedErrorCode, await ReadErrorCodeAsync(response));
        await AssertOwnerCheckDidNotRunAsync();
    }

    [Fact]
    public async Task UnknownSet_Gets404EmoteSetNotFound()
    {
        // In no checked account's list, and 7TV knows no owner for it.
        var userId = NewUserId();
        ArrangeActorWithoutGrants(userId, SetList(ActorSevenTvUserId));
        _factory.SevenTvApi.LookUpEmoteSetOwnerAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetOwnerLookupResult.Failed(SevenTvEmoteSetOwnerLookupStatus.NotFound));

        var response = await SendAsync(EmoteSetId, userId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.EmoteSetNotFound, await ReadErrorCodeAsync(response));
        await AssertNoAuditEntryAsync();
    }

    [Fact]
    public async Task NeitherOwnerNorEditor_GetsBareForbid_WithNoErrorCodeBody()
    {
        // Listed in the actor's own list — but owned by an account the actor neither is nor edits.
        var userId = NewUserId();
        ArrangeActorWithoutGrants(userId, SetList(ActorSevenTvUserId, (EmoteSetId, OwnerSevenTvUserId)));

        var response = await SendAsync(EmoteSetId, userId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // "Bare" (spec 6.7): Results.Forbid(), like the four existing IEndpointFilter-based
        // authorization filters — no { errorCode } body at all.
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(string.IsNullOrEmpty(responseBody), $"Expected an empty 403 body, got: {responseBody}");
        await AssertNoAuditEntryAsync();
    }

    [Fact]
    public async Task SevenTvUnavailable_Gets503_AndWritesNoAuditEntry()
    {
        // In no list, and the one direct owner lookup gets no answer from 7TV.
        var userId = NewUserId();
        ArrangeActorWithoutGrants(userId, SetList(ActorSevenTvUserId));
        _factory.SevenTvApi.LookUpEmoteSetOwnerAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetOwnerLookupResult.Failed(SevenTvEmoteSetOwnerLookupStatus.Unavailable));

        var response = await SendAsync(EmoteSetId, userId);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ForeignChannelSevenTvUnavailable, await ReadErrorCodeAsync(response));
        await AssertNoAuditEntryAsync();
    }

    [Fact]
    public async Task ConfirmedOwnership_Gets204_AndForwardsTheResolvedIdentityToTheService()
    {
        // The ordinary case: the set sits in the list of an account the actor edits, owned by that
        // account — answered from the lists alone, without asking 7TV about the set.
        var userId = NewUserId();
        _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SetList(ActorSevenTvUserId));
        _factory.EmoteSetList.ListByTwitchIdAsync(OwnerTwitchId, Arg.Any<CancellationToken>())
            .Returns(SetList(OwnerSevenTvUserId, (EmoteSetId, OwnerSevenTvUserId)));
        _factory.GuardedEditorGrants.GetEditorGrantsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Ok(new SevenTvEditorGrants(
                new HashSet<string> { OwnerTwitchLogin },
                new HashSet<string> { OwnerTwitchId },
                [new SevenTvEditorGrantEntry(OwnerTwitchLogin, OwnerTwitchId)])));

        var body = """{"sevenTvEmoteIds": ["7tv-x1", "7tv-x2"], "sourceChannelName": "sourcechannel", "sourceKind": "channel"}""";
        var response = await SendAsync(EmoteSetId, userId, body: body);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await _factory.Emotes.Received(1).MarkImportedToSetAsync(
            EmoteSetId,
            OwnerSevenTvUserId,
            OwnerTwitchLogin,
            Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 2 && ids[0] == "7tv-x1" && ids[1] == "7tv-x2"),
            "sourcechannel",
            "channel",
            null,
            Arg.Any<AuditActor>(),
            Arg.Any<CancellationToken>());
        await _factory.SevenTvApi.DidNotReceive().LookUpEmoteSetOwnerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // The report reads its grants the guarded way only; the unguarded lookup the authorization
        // path uses is never asked from here (spec section 32, second review round).
        await _factory.EditorService.DidNotReceive().GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static EmoteSetListResult SetList(string accountSevenTvUserId, params (string Id, string OwnerSevenTvUserId)[] sets) =>
        EmoteSetListResult.Ok(new EmoteSetList(
            null,
            [.. sets.Select(set => new EmoteSetSummary(set.Id, "Some Set", 1000, "NORMAL", false, "Some Owner", set.OwnerSevenTvUserId))],
            accountSevenTvUserId));

    private void ArrangeActorWithoutGrants(string userId, EmoteSetListResult actorList)
    {
        _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>()).Returns(actorList);
        _factory.GuardedEditorGrants.GetEditorGrantsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Ok(new SevenTvEditorGrants(new HashSet<string>(), new HashSet<string>())));
    }

    private async Task AssertOwnerCheckDidNotRunAsync()
    {
        await _factory.EmoteSetList.DidNotReceive().ListByTwitchIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.GuardedEditorGrants.DidNotReceive().GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.SevenTvApi.DidNotReceive().LookUpEmoteSetOwnerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private async Task AssertNoAuditEntryAsync() =>
        await _factory.Emotes.DidNotReceive().MarkImportedToSetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());

    private static string NewUserId() => Guid.NewGuid().ToString("N");

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }

    private async Task<HttpResponseMessage> SendAsync(string emoteSetId, string? userId, string? login = "someuser", string? body = null)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/seventv/emote-sets/{emoteSetId}/sync-imported")
        {
            Content = new StringContent(body ?? """{"sevenTvEmoteIds": ["7tv-x1"], "sourceChannelName": null, "sourceKind": "file"}""", Encoding.UTF8, "application/json"),
        };
        if (userId is not null)
        {
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            if (login is not null)
            {
                request.Headers.Add(TestAuthHandler.LoginHeader, login);
            }
        }

        return await client.SendAsync(request);
    }
}
