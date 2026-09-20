using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The five-step ladder of <c>POST /api/seventv/emote-sets/{emoteSetId}/sync-imported</c> (spec 6.7,
/// AK 30, T2.4): middleware (401/429) -&gt; <c>EmoteSetIdValidationFilter</c> (400) -&gt; the shared
/// body vocabulary table (400) -&gt; the owner check (404/403 bare/503) -&gt; the service call (204,
/// <c>ChannelName = null</c>).
/// </summary>
public class SevenTvEmoteSetSyncImportedEndpointTests : IClassFixture<ApiFactory>
{
    private const string EmoteSetId = "01GV88A38G0006FW5TVZVMG507";
    private const string OwnerSevenTvUserId = "owner-seven-tv-id";
    private const string OwnerTwitchLogin = "handofblood";

    private readonly ApiFactory _factory;

    public SevenTvEmoteSetSyncImportedEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.EditorService.ClearReceivedCalls();
        _factory.Emotes.ClearReceivedCalls();
    }

    [Fact]
    public async Task AnonymousCaller_Gets401()
    {
        var response = await SendAsync(EmoteSetId, userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await _factory.EditorService.DidNotReceive().CheckEmoteSetOwnershipAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        await _factory.EditorService.DidNotReceive().CheckEmoteSetOwnershipAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        await _factory.EditorService.DidNotReceive().CheckEmoteSetOwnershipAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownSet_Gets404EmoteSetNotFound()
    {
        _factory.EditorService.CheckEmoteSetOwnershipAsync(
                Arg.Any<string>(), Arg.Any<string>(), EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetOwnershipCheckResult.SetNotFound());

        var response = await SendAsync(EmoteSetId, NewUserId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.EmoteSetNotFound, await ReadErrorCodeAsync(response));
        await _factory.Emotes.DidNotReceive().MarkImportedToSetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NeitherOwnerNorEditor_GetsBareForbid_WithNoErrorCodeBody()
    {
        _factory.EditorService.CheckEmoteSetOwnershipAsync(
                Arg.Any<string>(), Arg.Any<string>(), EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetOwnershipCheckResult.Forbidden());

        var response = await SendAsync(EmoteSetId, NewUserId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // "Bare" (spec 6.7): Results.Forbid(), like the four existing IEndpointFilter-based
        // authorization filters — no { errorCode } body at all.
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(string.IsNullOrEmpty(responseBody), $"Expected an empty 403 body, got: {responseBody}");
        await _factory.Emotes.DidNotReceive().MarkImportedToSetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SevenTvUnavailable_Gets503_AndWritesNoAuditEntry()
    {
        _factory.EditorService.CheckEmoteSetOwnershipAsync(
                Arg.Any<string>(), Arg.Any<string>(), EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetOwnershipCheckResult.Unavailable());

        var response = await SendAsync(EmoteSetId, NewUserId());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ForeignChannelSevenTvUnavailable, await ReadErrorCodeAsync(response));
        await _factory.Emotes.DidNotReceive().MarkImportedToSetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmedOwnership_Gets204_AndForwardsTheResolvedIdentityToTheService()
    {
        _factory.EditorService.CheckEmoteSetOwnershipAsync(
                Arg.Any<string>(), Arg.Any<string>(), EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEmoteSetOwnershipCheckResult.Owner(OwnerSevenTvUserId, OwnerTwitchLogin));

        var body = """{"sevenTvEmoteIds": ["7tv-x1", "7tv-x2"], "sourceChannelName": "sourcechannel", "sourceKind": "channel"}""";
        var response = await SendAsync(EmoteSetId, NewUserId(), body: body);

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
    }

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
