using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The bot account's editor read (#487 plan 2.5b, Revision 6b K6b-13, T8b): <see cref="SevenTvApiClient.GetEditorForAsync"/>
/// against a stub server, and the one-minute reuse of <see cref="BotAccountEditorCheck"/>. The measured answer is the one
/// 7TV gave publicly on 2026-11-25 for the bot account's id (T8b-Live): the owner's Twitch connection is filled in the
/// unauthenticated read. Every doubt is Unavailable, never a shorter list (a shorter list would suspend).
/// </summary>
public class SevenTvApiClientEditorForTests
{
    private const string BotId = "01M7XAHYKT63HSZMS71AW0C5TG";
    private const string OwnerId = "01KYJM4NCAXW8FASM2KWG38SBT";
    private const string OwnerTwitchId = "1340084534";

    private const string PlanQuery =
        "query($id: Id!) { users { user(id: $id) { editorFor { userId state addedAt permissions { emoteSet { manage } } user { connections { platform platformId } } } } } }";

    [Fact]
    public async Task TheMeasuredAnswer_IsOneAcceptedEntryWithItsTwitchConnection_SentWithoutAToken_UnderSevenTvBot()
    {
        var handler = new StubHandler(_ => Measured());
        var telemetry = new RecordingRateLimitTelemetry();
        var client = CreateClient(handler, telemetry);

        var result = await client.GetEditorForAsync(BotId);

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        var entry = Assert.Single(result.Entries!);
        Assert.Equal(OwnerId, entry.OwnerSevenTvUserId);
        Assert.Equal(OwnerTwitchId, entry.OwnerTwitchUserId);
        Assert.Equal(SevenTvEditorForEntry.Accepted, entry.State);
        Assert.True(entry.ManagesEmoteSets);
        Assert.Equal(new DateTime(2026, 11, 24, 17, 33, 52, 502, DateTimeKind.Utc), entry.AddedAtUtc);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(PlanQuery, request.Query);
        Assert.Equal(BotId, request.Id);
        Assert.Equal("https://7tv.io/v4/gql", request.Uri);
        Assert.False(request.HadAuthorization);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal((RateLimitCallSources.SevenTvBot, 200), (observation.CallSource, observation.StatusCode));
    }

    [Fact]
    public async Task AnEditorOfNobody_IsAnEmptyOk()
    {
        var result = await CreateClient(new StubHandler(_ => Answer(new JsonArray()))).GetEditorForAsync(BotId);

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        Assert.Empty(result.Entries!);
    }

    // Entries without a Twitch connection cannot be a channel of ours and are left out; the rest stays.
    [Fact]
    public async Task AnOwnerWithoutATwitchConnection_IsLeftOut()
    {
        var result = await CreateClient(new StubHandler(_ => Answer(new JsonArray
        {
            Entry("01A", "ACCEPTED", true, new JsonArray { Connection("YOUTUBE", "yt-1") }),
            Entry("01B", "PENDING", true, new JsonArray()),
            Entry("01C", "REJECTED", false, new JsonArray { Connection("TWITCH", "333") }),
        }))).GetEditorForAsync(BotId);

        var entry = Assert.Single(result.Entries!);
        Assert.Equal(("01C", "333", "REJECTED", false), (entry.OwnerSevenTvUserId, entry.OwnerTwitchUserId, entry.State, entry.ManagesEmoteSets));
    }

    // Unknown states pass through raw; the mapping decides (not_editor).
    [Fact]
    public async Task AnUnknownStateOrMissingPermissions_PassThroughRaw()
    {
        var odd = Entry("01A", "SOMETHING_NEW", true, new JsonArray { Connection("TWITCH", "111") });
        odd["permissions"] = null;
        var result = await CreateClient(new StubHandler(_ => Answer(new JsonArray { odd }))).GetEditorForAsync(BotId);

        var entry = Assert.Single(result.Entries!);
        Assert.Equal(("SOMETHING_NEW", false), (entry.State, entry.ManagesEmoteSets));
        Assert.Equal(BotAccountEditorStatus.NotEditor, BotAccountEditorEntry.Of(entry.State, entry.ManagesEmoteSets, entry.OwnerSevenTvUserId).Status);
    }

