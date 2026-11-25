using System.Net;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Telemetry;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Unit.SevenTvWriteClientTestData;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Telemetry of the bot account client (plan #487 T8a, K6b-9, K6c-7, K6d-9, K6e-4): the shared handler is wired in for real
/// and must stay silent, the client reports every HTTP response once under <c>seventv-bot</c>, a rate limit anywhere in the
/// answer counts as 429, and such a rate limit also sets <c>RetryAfter</c> on an <c>Ok</c>.
/// </summary>
public class SevenTvWriteClientTelemetryTests
{
    private const string AddPath = """["emoteSets","emoteSet","addEmote","emotes"]""";

    [Fact]
    public async Task DisguisedRateLimit_WithStatus_IsCountedAs429_AndNotAs200()
    {
        var (client, telemetry) = Create(new ScriptedHttpHandler()
            .ThenOk(ErrorsOnly(Error("slow down", "RATE_LIMIT_EXCEEDED", 429, extra: Hint("x-ratelimit-emote_set_change-reset", "30")))));

        var result = await client.AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.RateLimited, result.Outcome);
        AssertSingle(telemetry, 429);
    }

    [Fact]
    public async Task DisguisedRateLimit_WithCodeOnly_IsCountedAs429()
    {
        var (client, telemetry) = Create(new ScriptedHttpHandler()
            .ThenOk(ErrorsOnly(Error("slow down", "RATE_LIMIT_EXCEEDED", null))));

        var result = await client.VerifyAsync(Token);

        Assert.Equal(SevenTvWriteOutcome.RateLimited, result.Outcome);
        AssertSingle(telemetry, 429);
    }

    [Fact]
    public async Task AddEmote_ProvenMutation_WithNestedRateLimit_IsOkWithRetryAfter_AndCountedAs429()
    {
        var (client, telemetry) = Create(new ScriptedHttpHandler().ThenOk(NestedRateLimit(Error("slow down", null, 429, AddPath, Hint("retry-after", "30")))));

        var result = await client.AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Status.RetryAfter);
        AssertSingle(telemetry, 429);
    }

    [Fact]
    public async Task AddEmote_NestedRateLimit_ByCodeWithoutStatusAndHint_UsesTheDefaultWait()
    {
        var (client, telemetry) = Create(new ScriptedHttpHandler().ThenOk(NestedRateLimit(Error("slow down", "RATE_LIMIT_EXCEEDED", null, AddPath))));

        var result = await client.AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Equal(SevenTvGqlReply.DefaultRetryAfter, result.Status.RetryAfter);
        AssertSingle(telemetry, 429);
    }

    [Fact]
    public async Task AddEmote_NestedRateLimit_WithHintAboveTheCeiling_IsCapped()
    {
        var (client, _) = Create(new ScriptedHttpHandler().ThenOk(NestedRateLimit(Error("slow down", null, 429, AddPath, Hint("retry-after", "999999")))));

        var result = await client.AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Equal(SevenTvGqlReply.MaxRetryAfter, result.Status.RetryAfter);
    }

    [Fact]
    public async Task RemoveAndAccept_WithRateLimitBelowTheMutationField_AreOkWithRetryAfter()
    {
        var (client, telemetry) = Create(new ScriptedHttpHandler()
            .ThenOk(Fill("""{"data":{"emoteSets":{"emoteSet":{"removeEmote":{"id":"@SET@"}}}},"errors":[""" + Error("slow", null, 429, """["emoteSets","emoteSet","removeEmote","emotes"]""") + "]}"))
            .ThenOk("""{"data":{"userEditors":{"editor":{"updateState":{"state":"ACCEPTED"}}}},"errors":[""" + Error("slow", "RATE_LIMIT_EXCEEDED", null, """["userEditors","editor","updateState","x"]""") + "]}"));

        var removed = await client.RemoveEmoteAsync(Token, SetId, EmoteId, Alias);
        var accepted = await client.AcceptEditorInvitationAsync(Token, BotId, OwnerId);

        Assert.Equal(SevenTvWriteOutcome.Ok, removed.Outcome);
        Assert.Equal(SevenTvGqlReply.DefaultRetryAfter, removed.RetryAfter);
        Assert.Equal(SevenTvWriteOutcome.Ok, accepted.Outcome);
        Assert.Equal(SevenTvGqlReply.DefaultRetryAfter, accepted.RetryAfter);
        Assert.Equal([429, 429], telemetry.Observations.Select(o => o.StatusCode));
    }

    [Fact]
    public async Task OkWithoutRateLimit_HasNoRetryAfter_AndIsCountedAs200()
    {
        var (client, telemetry) = Create(new ScriptedHttpHandler().ThenOk(AddAnswer()));

        var result = await client.AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Value);
        Assert.Null(result.Status.RetryAfter);
        AssertSingle(telemetry, 200);
    }

    [Fact]
    public async Task RealHttp429_IsCountedOnce_WithRetryAfter()
    {
        var (client, telemetry) = Create(new ScriptedHttpHandler().Then(HttpStatusCode.TooManyRequests, "{}", r => r.Headers.TryAddWithoutValidation("Retry-After", "12")));

        var result = await client.VerifyAsync(Token);

        Assert.Equal(TimeSpan.FromSeconds(12), result.Status.RetryAfter);
        var observation = AssertSingle(telemetry, 429);
        Assert.Equal(12, observation.RetryAfterSeconds);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, UnauthorizedBody)]
    [InlineData(HttpStatusCode.Forbidden, UnauthorizedBody)]
    [InlineData(HttpStatusCode.Forbidden, "<html>blocked</html>")]
    [InlineData(HttpStatusCode.InternalServerError, "oops")]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>")]
    public async Task OtherHttpStatuses_AreCountedOnce_WithTheirHttpStatus(HttpStatusCode status, string body)
    {
        var (client, telemetry) = Create(new ScriptedHttpHandler().Then(status, body));

        await client.VerifyAsync(Token);

        AssertSingle(telemetry, (int)status);
    }

    [Fact]
    public async Task UnreadableBody_IsCountedOnceWithTheHttpStatus()
    {
        var (client, telemetry) = Create(new ScriptedHttpHandler().ThenOk("{\"data\":{\"cut off "));

        var result = await client.VerifyAsync(Token);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
        AssertSingle(telemetry, 200);
    }

    [Fact]
    public async Task PaginatedRead_ReportsOncePerPage_AndStopsAfterTheDisguisedRateLimit()
    {
        var (client, telemetry) = Create(new ScriptedHttpHandler()
            .ThenOk(ReadPage($"[{ReadItem(EmoteId, Alias)}]", pageCount: 3, totalCount: 3))
            .ThenOk(ErrorsOnly(Error("slow down", null, 429)))
            .ThenOk(ReadPage($"[{ReadItem(EmoteId, Alias)}]", pageCount: 3, totalCount: 3)));

        var result = await client.ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.RateLimited, result.Outcome);
        Assert.Equal([200, 429], telemetry.Observations.Select(o => o.StatusCode));
    }

    [Fact]
    public async Task PaginatedRead_OverThreePages_ReportsThree()
    {
        var item = ReadItem(EmoteId, Alias);
        var (client, telemetry) = Create(new ScriptedHttpHandler()
            .ThenOk(ReadPage($"[{item}]", pageCount: 3, totalCount: 3))
            .ThenOk(ReadPage($"[{item}]", pageCount: 3, totalCount: 3))
            .ThenOk(ReadPage($"[{item}]", pageCount: 3, totalCount: 3)));

        var result = await client.ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Equal([200, 200, 200], telemetry.Observations.Select(o => o.StatusCode));
    }

    [Fact]
    public async Task Timeout_ReportsNothing()
    {
        var (client, telemetry) = Create(new ScriptedHttpHandler().ThenHang(), TimeSpan.FromMilliseconds(100));

        var result = await client.VerifyAsync(Token);

        Assert.Equal(SevenTvWriteOutcome.Timeout, result.Outcome);
        Assert.Empty(telemetry.Observations);
    }

    [Fact]
    public async Task TransportFailure_ReportsNothing()
    {
        var (client, telemetry) = Create(new ScriptedHttpHandler().ThenThrow(new HttpRequestException("down")));

        var result = await client.VerifyAsync(Token);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
        Assert.Empty(telemetry.Observations);
    }

    private static string Hint(string header, string seconds) => $"\"headers\":{{\"{header}\":\"{seconds}\"}}";

    // A proven addEmote (mutation field with the requested set's id) whose only error sits below it, in the `emotes` selection.
    private static string NestedRateLimit(string error) =>
        Fill("""{"data":{"emoteSets":{"emoteSet":{"addEmote":{"id":"@SET@","emotes":null}}}},"errors":[""") + error + "]}";

    // The real shared handler sits in front of the scripted network end, as in the worker: it must stay silent, so every
    // observation here comes from the client and none is counted twice.
    private static (SevenTvWriteClient Client, RecordingRateLimitTelemetry Telemetry) Create(ScriptedHttpHandler network, TimeSpan? timeout = null)
    {
        var telemetry = new RecordingRateLimitTelemetry();
        var handler = new ProviderRequestTelemetryHandler(RateLimitProviders.SevenTv, RateLimitCallSources.SevenTvBot, telemetry)
        {
            InnerHandler = network,
        };
        var client = new SevenTvWriteClient(
            new HttpClient(handler)
            {
                BaseAddress = BotAccountRegistration.WriteClientBaseAddress,
                Timeout = timeout ?? BotAccountRegistration.WriteClientTimeout,
            },
            telemetry,
            new RecordingLogger<SevenTvWriteClient>());
        return (client, telemetry);
    }

    private static ProviderResponseObservation AssertSingle(RecordingRateLimitTelemetry telemetry, int status)
    {
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal(status, observation.StatusCode);
        Assert.Equal(RateLimitProviders.SevenTv, observation.ProviderName);
        Assert.Equal(RateLimitCallSources.SevenTvBot, observation.CallSource);
        return observation;
    }
}
