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
/// <c>GET /api/seventv/me/emote-set-targets/{emoteSetId}</c> (owner-hint design 3.4): middleware
/// (401/429) -&gt; <c>EmoteSetIdValidationFilter</c> (400) -&gt; the pre-check mode of the owner check
/// -&gt; always 200 with one of four statuses, and the target only on <c>editable</c>.
/// </summary>
/// <remarks>
/// The owner check runs for real, as in <see cref="SevenTvEmoteSetSyncImportedEndpointTests"/>: each
/// case arranges the set lists and the guarded grants the way they would produce its status, rather
/// than stubbing the status. Every case also pins what the pre-check must never do — ask 7TV about
/// the set directly, or read the grants the unguarded way.
/// </remarks>
public class SevenTvEmoteSetPreCheckEndpointTests : IClassFixture<ApiFactory>
{
    private const string EmoteSetId = "01GV88A38G0006FW5TVZVMG507";
    private const string ActorLogin = "someuser";
    private const string ActorSevenTvUserId = "actor-seven-tv-id";
    private const string OwnerSevenTvUserId = "owner-seven-tv-id";
    private const string OwnerTwitchId = "49140130";
    private const string OwnerTwitchLogin = "handofblood";
    private const string OtherSevenTvUserId = "other-seven-tv-id";
    private const string OtherTwitchId = "other-twitch-id";
    private const string OtherTwitchLogin = "otherlogin";

    private readonly ApiFactory _factory;

    public SevenTvEmoteSetPreCheckEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.EditorService.ClearReceivedCalls();
        _factory.GuardedEditorGrants.ClearReceivedCalls();
        _factory.EmoteSetList.ClearReceivedCalls();
        _factory.SevenTvApi.ClearReceivedCalls();
        _factory.Channels.ClearReceivedCalls();

