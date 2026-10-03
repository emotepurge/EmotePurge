using System.Net;
using System.Text;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Issue #43: 7TV is rolling out (announced, no fixed date) nulling the embedded <c>emote_set</c>
/// object out of <c>GET users/twitch/:id</c> — measured live 2026-09-01, already null for every
/// checked channel while <c>emote_set_id</c> (top-level and per-connection) stays populated. These
/// tests pin down <see cref="SevenTvApiClient.GetChannelStateForTwitchUserAsync"/>'s fallback:
/// resolve a usable set id and reload it from <c>GET emote-sets/:id</c>, without regressing the
/// "one request when emote_set is still present" case or the existing NoActiveEmoteSet/Unavailable
/// distinction from issue #32.
/// </summary>
public class SevenTvApiClientChannelStateTests
{
    private const string TwitchUserId = "36340781";

    // Answered for every test: the v4 addedAt overlay only depends on the resolved set id, runs in
    // both branches, and must not itself produce warnings that would muddy the log-line assertion.
    private const string HarmlessV4GqlPayload =
        """{"data":{"emote_sets":{"emote_set":{"emotes":{"page_count":1,"items":[]}}}}}""";

    [Fact]
    public async Task EmoteSetPresent_ReturnsOk_WithoutAnyEmoteSetsRequest()
    {
        const string userPayload =
            """{"emote_set":{"id":"SET1","capacity":600,"emotes":[]},"user":{"id":"USER1","connections":[]}}""";
        var handler = CreateHandler(userPayload);
        var client = CreateClient(handler);

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        Assert.NotNull(result.State);
        Assert.Equal("SET1", result.State!.EmoteSet.Id);
        Assert.Equal(0, handler.CountFor("emote-sets"));
    }

    [Fact]
    public async Task EmoteSetNull_TopLevelEmoteSetId_FetchesEmoteSetOnce()
    {
        const string userPayload =
            """{"emote_set":null,"emote_set_id":"SET2","user":{"id":"USER1","connections":[]}}""";
        const string emoteSetPayload =
            """{"id":"SET2","capacity":700,"emotes":[{"id":"E1","name":"Foo"}]}""";
        var handler = CreateHandler(userPayload, ("emote-sets", emoteSetPayload));
        var client = CreateClient(handler);

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        Assert.NotNull(result.State);
        Assert.Equal("SET2", result.State!.EmoteSet.Id);
        Assert.Equal(700, result.State.EmoteSet.Capacity);
        Assert.Single(result.State.EmoteSet.Emotes);
        Assert.Equal(1, handler.CountFor("emote-sets"));
    }

    [Fact]
    public async Task EmoteSetNull_NoTopLevelId_MatchingTwitchConnection_FallsBack()
    {
        var userPayload =
            "{\"emote_set\":null,\"user\":{\"id\":\"USER1\",\"connections\":[" +
            "{\"platform\":\"TWITCH\",\"id\":\"" + TwitchUserId + "\",\"emote_set_id\":\"SET3\"}]}}";
        const string emoteSetPayload = """{"id":"SET3","capacity":500,"emotes":[]}""";
        var handler = CreateHandler(userPayload, ("emote-sets", emoteSetPayload));
        var client = CreateClient(handler);

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        Assert.NotNull(result.State);
        Assert.Equal("SET3", result.State!.EmoteSet.Id);
        Assert.Equal(1, handler.CountFor("emote-sets"));
    }

