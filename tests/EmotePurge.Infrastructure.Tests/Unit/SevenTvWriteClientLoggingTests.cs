using System.Net;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Unit.SevenTvWriteClientTestData;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The write client as the worker registers it (<see cref="BotAccountRegistration.AddBotAccountWriter"/>), with every
/// logger at trace level: no line carries the token, the <c>Bearer</c> header, a response body or a 7TV error message
/// (plan #487 2.4, spec AK75 client part, AK125), and the HttpClient factory's own request logging, which writes request
/// headers at trace level, is off for this client. Also pins the registration's base address, time limit and call source.
/// </summary>
public class SevenTvWriteClientLoggingTests
{
    [Fact]
    public async Task EveryOperation_AtTraceLevel_LogsNoTokenNoBodyAndNoMessage()
    {
        var handler = new ScriptedHttpHandler()
            .ThenOk(Fill("""{"data":{"users":{"me":{"id":"@BOT@"}}}}"""))
            .ThenOk(ReadPage($"[{ReadItem(EmoteId, Alias)}]", pageCount: 1))
            .ThenOk(AddAnswer())
            .ThenOk(ErrorsOnly(Error(ConflictMessage, "BAD_REQUEST", 409)))
            .ThenOk(ErrorsOnly(Error(NotEditorMessage, "LACKING_PRIVILEGES", 403)))
            .ThenOk(ErrorsOnly(Error(PendingInvitationMessage, "LACKING_PRIVILEGES", 403)))
            .ThenOk(ErrorsOnly(Error("rate limited", "RATE_LIMIT_EXCEEDED", 429)))
            .ThenOk(Fill("""{"data":{"emoteSets":{"emoteSet":{"removeEmote":{"id":"@SET@"}}}}}"""))
            .ThenOk("""{"data":{"userEditors":{"editor":{"updateState":{"state":"ACCEPTED"}}}}}""")
            .ThenOk("""{"data":{"userEditors":{"editor":{"updateState":{"state":"PENDING"}}}}}""")
            .Then(HttpStatusCode.Unauthorized, UnauthorizedBody)
            .ThenOk("{\"data\":{\"cut off ")
            .ThenThrow(new HttpRequestException($"failure while sending Bearer {Token}"));
        var logs = new CapturingLoggerProvider();
        using var provider = WorkerProvider(handler, logs, new RecordingRateLimitTelemetry());
        var client = provider.GetRequiredService<ISevenTvWriteClient>();

        Assert.Equal(SevenTvWriteOutcome.Ok, (await client.VerifyAsync(Token)).Outcome);
        Assert.Equal(SevenTvWriteOutcome.Ok, (await client.ReadSetEntriesAsync(Token, SetId)).Outcome);
        Assert.Equal(SevenTvWriteOutcome.Ok, (await client.AddEmoteAsync(Token, SetId, EmoteId, Alias)).Outcome);
        Assert.Equal(SevenTvWriteOutcome.Rejected, (await client.AddEmoteAsync(Token, SetId, EmoteId, Alias)).Outcome);
        Assert.Equal(SevenTvWriteOutcome.LackingPrivileges, (await client.AddEmoteAsync(Token, SetId, EmoteId, Alias)).Outcome);
        Assert.Equal(SevenTvWriteOutcome.LackingPrivileges, (await client.AcceptEditorInvitationAsync(Token, BotId, OwnerId)).Outcome);
        Assert.Equal(SevenTvWriteOutcome.RateLimited, (await client.AddEmoteAsync(Token, SetId, EmoteId, Alias)).Outcome);
        Assert.Equal(SevenTvWriteOutcome.Ok, (await client.RemoveEmoteAsync(Token, SetId, EmoteId, Alias)).Outcome);
        Assert.Equal(SevenTvWriteOutcome.Ok, (await client.AcceptEditorInvitationAsync(Token, BotId, OwnerId)).Outcome);
        Assert.Equal(SevenTvWriteOutcome.Unavailable, (await client.AcceptEditorInvitationAsync(Token, BotId, OwnerId)).Outcome);
        Assert.Equal(SevenTvWriteOutcome.Unauthorized, (await client.VerifyAsync(Token)).Outcome);
        Assert.Equal(SevenTvWriteOutcome.Unavailable, (await client.AddEmoteAsync(Token, SetId, EmoteId, Alias)).Outcome);
        Assert.Equal(SevenTvWriteOutcome.Unavailable, (await client.AddEmoteAsync(Token, SetId, EmoteId, Alias)).Outcome);

        Assert.Equal(13, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal($"Bearer {Token}", request.Authorization));
        Assert.NotEmpty(logs.Lines);
        string[] forbidden =
        [
            Token, Token[..20], Token[^20..], "Bearer", "Authorization",
            NotEditorMessage, PendingInvitationMessage, ConflictMessage, "rate limited", "invalid session", "cut off",
        ];
        foreach (var line in logs.Lines)
        {
            foreach (var value in forbidden)
            {
                Assert.DoesNotContain(value, line.AllText, StringComparison.OrdinalIgnoreCase);
            }

            Assert.False(
                line.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal),
                $"HttpClient factory logging is on for the write client: {line.Category}");
        }
    }

    [Fact]
    public async Task Control_AnotherFactoryClient_DoesLogIntoTheHttpClientCategory()
    {
        // Proves the assertion above can fail: the same logging setup does capture the factory's lines for a client that
        // keeps them.
        var logs = new CapturingLoggerProvider();
        using var provider = WorkerProvider(new ScriptedHttpHandler(), logs, new RecordingRateLimitTelemetry(), addControlClient: true);

        await provider.GetRequiredService<IHttpClientFactory>().CreateClient("control").GetAsync("https://example.invalid/");

        Assert.Contains(logs.Lines, line => line.Category.StartsWith("System.Net.Http.HttpClient.control", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Registration_Uses7TvV4_TenSeconds_AndTheBotCallSource()
    {
        var handler = new ScriptedHttpHandler().ThenOk(Fill("""{"data":{"users":{"me":{"id":"@BOT@"}}}}"""));
        var telemetry = new RecordingRateLimitTelemetry();
        using var provider = WorkerProvider(handler, new CapturingLoggerProvider(), telemetry);

        await provider.GetRequiredService<ISevenTvWriteClient>().VerifyAsync(Token);

        Assert.Equal(new Uri("https://7tv.io/v4/gql"), Assert.Single(handler.Requests).RequestUri);
        var factoryClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ISevenTvWriteClient));
        Assert.Equal(TimeSpan.FromSeconds(10), factoryClient.Timeout);
        Assert.Equal(new Uri("https://7tv.io/v4/"), factoryClient.BaseAddress);
        Assert.Null(factoryClient.DefaultRequestHeaders.Authorization);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal(RateLimitProviders.SevenTv, observation.ProviderName);
        Assert.Equal("seventv-bot", observation.CallSource);
        Assert.Equal(RateLimitCallSources.SevenTvBot, observation.CallSource);
    }

    private static ServiceProvider WorkerProvider(
        ScriptedHttpHandler handler,
        CapturingLoggerProvider logs,
        RecordingRateLimitTelemetry telemetry,
        bool addControlClient = false)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BotAccount:Enabled"] = "true",
            [BotAccountTokenKeys.PrivateKeyName] = BotAccountTestKeys.PrivateKey,
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<IRateLimitTelemetry>(telemetry);
        services.AddBotAccountWriter(configuration);

        // Only the network end is swapped; everything else is the worker's own registration.
        services.AddHttpClient<ISevenTvWriteClient, SevenTvWriteClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        if (addControlClient)
        {
            services.AddHttpClient("control").ConfigurePrimaryHttpMessageHandler(() => new ScriptedHttpHandler().ThenOk("{}"));
        }

        return services.BuildServiceProvider();
    }
}