        // No tracked channel unless a case says otherwise; a case that needs one stubs its id after.
        _factory.Channels.GetActiveByTwitchChannelIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Channel?)null);
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
        // 33 characters, one over E14's limit — the same shape as the set-centric import's case.
        var response = await SendAsync(new string('a', 33), NewUserId());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidEmoteSetId, await ReadErrorCodeAsync(response));
        await AssertOwnerCheckDidNotRunAsync();
    }

    [Fact]
    public async Task ActorsOwnNormalSet_IsEditable_WithTheActorAsTarget()
    {
        // Untracked actor, so the list's own opinion of "active" decides isActiveSet.
        var userId = NewUserId();
        _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SetList(ActorSevenTvUserId, activeEmoteSetId: EmoteSetId, (EmoteSetId, ActorSevenTvUserId, "NORMAL")));

        var response = await SendAsync(EmoteSetId, userId);

        var body = await ReadOkBodyAsync(response);
        Assert.Equal("editable", body.GetProperty("status").GetString());
        var target = body.GetProperty("target");
        Assert.Equal(EmoteSetId, target.GetProperty("emoteSetId").GetString());
        Assert.Equal("Some Set", target.GetProperty("setName").GetString());
        Assert.Equal("Some Owner", target.GetProperty("ownerDisplayName").GetString());
        Assert.Equal(ActorLogin, target.GetProperty("twitchLogin").GetString());
        Assert.Equal(userId, target.GetProperty("twitchChannelId").GetString());
        Assert.Equal(JsonValueKind.Null, target.GetProperty("trackedChannelName").ValueKind);
        Assert.True(target.GetProperty("isActiveSet").GetBoolean());
        await AssertNoLookupAndNoUnguardedGrantsAsync();
    }

    [Fact]
    public async Task GrantOwnedSet_OnATrackedChannel_NamesTheTrackedChannel_AndItsObservedActiveSetWins()
    {
        // 7TV's list says another set is active; the tracked channel's own observation says this one
        // is — the Channel row wins, exactly as on the target list (E7/E21).
        var userId = NewUserId();
        _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SetList(ActorSevenTvUserId));
        _factory.EmoteSetList.ListByTwitchIdAsync(OwnerTwitchId, Arg.Any<CancellationToken>())
            .Returns(SetList(OwnerSevenTvUserId, activeEmoteSetId: "01SOMEOTHERSET", (EmoteSetId, OwnerSevenTvUserId, "NORMAL")));
        ArrangeGrants(userId, (OwnerTwitchLogin, OwnerTwitchId));
        _factory.Channels.GetActiveByTwitchChannelIdAsync(OwnerTwitchId, Arg.Any<CancellationToken>())
            .Returns(new Channel
            {
                ChannelName = OwnerTwitchLogin,
                TwitchChannelId = OwnerTwitchId,
                ActiveEmoteSetId = EmoteSetId,
                IsBotActive = true,
            });

        var response = await SendAsync(EmoteSetId, userId);

        var body = await ReadOkBodyAsync(response);
        Assert.Equal("editable", body.GetProperty("status").GetString());
        var target = body.GetProperty("target");
        Assert.Equal(OwnerTwitchLogin, target.GetProperty("twitchLogin").GetString());
        Assert.Equal(OwnerTwitchId, target.GetProperty("twitchChannelId").GetString());
        Assert.Equal(OwnerTwitchLogin, target.GetProperty("trackedChannelName").GetString());
        Assert.True(target.GetProperty("isActiveSet").GetBoolean());
        await AssertNoLookupAndNoUnguardedGrantsAsync();
    }

    [Fact]
    public async Task SetListedUnderOneGrant_ButOwnedByAnother_NamesTheOwner_NeverTheListingAccount()
    {
        // The set sits in A's list (A = "other", read first) but belongs to B (the owner), whose own
        // list does not carry it. The route names B throughout: B's login and id, B's tracked channel
        // — and A's active set, although it is this very set, never makes it B's active one.
        var userId = NewUserId();
        _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SetList(ActorSevenTvUserId));
        _factory.EmoteSetList.ListByTwitchIdAsync(OtherTwitchId, Arg.Any<CancellationToken>())
            .Returns(SetList(OtherSevenTvUserId, activeEmoteSetId: EmoteSetId, (EmoteSetId, OwnerSevenTvUserId, "NORMAL")));
        _factory.EmoteSetList.ListByTwitchIdAsync(OwnerTwitchId, Arg.Any<CancellationToken>())
            .Returns(SetList(OwnerSevenTvUserId));
        ArrangeGrants(userId, (OtherTwitchLogin, OtherTwitchId), (OwnerTwitchLogin, OwnerTwitchId));

        var response = await SendAsync(EmoteSetId, userId);

        var body = await ReadOkBodyAsync(response);
        Assert.Equal("editable", body.GetProperty("status").GetString());
        var target = body.GetProperty("target");
        Assert.Equal(OwnerTwitchLogin, target.GetProperty("twitchLogin").GetString());
        Assert.Equal(OwnerTwitchId, target.GetProperty("twitchChannelId").GetString());
        Assert.False(target.GetProperty("isActiveSet").GetBoolean());
        await _factory.Channels.Received(1).GetActiveByTwitchChannelIdAsync(OwnerTwitchId, Arg.Any<CancellationToken>());
        await _factory.Channels.DidNotReceive().GetActiveByTwitchChannelIdAsync(OtherTwitchId, Arg.Any<CancellationToken>());
        await AssertNoLookupAndNoUnguardedGrantsAsync();
    }

    [Fact]
    public async Task ActorsOwnPersonalSet_IsNotSelectable_WithoutATarget()
    {
        // Editable by ownership, but only NORMAL sets are targets — kind first, like the client.
        var userId = NewUserId();
        _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SetList(ActorSevenTvUserId, activeEmoteSetId: null, (EmoteSetId, ActorSevenTvUserId, "PERSONAL")));

        var response = await SendAsync(EmoteSetId, userId);

        var body = await ReadOkBodyAsync(response);
        Assert.Equal("notSelectable", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("target").ValueKind);
        await _factory.Channels.DidNotReceive().GetActiveByTwitchChannelIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await AssertNoLookupAndNoUnguardedGrantsAsync();
    }

    [Fact]
    public async Task SetInNoList_IsNotEditable_WithoutAnOwnerLookup()
    {
        // Where the report would ask 7TV about the set once, the pre-check never does (F16).
        var userId = NewUserId();
        _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SetList(ActorSevenTvUserId));
        ArrangeGrants(userId);

        var response = await SendAsync(EmoteSetId, userId);

        var body = await ReadOkBodyAsync(response);
        Assert.Equal("notEditable", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("target").ValueKind);
        await AssertNoLookupAndNoUnguardedGrantsAsync();
    }

    [Fact]
    public async Task SetListedOnlyUnderAForeignOwner_IsNotEditable()
    {
        var userId = NewUserId();
        _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SetList(ActorSevenTvUserId, activeEmoteSetId: null, (EmoteSetId, "foreign-seven-tv-id", "NORMAL")));
        ArrangeGrants(userId);

        var response = await SendAsync(EmoteSetId, userId);

        var body = await ReadOkBodyAsync(response);
        Assert.Equal("notEditable", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("target").ValueKind);
        await AssertNoLookupAndNoUnguardedGrantsAsync();
    }

    [Fact]
    public async Task HeldGrantFailure_IsUnavailable_EvenThoughTheActorsOwnListWasRead()
    {
        // The guarded grants hold a failure (7tveditorhold:) and answer Unavailable: one of the
        // grants may own the set, so the answer is "unknown", not "no" — stricter than the picker,
        // whose unguarded list would still have shown the actor's own account.
        var userId = NewUserId();
        _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SetList(ActorSevenTvUserId));
        _factory.GuardedEditorGrants.GetEditorGrantsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Failed(SevenTvLookupStatus.Unavailable));

        var response = await SendAsync(EmoteSetId, userId);

        var body = await ReadOkBodyAsync(response);
        Assert.Equal("unavailable", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("target").ValueKind);
        await _factory.GuardedEditorGrants.Received(1).GetEditorGrantsAsync(userId, Arg.Any<CancellationToken>());
        await AssertNoLookupAndNoUnguardedGrantsAsync();
    }

    [Fact]
    public async Task OwnerTwitchIdHint_ReadsTheHintedGrantsList_AndSkipsTheGrantBeforeIt()
    {
        // Without a hint the walk would read "other" first; the hint sends it to the owner's list
        // alongside the actor's own, and "other" is never read. That is how the query reaches the
        // service — observable without substituting it.
        var userId = NewUserId();
        ArrangeOwnerAsSecondOfTwoGrants(userId);

        var response = await SendAsync(EmoteSetId, userId, query: $"ownerTwitchId={OwnerTwitchId}");

        var body = await ReadOkBodyAsync(response);
        Assert.Equal("editable", body.GetProperty("status").GetString());
        Assert.Equal(OwnerTwitchId, body.GetProperty("target").GetProperty("twitchChannelId").GetString());
        await _factory.EmoteSetList.Received(1).ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>());
        await _factory.EmoteSetList.Received(1).ListByTwitchIdAsync(OwnerTwitchId, Arg.Any<CancellationToken>());
        await _factory.EmoteSetList.DidNotReceive().ListByTwitchIdAsync(OtherTwitchId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OwnerLoginHint_ResolvesToTheMatchingGrant_AfterNormalization()
    {
        // A login only ever resolves to the grant's Twitch id; case and padding do not matter
        // (rule 9).
        var userId = NewUserId();
        ArrangeOwnerAsSecondOfTwoGrants(userId);

        var response = await SendAsync(EmoteSetId, userId, query: "ownerLogin=%20HandOfBlood%20");

        var body = await ReadOkBodyAsync(response);
        Assert.Equal("editable", body.GetProperty("status").GetString());
        await _factory.EmoteSetList.Received(1).ListByTwitchIdAsync(OwnerTwitchId, Arg.Any<CancellationToken>());
        await _factory.EmoteSetList.DidNotReceive().ListByTwitchIdAsync(OtherTwitchId, Arg.Any<CancellationToken>());
    }

    [Theory]
    // Neither the actor nor one of their grants: dropped before any request, the walk runs as without.
    [InlineData("ownerTwitchId=not-one-of-my-grants")]
    // Blank: no hint.
    [InlineData("ownerTwitchId=&ownerLogin=")]
    public async Task UnusableHint_IsDropped_AndTheWalkReadsEveryGrantInOrder(string query)
    {
        var userId = NewUserId();
        ArrangeOwnerAsSecondOfTwoGrants(userId);

        var response = await SendAsync(EmoteSetId, userId, query: query);

        var body = await ReadOkBodyAsync(response);
        Assert.Equal("editable", body.GetProperty("status").GetString());
        Assert.Equal(OwnerTwitchId, body.GetProperty("target").GetProperty("twitchChannelId").GetString());
        Received.InOrder(() =>
        {
            _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>());
            _factory.EmoteSetList.ListByTwitchIdAsync(OtherTwitchId, Arg.Any<CancellationToken>());
            _factory.EmoteSetList.ListByTwitchIdAsync(OwnerTwitchId, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task OverlongHint_IsNoHint_EvenWhenItWouldNameAGrant_AndNever400()
    {
        // The 64-character cap drops the value before the service sees it: a grant whose id is that
        // long (artificial, but the only way to tell the cap from "names no grant") is still read in
        // walk order, after "other", as without a hint.
        var longOwnerTwitchId = new string('9', 65);
        var userId = NewUserId();
        ArrangeOwnerAsSecondOfTwoGrants(userId, longOwnerTwitchId);

        var response = await SendAsync(EmoteSetId, userId, query: $"ownerTwitchId={longOwnerTwitchId}");

        var body = await ReadOkBodyAsync(response);
        Assert.Equal("editable", body.GetProperty("status").GetString());
        Assert.Equal(longOwnerTwitchId, body.GetProperty("target").GetProperty("twitchChannelId").GetString());
        Received.InOrder(() =>
        {
            _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>());
            _factory.EmoteSetList.ListByTwitchIdAsync(OtherTwitchId, Arg.Any<CancellationToken>());
            _factory.EmoteSetList.ListByTwitchIdAsync(longOwnerTwitchId, Arg.Any<CancellationToken>());
        });
    }

    private void ArrangeGrants(string userId, params (string Login, string TwitchId)[] grants) =>
        _factory.GuardedEditorGrants.GetEditorGrantsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Ok(new SevenTvEditorGrants(
                grants.Select(grant => grant.Login).ToHashSet(),
                grants.Select(grant => grant.TwitchId).ToHashSet(),
                [.. grants.Select(grant => new SevenTvEditorGrantEntry(grant.Login, grant.TwitchId))])));

    /// <summary>
    /// The actor owns nothing; of two grants, "other" comes first in walk order and owns nothing
    /// either, the owner comes second and owns the set.
    /// </summary>
    private void ArrangeOwnerAsSecondOfTwoGrants(string userId, string ownerTwitchId = OwnerTwitchId)
    {
        _factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SetList(ActorSevenTvUserId));
        _factory.EmoteSetList.ListByTwitchIdAsync(OtherTwitchId, Arg.Any<CancellationToken>())
            .Returns(SetList(OtherSevenTvUserId));
        _factory.EmoteSetList.ListByTwitchIdAsync(ownerTwitchId, Arg.Any<CancellationToken>())
            .Returns(SetList(OwnerSevenTvUserId, activeEmoteSetId: null, (EmoteSetId, OwnerSevenTvUserId, "NORMAL")));
        ArrangeGrants(userId, (OtherTwitchLogin, OtherTwitchId), (OwnerTwitchLogin, ownerTwitchId));
    }

    private async Task AssertOwnerCheckDidNotRunAsync()
    {
        await _factory.EmoteSetList.DidNotReceive().ListByTwitchIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.GuardedEditorGrants.DidNotReceive().GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await AssertNoLookupAndNoUnguardedGrantsAsync();
    }

    /// <summary>
    /// The two things the pre-check never does: ask 7TV about the set directly (only the report
    /// falls back to that), and read the grants through the unguarded authorization path.
    /// </summary>
    private async Task AssertNoLookupAndNoUnguardedGrantsAsync()
    {
        await _factory.SevenTvApi.DidNotReceive().LookUpEmoteSetOwnerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.EditorService.DidNotReceive().GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private async Task<HttpResponseMessage> SendAsync(string emoteSetId, string? userId, string? query = null)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var path = $"/api/seventv/me/emote-set-targets/{emoteSetId}" + (query is null ? "" : "?" + query);
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (userId is not null)
        {
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            request.Headers.Add(TestAuthHandler.LoginHeader, ActorLogin);
        }

        return await client.SendAsync(request);
    }

    private static EmoteSetListResult SetList(string accountSevenTvUserId) =>
        SetList(accountSevenTvUserId, activeEmoteSetId: null);

    private static EmoteSetListResult SetList(
        string accountSevenTvUserId, string? activeEmoteSetId, params (string Id, string OwnerSevenTvUserId, string Kind)[] sets) =>
        EmoteSetListResult.Ok(new EmoteSetList(
            activeEmoteSetId,
            [.. sets.Select(set => new EmoteSetSummary(
                set.Id, "Some Set", 1000, set.Kind, set.Kind == "PERSONAL", "Some Owner", set.OwnerSevenTvUserId))],
            accountSevenTvUserId));

    private static string NewUserId() => Guid.NewGuid().ToString("N");

    private static async Task<JsonElement> ReadOkBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }
}
