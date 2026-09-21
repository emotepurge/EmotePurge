using System.Net;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The filter matrix for the new <c>/api/seventv/channels/{channelName}/emotes</c> group
/// (foreign-channel-import spec, section 4/AK 14) — the group's whole reason to exist is that it
/// carries no <c>UsageStatsAccessAuthorizationFilter</c>, and this file is what proves the group
/// actually reflects that instead of just claiming it in a comment.
/// </summary>
/// <remarks>
/// The two budget-exhaustion cases pin the ordering the spec calls out explicitly as deliberate:
/// <c>UseRateLimiter</c> is middleware and therefore always runs before the endpoint's
/// <see cref="ChannelNameValidationFilter"/>, so an invalid channel name over a spent budget answers
/// 429, not the 400 it would get with budget still available — the opposite of every other
/// channel-scoped group's contract, and worth its own regression here rather than being inferred from
/// <c>AuthFilterMatrixTests</c>.
/// </remarks>
public class SevenTvForeignEmoteSetEndpointTests : IClassFixture<ApiFactory>
{
    private const string Channel = "handofblood";
    private const string InvalidChannel = "!!";

    private readonly ApiFactory _factory;

    public SevenTvForeignEmoteSetEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.ChannelAccess.ClearReceivedCalls();
        _factory.ForeignEmoteSet.ClearReceivedCalls();
        _factory.EditorService.ClearReceivedCalls();
        _factory.EmoteSetList.ClearReceivedCalls();
        _factory.Channels.ClearReceivedCalls();
    }

    [Fact]
    public async Task AnonymousCaller_Gets401()
    {
        var response = await SendAsync(Channel, userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoggedInCaller_WithNoRoleInTheChannel_Gets200()
    {
        var emoteSet = new ForeignEmoteSet(
            Channel, "01FRY81K4800085N93FNKSBYXS", "01FRY81K4800085N93FNKSBYXS-set", 1, false,
            [new ForeignEmoteRow("e1", "Alias", "Default", "https://cdn.7tv.app/emote/e1/4x_static.webp", 500, 12)]);
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Channel, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Ok(emoteSet));

        var response = await SendAsync(Channel, NewUserId());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(Channel, body.GetProperty("channelName").GetString());
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());

        // The whole point of this group (spec section 4): no role check of any kind runs. A
        // logged-in caller with zero relationship to the channel still gets 200, and nothing here
        // ever asked the usage-stats authorization service anything.
        await _factory.ChannelAccess.DidNotReceive().CanViewUsageStatsAsync(
            Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ForeignEmoteSetLookupStatus.ChannelNotOnTwitch, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotOnTwitch)]
    [InlineData(ForeignEmoteSetLookupStatus.TwitchUnavailable, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelTwitchUnavailable)]
    [InlineData(ForeignEmoteSetLookupStatus.NoSevenTvAccount, HttpStatusCode.NotFound, ApiErrorCodes.ForeignChannelNoSevenTvAccount)]
    [InlineData(ForeignEmoteSetLookupStatus.NoActiveEmoteSet, HttpStatusCode.NotFound, ApiErrorCodes.ForeignChannelNoActiveEmoteSet)]
    [InlineData(ForeignEmoteSetLookupStatus.SevenTvUnavailable, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelSevenTvUnavailable)]
    [InlineData(ForeignEmoteSetLookupStatus.SevenTvRateLimited, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelSevenTvUnavailable)]
    public async Task EveryFailureStatus_MapsToItsDocumentedResponse(
        ForeignEmoteSetLookupStatus status, HttpStatusCode expectedStatusCode, string expectedErrorCode)
    {
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(status));

        var response = await SendAsync(Channel, NewUserId());

        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal(expectedErrorCode, await ReadErrorCodeAsync(response));
    }

    /// <summary>T2, spec E3: <c>?refresh=true</c> reaches the service as <c>refresh: true</c>.</summary>
    [Fact]
    public async Task RefreshQueryParam_IsPassedThroughToTheService()
    {
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Channel, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.NoSevenTvAccount));

        await SendAsync(Channel, NewUserId(), refresh: true);

        await _factory.ForeignEmoteSet.Received(1).GetForeignEmoteSetAsync(Channel, true, Arg.Any<CancellationToken>());
    }

    /// <summary>The counterpart: an absent query string reaches the service as <c>refresh: false</c>,
    /// not as a 400 for a "missing" parameter — the whole point of giving it a C# default.</summary>
    [Fact]
    public async Task AbsentRefreshQueryParam_DefaultsToFalse()
    {
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Channel, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.NoSevenTvAccount));

        var response = await SendAsync(Channel, NewUserId());

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await _factory.ForeignEmoteSet.Received(1).GetForeignEmoteSetAsync(Channel, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidChannelName_WithBudgetStillAvailable_Gets400()
    {
        var response = await SendAsync(InvalidChannel, NewUserId());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidChannelName, await ReadErrorCodeAsync(response));
    }

    /// <summary>
    /// AK 14's central, easy-to-get-backwards case: the rate limiter is middleware and runs before
    /// <see cref="ChannelNameValidationFilter"/> can, so once the budget is spent an invalid name no
    /// longer gets the 400 the previous test just proved it gets with budget to spare — it gets 429
    /// like everything else. This is documented as deliberate (spec section 4), not a bug.
    /// </summary>
    [Fact]
    public async Task InvalidChannelName_OverBudget_Gets429_NotThe400ItWouldGetOtherwise()
    {
        var userId = NewUserId();
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.NoSevenTvAccount));

        // Spend the ForeignEmoteLookup budget (10/min, RateLimitingOptions.ForeignEmoteLookup) on
        // valid requests first — ChannelNameValidationFilter never rejects these, so every one of
        // them is genuinely a permit spent, not a request the filter would have refused anyway.
        for (var i = 0; i < 10; i++)
        {
            var spend = await SendAsync(Channel, userId);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, spend.StatusCode);
        }

        var response = await SendAsync(InvalidChannel, userId);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    /// <summary>The other reading of the same ordering: a caller who spends the budget on nothing but
    /// requests the group's own handler would happily serve still gets 429 on the next one — the
    /// budget itself, not the name filter, is what stops them.</summary>
    [Fact]
    public async Task ValidChannelName_OverBudget_Gets429()
    {
        var userId = NewUserId();
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.NoSevenTvAccount));

        for (var i = 0; i < 10; i++)
        {
            await SendAsync(Channel, userId);
        }

        var response = await SendAsync(Channel, userId);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    // AK 26/27 (spec 2026-09-20, 6.4): the ?emoteSetId= query parameter on this same route —
    // format-rejected before the handler, and switching the service call to the set-ID mode when
    // it is present and valid.

    [Fact]
    public async Task EmoteSetIdQueryParam_Malformed_Gets400_WithoutCallingTheService()
    {
        var response = await SendAsync(Channel, NewUserId(), emoteSetId: "../x");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidEmoteSetId, await ReadErrorCodeAsync(response));
        await _factory.ForeignEmoteSet.DidNotReceive().GetForeignEmoteSetAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _factory.ForeignEmoteSet.DidNotReceive().GetForeignEmoteSetBySetIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmoteSetIdQueryParam_Valid_CallsTheSetIdModeWithTheGivenIdAndRefresh_NotTheLoginMode()
    {
        const string setId = "01FRY81K4800085N93FNKSBYXS";
        var emoteSet = new ForeignEmoteSet(Channel, null, setId, 0, false, []);
        _factory.ForeignEmoteSet.GetForeignEmoteSetBySetIdAsync(Channel, setId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Ok(emoteSet));

        var response = await SendAsync(Channel, NewUserId(), emoteSetId: setId, refresh: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.ForeignEmoteSet.Received(1).GetForeignEmoteSetBySetIdAsync(Channel, setId, true, Arg.Any<CancellationToken>());
        await _factory.ForeignEmoteSet.DidNotReceive().GetForeignEmoteSetAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmoteSetIdQueryParam_Valid_ReturnsTheSetModeBody_WithANullSevenTvUserId()
    {
        const string setId = "01FRY81K4800085N93FNKSBYXS";
        var emoteSet = new ForeignEmoteSet(
            Channel, null, setId, 1, false,
            [new ForeignEmoteRow("e1", "Alias", "Default", "https://cdn.7tv.app/emote/e1/4x_static.webp", 500, 12)],
            "Some set", 956);
        _factory.ForeignEmoteSet.GetForeignEmoteSetBySetIdAsync(Channel, setId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Ok(emoteSet));

        var response = await SendAsync(Channel, NewUserId(), emoteSetId: setId);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(setId, body.GetProperty("emoteSetId").GetString());
        Assert.Equal("Some set", body.GetProperty("emoteSetName").GetString());
        Assert.Equal(956, body.GetProperty("capacity").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sevenTvUserId").ValueKind);
    }

    // AK 47 (spec 2026-09-20, 6.3): GET /api/seventv/channels/{c}/emote-sets — the K3 source-set
    // picker's list route.

    [Fact]
    public async Task EmoteSets_AnonymousCaller_Gets401()
    {
        var response = await SendSetsAsync(Channel, userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EmoteSets_InvalidChannelName_Gets400()
    {
        var response = await SendSetsAsync(InvalidChannel, NewUserId());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidChannelName, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task EmoteSets_Ok_ReturnsSetsSortedActiveFirstThenOrdinalByName()
    {
        _factory.ForeignEmoteSet.GetForeignEmoteSetListAsync(Channel, Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetListLookupResult.Ok(new EmoteSetList(
                "set-b",
                [
                    new EmoteSetSummary("set-a", "Alpha", 1000, "NORMAL", false, "Owner"),
                    new EmoteSetSummary("set-b", "Beta", 1000, "NORMAL", false, "Owner"),
                    new EmoteSetSummary("set-c", "Personal", 5, "PERSONAL", true, "Owner"),
                ],
                "7tv-owner-1")));

        var response = await SendSetsAsync(Channel, NewUserId());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("set-b", body.GetProperty("activeEmoteSetId").GetString());

        var sets = body.GetProperty("sets").EnumerateArray().ToList();
        Assert.Equal(3, sets.Count);
        // The active set first, regardless of name — then ordinal by name among the rest.
        Assert.Equal("set-b", sets[0].GetProperty("id").GetString());
        Assert.True(sets[0].GetProperty("isActive").GetBoolean());
        Assert.Equal("set-a", sets[1].GetProperty("id").GetString());
        Assert.Equal("set-c", sets[2].GetProperty("id").GetString());
        // observations is always [] on this route — no Channel row, no ChannelEmoteSetObservation
        // rows, ever (spec 6.3).
        Assert.Empty(sets[0].GetProperty("observations").EnumerateArray());
    }

    [Fact]
    public async Task EmoteSets_NullSevenTvActiveEmoteSetId_MapsToEmptyStringOnTheWire_NotNull()
    {
        _factory.ForeignEmoteSet.GetForeignEmoteSetListAsync(Channel, Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetListLookupResult.Ok(new EmoteSetList(
                null, [new EmoteSetSummary("set-a", "Alpha", 1000, "NORMAL", false, "Owner")], "7tv-owner-1")));

        var response = await SendSetsAsync(Channel, NewUserId());

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.String, body.GetProperty("activeEmoteSetId").ValueKind);
        Assert.Equal(string.Empty, body.GetProperty("activeEmoteSetId").GetString());
        // No set can ever have id "", so none of them is reported active.
        Assert.False(body.GetProperty("sets")[0].GetProperty("isActive").GetBoolean());
    }

    [Theory]
    [InlineData(ForeignEmoteSetListLookupStatus.ChannelNotOnTwitch, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotOnTwitch)]
    [InlineData(ForeignEmoteSetListLookupStatus.TwitchUnavailable, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelTwitchUnavailable)]
    // Deliberately 404, not the 200-with-empty-list a tracked channel's own /emote-sets route (6.1)
    // gives the same underlying EmoteSetListStatus.NoSevenTvAccount — see the status enum's own doc.
    [InlineData(ForeignEmoteSetListLookupStatus.NoSevenTvAccount, HttpStatusCode.NotFound, ApiErrorCodes.ForeignChannelNoSevenTvAccount)]
    [InlineData(ForeignEmoteSetListLookupStatus.SevenTvUnavailable, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelSevenTvUnavailable)]
    [InlineData(ForeignEmoteSetListLookupStatus.SevenTvRateLimited, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelSevenTvUnavailable)]
    public async Task EmoteSets_EveryFailureStatus_MapsToItsDocumentedResponse(
        ForeignEmoteSetListLookupStatus status, HttpStatusCode expectedStatusCode, string expectedErrorCode)
    {
        _factory.ForeignEmoteSet.GetForeignEmoteSetListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetListLookupResult.Failed(status));

        var response = await SendSetsAsync(Channel, NewUserId());

        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal(expectedErrorCode, await ReadErrorCodeAsync(response));
    }

    private async Task<HttpResponseMessage> SendSetsAsync(string channelName, string? userId)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/seventv/channels/{channelName}/emote-sets");
        if (userId is not null)
        {
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        }

        return await client.SendAsync(request);
    }

    // AK 25 (spec 2026-09-20, 6.2): GET /api/seventv/me/emote-set-targets.

    [Fact]
    public async Task EmoteSetTargets_ListsOwnAccountFirst_ThenEditorAccountsOrdinalByLogin_WithTrackedChannelNameWhenApplicable()
    {
        const string ownTwitchId = "1000";
        const string ownLogin = "owner";

        _factory.EditorService.GetEditorGrantsAsync(ownTwitchId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Ok(new SevenTvEditorGrants(
                new HashSet<string> { "trackedmod", "untrackedmod" },
                new HashSet<string> { "2000", "3000" },
                [
                    new SevenTvEditorGrantEntry("untrackedmod", "3000"),
                    new SevenTvEditorGrantEntry("trackedmod", "2000"),
                ])));

        _factory.Channels.GetActiveByTwitchChannelIdAsync(ownTwitchId, Arg.Any<CancellationToken>())
            .Returns((Channel?)null);
        _factory.Channels.GetActiveByTwitchChannelIdAsync("2000", Arg.Any<CancellationToken>())
            .Returns(new Channel
            {
                ChannelName = "trackedmod",
                TwitchChannelId = "2000",
                ActiveEmoteSetId = "set-tracked",
                IsBotActive = true,
            });
        _factory.Channels.GetActiveByTwitchChannelIdAsync("3000", Arg.Any<CancellationToken>())
            .Returns((Channel?)null);

        _factory.EmoteSetList.ListByTwitchIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(EmoteSetListResult.Ok(new EmoteSetList(null, [])));

        var response = await SendMeAsync(ownTwitchId, ownLogin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var accounts = body.GetProperty("accounts").EnumerateArray().ToList();

        Assert.Equal(3, accounts.Count);
        Assert.True(accounts[0].GetProperty("isOwnAccount").GetBoolean());
        Assert.Equal(ownLogin, accounts[0].GetProperty("twitchLogin").GetString());
        Assert.Equal(JsonValueKind.Null, accounts[0].GetProperty("trackedChannelName").ValueKind);

        // editor_of accounts ordinal by twitchLogin: "trackedmod" before "untrackedmod", even though
        // the grants came back in the opposite order above.
        Assert.Equal("trackedmod", accounts[1].GetProperty("twitchLogin").GetString());
        Assert.False(accounts[1].GetProperty("isOwnAccount").GetBoolean());
        Assert.Equal("trackedmod", accounts[1].GetProperty("trackedChannelName").GetString());

        Assert.Equal("untrackedmod", accounts[2].GetProperty("twitchLogin").GetString());
        Assert.Equal(JsonValueKind.Null, accounts[2].GetProperty("trackedChannelName").ValueKind);

        Assert.False(body.GetProperty("sevenTvUnavailable").GetBoolean());
    }

    [Fact]
    public async Task EmoteSetTargets_Answers_OnlyTheOwnAccount_AndSevenTvUnavailableTrue_WhenTheGrantsLookupFails()
    {
        const string ownTwitchId = "5000";

        _factory.EditorService.GetEditorGrantsAsync(ownTwitchId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Failed(SevenTvLookupStatus.Unavailable));
        _factory.Channels.GetActiveByTwitchChannelIdAsync(ownTwitchId, Arg.Any<CancellationToken>())
            .Returns((Channel?)null);
        _factory.EmoteSetList.ListByTwitchIdAsync(ownTwitchId, Arg.Any<CancellationToken>())
            .Returns(EmoteSetListResult.Ok(new EmoteSetList(null, [])));

        var response = await SendMeAsync(ownTwitchId, "owner");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var accounts = body.GetProperty("accounts").EnumerateArray().ToList();

        var account = Assert.Single(accounts);
        Assert.True(account.GetProperty("isOwnAccount").GetBoolean());
        Assert.True(body.GetProperty("sevenTvUnavailable").GetBoolean());
    }

    [Fact]
    public async Task EmoteSetTargets_MarksTheAccountSetsUnavailable_WhenItsOwnListLookupFails()
    {
        const string ownTwitchId = "6000";

        _factory.EditorService.GetEditorGrantsAsync(ownTwitchId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Ok(new SevenTvEditorGrants(new HashSet<string>(), new HashSet<string>())));
        _factory.Channels.GetActiveByTwitchChannelIdAsync(ownTwitchId, Arg.Any<CancellationToken>())
            .Returns((Channel?)null);
        _factory.EmoteSetList.ListByTwitchIdAsync(ownTwitchId, Arg.Any<CancellationToken>())
            .Returns(EmoteSetListResult.Failed(EmoteSetListStatus.Unavailable));

        var response = await SendMeAsync(ownTwitchId, "owner");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var account = Assert.Single(body.GetProperty("accounts").EnumerateArray());

        Assert.True(account.GetProperty("setsUnavailable").GetBoolean());
        Assert.Empty(account.GetProperty("sets").EnumerateArray());
        Assert.True(body.GetProperty("sevenTvUnavailable").GetBoolean());
    }

    private static string NewUserId() => Guid.NewGuid().ToString("N");

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }

    private async Task<HttpResponseMessage> SendAsync(
        string channelName, string? userId, bool refresh = false, string? emoteSetId = null)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var query = new List<string>();
        if (refresh)
        {
            query.Add("refresh=true");
        }

        if (emoteSetId is not null)
        {
            query.Add($"emoteSetId={emoteSetId}");
        }

        var path = $"/api/seventv/channels/{channelName}/emotes" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (userId is not null)
        {
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        }

        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendMeAsync(string? userId, string? login = "someuser")
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/seventv/me/emote-set-targets");
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