    [Fact]
    public async Task EmoteSetNull_OnlyNonTwitchConnectionWithSetId_IsNoActiveEmoteSet()
    {
        const string userPayload =
            """
            {"emote_set":null,"user":{"id":"USER1","connections":[
                {"platform":"YOUTUBE","id":"yt-1","emote_set_id":"SET4"}
            ]}}
            """;
        var handler = CreateHandler(userPayload);
        var client = CreateClient(handler);

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.NoActiveEmoteSet, result.Status);
        Assert.Null(result.State);
        Assert.Equal(0, handler.CountFor("emote-sets"));
    }

    /// <summary>
    /// A second TWITCH connection on the same 7TV account belongs to a *different* channel. It can
    /// only ever be reached once the requested connection has no set of its own — which is precisely
    /// what NoActiveEmoteSet reports. Syncing the other one instead would persist a foreign set id
    /// and reconcile a foreign channel's emotes into this channel, where voting and the 7TV mass
    /// delete would then act on it.
    /// </summary>
    [Fact]
    public async Task EmoteSetNull_OtherTwitchConnectionHasSetId_IsNoActiveEmoteSet()
    {
        var userPayload =
            "{\"emote_set\":null,\"user\":{\"id\":\"USER1\",\"connections\":[" +
            "{\"platform\":\"TWITCH\",\"id\":\"" + TwitchUserId + "\",\"emote_set_id\":null}," +
            "{\"platform\":\"TWITCH\",\"id\":\"other-twitch-id\",\"emote_set_id\":\"FOREIGN_SET\"}]}}";
        var handler = CreateHandler(userPayload);
        var client = CreateClient(handler);

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.NoActiveEmoteSet, result.Status);
        Assert.Null(result.State);
        Assert.Equal(0, handler.CountFor("emote-sets"));
    }

    [Fact]
    public async Task EmoteSetNull_NoIdAnywhere_IsNoActiveEmoteSet()
    {
        const string userPayload = """{"emote_set":null,"user":{"id":"USER1","connections":[]}}""";
        var handler = CreateHandler(userPayload);
        var client = CreateClient(handler);

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.NoActiveEmoteSet, result.Status);
        Assert.Null(result.State);
        Assert.Equal(0, handler.CountFor("emote-sets"));
    }

    [Fact]
    public async Task EmoteSetNull_TopLevelIdIsNullObjectIdSentinel_IsNoActiveEmoteSet()
    {
        const string userPayload =
            """{"emote_set":null,"emote_set_id":"000000000000000000000000","user":{"id":"USER1","connections":[]}}""";
        var handler = CreateHandler(userPayload);
        var client = CreateClient(handler);

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.NoActiveEmoteSet, result.Status);
        Assert.Null(result.State);
        Assert.Equal(0, handler.CountFor("emote-sets"));
    }

    [Fact]
    public async Task EmoteSetReloadFails_500_ReturnsUnavailable_NotNoActiveEmoteSet()
    {
        const string userPayload =
            """{"emote_set":null,"emote_set_id":"SET5","user":{"id":"USER1","connections":[]}}""";
        var handler = CreateHandler(userPayload, statusFor: HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task EmoteSetReloadFails_404_ReturnsUnavailable_NotNoActiveEmoteSet()
    {
        const string userPayload =
            """{"emote_set":null,"emote_set_id":"SET6","user":{"id":"USER1","connections":[]}}""";
        var handler = CreateHandler(userPayload, statusFor: HttpStatusCode.NotFound);
        var client = CreateClient(handler);

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task FallbackPath_LeavesALogLine()
    {
        const string userPayload =
            """{"emote_set":null,"emote_set_id":"SET7","user":{"id":"USER1","connections":[]}}""";
        const string emoteSetPayload = """{"id":"SET7","capacity":500,"emotes":[]}""";
        var handler = CreateHandler(userPayload, ("emote-sets", emoteSetPayload));
        var logger = new RecordingLogger<SevenTvApiClient>();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") };
        var client = new SevenTvApiClient(httpClient, new RecordingRateLimitTelemetry(), new RecordingForeignUpstreamRequestBudget(), logger);

        await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        // Robust to whether the process-wide once-per-process latch already fired in an earlier
        // test in this run: assert a fallback-path line exists, regardless of its level.
        Assert.Contains(logger.Entries, e => e.Message.Contains("lade Set separat", StringComparison.Ordinal));
    }

    // Pins the REAL shape of a genuinely empty 7TV set: the upstream compat model skips an empty
    // `emotes` list and a default `emote_count`, so both keys are simply absent. This must stay Ok
    // with zero emotes (the sync's wipe guard decides what to do with it) — "fixing" a missing
    // list into Unavailable would make a truly empty set unsyncable forever (issue #76).
    [Fact]
    public async Task EmoteSetWithoutEmotesAndCount_IsOk_WithZeroEmotes()
    {
        const string userPayload = """{"emote_set":{"id":"SET","capacity":600},"user":{"id":"USER1","connections":[]}}""";
        var client = CreateClient(CreateHandler(userPayload));

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        Assert.Empty(result.State!.EmoteSet.Emotes);
    }

    [Fact]
    public async Task PositiveEmoteCountWithEmptyEmotes_IsUnavailable()
    {
        const string userPayload = """{"emote_set":{"id":"SET","capacity":600,"emote_count":5,"emotes":[]},"user":{"id":"USER1","connections":[]}}""";
        var client = CreateClient(CreateHandler(userPayload));

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task ExplicitNullEmotes_IsUnavailable()
    {
        const string userPayload = """{"emote_set":{"id":"SET","capacity":600,"emotes":null},"user":{"id":"USER1","connections":[]}}""";
        var client = CreateClient(CreateHandler(userPayload));

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task ReloadedSetWithPositiveEmoteCountAndNoEmotes_IsUnavailable()
    {
        const string userPayload = """{"emote_set":null,"emote_set_id":"SET2","user":{"id":"USER1","connections":[]}}""";
        const string emoteSetPayload = """{"id":"SET2","capacity":700,"emote_count":3}""";
        var client = CreateClient(CreateHandler(userPayload, ("emote-sets", emoteSetPayload)));

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status);
    }

    // Issue #76: the v4 entry count is the cross-check against a v3 zero, so it has to arrive
    // exactly when v3 lists nothing. Counted over every page; an entry without a resolved emote is
    // not counted, an entry without a date is.
    [Fact]
    public async Task V4EntryCount_IsReportedOverAllPages_EvenWhenV3ListsNoEmotes()
    {
        const string userPayload = """{"emote_set":{"id":"SET","capacity":600},"user":{"id":"USER1","connections":[]}}""";
        string[] pages =
        [
            """{"data":{"emote_sets":{"emote_set":{"emotes":{"page_count":2,"items":[{"added_at":"2026-09-01T10:00:00Z","emote":{"id":"a"}},{"added_at":null,"emote":{"id":"b"}}]}}}}}""",
            """{"data":{"emote_sets":{"emote_set":{"emotes":{"page_count":2,"items":[{"added_at":"2026-09-02T10:00:00Z","emote":{"id":"c"}},{"added_at":"2026-09-03T10:00:00Z","emote":null}]}}}}}""",
        ];
        var page = 0;
        var client = CreateClient(CreateHandler(userPayload, v4Response: () => JsonResponse(HttpStatusCode.OK, pages[page++])));

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        Assert.Empty(result.State!.EmoteSet.Emotes);
        Assert.Equal(3, result.State.EmoteSet.RemoteEntryCount);
    }

    [Fact]
    public async Task V4ListingNoEntries_ReportsZero()
    {
        const string userPayload = """{"emote_set":{"id":"SET","capacity":600},"user":{"id":"USER1","connections":[]}}""";
        var client = CreateClient(CreateHandler(userPayload));

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(0, result.State!.EmoteSet.RemoteEntryCount);
    }

    public static TheoryData<HttpStatusCode, string> FailedV4Answers => new()
    {
        { HttpStatusCode.InternalServerError, "" },
        { HttpStatusCode.OK, """{"data":{"emote_sets":{"emote_set":null}}}""" },
        { HttpStatusCode.OK, """{"data":null,"errors":[{"message":"rate limited","extensions":{"status":429}}]}""" },
        { HttpStatusCode.OK, "{not json" },
    };

    [Theory]
    [MemberData(nameof(FailedV4Answers))]
    public async Task V4LookupThatFails_LeavesRemoteEntryCountUnknown(HttpStatusCode status, string payload)
    {
        const string userPayload = """{"emote_set":{"id":"SET","capacity":600},"user":{"id":"USER1","connections":[]}}""";
        var client = CreateClient(CreateHandler(userPayload, v4Response: () => JsonResponse(status, payload)));

        var result = await client.GetChannelStateForTwitchUserAsync(TwitchUserId);

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        Assert.Null(result.State!.EmoteSet.RemoteEntryCount);
    }

    private static RoutingStubHandler CreateHandler(
        string userPayload,
        (string PathPrefix, string Payload)? emoteSetRoute = null,
        HttpStatusCode statusFor = HttpStatusCode.OK,
        Func<HttpResponseMessage>? v4Response = null)
    {
        var routes = new List<(string Prefix, Func<HttpResponseMessage> Factory)>
        {
            ("users/twitch/", () => JsonResponse(HttpStatusCode.OK, userPayload)),
            ("/v4/gql", v4Response ?? (() => JsonResponse(HttpStatusCode.OK, HarmlessV4GqlPayload))),
        };

        if (emoteSetRoute is { } route)
        {
            routes.Add(("emote-sets", () => JsonResponse(HttpStatusCode.OK, route.Payload)));
        }
        else if (statusFor != HttpStatusCode.OK)
        {
            routes.Add(("emote-sets", () => new HttpResponseMessage(statusFor)));
        }

        return new RoutingStubHandler(routes);
    }

    private static SevenTvApiClient CreateClient(RoutingStubHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") };
        return new SevenTvApiClient(httpClient, new RecordingRateLimitTelemetry(), new RecordingForeignUpstreamRequestBudget(), new RecordingLogger<SevenTvApiClient>());
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string payload) =>
        new(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Unlike <see cref="SevenTvApiClientResolveIdentityTests"/>'s single-answer stub, this one
    /// routes by request path (matched against a substring of the absolute path, checked in
    /// registration order) and counts requests per route — needed here because the same test often
    /// has to assert "and no emote-sets request happened" for the no-extra-request cases.
    /// </summary>
    private sealed class RoutingStubHandler(List<(string Prefix, Func<HttpResponseMessage> Factory)> routes) : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _requestCounts = [];

        public int CountFor(string prefix) => _requestCounts.GetValueOrDefault(prefix);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsoluteUri;
            foreach (var (prefix, factory) in routes)
            {
                if (path.Contains(prefix, StringComparison.Ordinal))
                {
                    _requestCounts[prefix] = _requestCounts.GetValueOrDefault(prefix) + 1;
                    return Task.FromResult(factory());
                }
            }

            throw new InvalidOperationException($"Kein Route-Stub für Request an {path}.");
        }
    }
}