    [Theory]
    [InlineData("owner null")]
    [InlineData("owner without id")]
    [InlineData("connections null")]
    [InlineData("entry null")]
    [InlineData("editorFor null")]
    [InlineData("user null")]
    [InlineData("data null")]
    [InlineData("graphql error")]
    public async Task AnAnswerThatCannotBeTrusted_IsUnavailable(string shape)
    {
        JsonObject body = shape switch
        {
            "owner null" => Answer(new JsonArray { Entry(OwnerId, "ACCEPTED", true, null) }),
            "owner without id" => Answer(new JsonArray { Entry(null, "ACCEPTED", true, new JsonArray { Connection("TWITCH", "1") }) }),
            "connections null" => AnswerWithOwner(new JsonObject { ["connections"] = null }),
            "entry null" => Answer(new JsonArray { null }),
            "editorFor null" => new JsonObject { ["data"] = new JsonObject { ["users"] = new JsonObject { ["user"] = new JsonObject { ["editorFor"] = null } } } },
            "user null" => new JsonObject { ["data"] = new JsonObject { ["users"] = new JsonObject { ["user"] = null } } },
            "data null" => new JsonObject { ["data"] = null, ["errors"] = new JsonArray { new JsonObject { ["message"] = "x" } } },
            _ => WithError(Measured(), 404),
        };

        var result = await CreateClient(new StubHandler(_ => body)).GetEditorForAsync(BotId);

        Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Entries);
    }

    // A 429 in either disguise is Unavailable for the read (no pause of the account), counted as 429 under seventv-bot.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A429_IsUnavailable_AndCountsAs429(bool literal)
    {
        var telemetry = new RecordingRateLimitTelemetry();
        var handler = literal
            ? new StubHandler(_ => new JsonObject(), HttpStatusCode.TooManyRequests)
            : new StubHandler(_ => WithError(Measured(), 429));

        var result = await CreateClient(handler, telemetry).GetEditorForAsync(BotId);

        Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status);
        Assert.Equal((RateLimitCallSources.SevenTvBot, 429), (telemetry.Observations[0].CallSource, telemetry.Observations[0].StatusCode));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AnHttpError_IsUnavailable(HttpStatusCode status) =>
        Assert.Equal(
            SevenTvLookupStatus.Unavailable,
            (await CreateClient(new StubHandler(_ => Measured(), status)).GetEditorForAsync(BotId)).Status);

    // An HTML page (a CDN or WAF answer) is no JSON: Unavailable.
    [Fact]
    public async Task APage_IsUnavailable() =>
        Assert.Equal(
            SevenTvLookupStatus.Unavailable,
            (await CreateClient(new RawHandler("<html><body>blocked</body></html>")).GetEditorForAsync(BotId)).Status);

    [Fact]
    public async Task ANetworkFailureOrATimeout_IsUnavailable_TheCallersCancellationIsNot()
    {
        Assert.Equal(
            SevenTvLookupStatus.Unavailable,
            (await CreateClient(new ThrowingHandler(new HttpRequestException("down"))).GetEditorForAsync(BotId)).Status);
        Assert.Equal(
            SevenTvLookupStatus.Unavailable,
            (await CreateClient(new ThrowingHandler(new TaskCanceledException("timeout"))).GetEditorForAsync(BotId)).Status);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateClient(new ThrowingHandler(new TaskCanceledException("stop"))).GetEditorForAsync(BotId, cancelled.Token));
    }

    // ------------------------------------------------------------------------------------ BotAccountEditorCheck

    // One read answers for a minute; a hand PUT (new identity) never gets the read of the old one; a failed read is not
    // reused; a caller that needs a read begun after an instant gets a new one.
    [Fact]
    public async Task TheCheck_ReusesARead_ForAMinute_PerBotIdAndIdentity()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler(_ => Measured());
        var check = new BotAccountEditorCheck(CreateClient(handler), clock);

        var first = await check.ReadAsync(BotId, 4);
        Assert.True(first.IsAvailable);
        Assert.Equal((BotId, 4, clock.GetUtcNow().UtcDateTime), (first.BotSevenTvUserId, first.IdentityVersion, first.StartedAtUtc));
        Assert.Equal(BotAccountEditorStatus.Editor, first.For(OwnerTwitchId)!.Value.Status);
        Assert.True(first.For(OwnerTwitchId)!.Value.Accepted);
        Assert.Equal(OwnerId, first.For(OwnerTwitchId)!.Value.OwnerSevenTvUserId);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Same(first, await check.ReadAsync(BotId, 4));
        Assert.Single(handler.Requests);

        var otherIdentity = await check.ReadAsync(BotId, 5);
        Assert.NotSame(first, otherIdentity);
        Assert.Equal(2, handler.Requests.Count);

        var fresher = await check.ReadAsync(BotId, 4, startedAfterUtc: first.StartedAtUtc);
        Assert.NotSame(first, fresher);
        Assert.Equal(3, handler.Requests.Count);

        clock.Advance(BotAccountEditorCheck.ReadLifetime);
        Assert.NotSame(fresher, await check.ReadAsync(BotId, 4));
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task TheCheck_NeverReusesAFailedRead()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler(_ => WithError(Measured(), 500));
        var check = new BotAccountEditorCheck(CreateClient(handler), clock);

        Assert.False((await check.ReadAsync(BotId, 4)).IsAvailable);
        Assert.Null((await check.ReadAsync(BotId, 4)).For(OwnerTwitchId));
        Assert.Equal(2, handler.Requests.Count);
    }

    private static SevenTvApiClient CreateClient(HttpMessageHandler handler, RecordingRateLimitTelemetry? telemetry = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") };
        return new SevenTvApiClient(
            httpClient,
            telemetry ?? new RecordingRateLimitTelemetry(),
            new RecordingForeignUpstreamRequestBudget(),
            new RecordingSevenTvSearchBudget(),
            new RecordingLogger<SevenTvApiClient>());
    }

    // The answer 7TV gave on 2026-11-25 (T8b-Live), character for character in its data part.
    private static JsonObject Measured() => (JsonObject)JsonNode.Parse(
        """
        {"data":{"users":{"user":{"editorFor":[{"userId":"01KYJM4NCAXW8FASM2KWG38SBT","state":"ACCEPTED","addedAt":"2026-11-24T17:33:52.502+00:00","permissions":{"emoteSet":{"manage":true}},"user":{"connections":[{"platform":"TWITCH","platformId":"1340084534"}]}}]}}},"extensions":{"analyzer":{"complexity":13,"depth":6}}}
        """)!;

    private static JsonObject Answer(JsonArray editorFor) => new()
    {
        ["data"] = new JsonObject { ["users"] = new JsonObject { ["user"] = new JsonObject { ["editorFor"] = editorFor } } },
    };

    private static JsonObject AnswerWithOwner(JsonObject owner)
    {
        var entry = Entry(OwnerId, "ACCEPTED", true, null);
        entry["user"] = owner;
        return Answer(new JsonArray { entry });
    }

    private static JsonObject Entry(string? userId, string state, bool manage, JsonArray? connections) => new()
    {
        ["userId"] = userId,
        ["state"] = state,
        ["addedAt"] = "2026-11-24T17:33:52.502+00:00",
        ["permissions"] = new JsonObject { ["emoteSet"] = new JsonObject { ["manage"] = manage } },
        ["user"] = connections is null ? null : new JsonObject { ["connections"] = connections },
    };

    private static JsonObject Connection(string platform, string id) => new() { ["platform"] = platform, ["platformId"] = id };

    private static JsonObject WithError(JsonObject body, int status)
    {
        body["errors"] = new JsonArray
        {
            new JsonObject { ["message"] = "x", ["extensions"] = new JsonObject { ["status"] = status } },
        };
        return body;
    }

    private sealed record SentRequest(string Uri, string Query, string Id, bool HadAuthorization);

    private sealed class StubHandler(Func<string, JsonObject> answer, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.GetProperty("variables").GetProperty("id").GetString() ?? string.Empty;
            Requests.Add(new SentRequest(
                request.RequestUri!.ToString(),
                doc.RootElement.GetProperty("query").GetString() ?? string.Empty,
                id,
                request.Headers.Authorization is not null));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(answer(id).ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class RawHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") });
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
