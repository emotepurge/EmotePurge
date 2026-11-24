using System.Collections.Concurrent;
using System.Net;
using System.Text;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The HTTP half of the bot token's trust boundary (plan #487 T11a, spec AK74/AK75, Sol-P2-9): every answer of the four
/// admin routes (body, status line, every header, problem details included) and every line the Api's loggers write at
/// trace level in every category (formatted text, exception with its inner exceptions and stack, structured values,
/// scopes) is searched for the token, its payload and signature segments, <c>Bearer</c> and the seal. Driven through the
/// paths PUT /token (valid, expired, malformed, broken JSON, wrong field type, empty and null bodies, 9 KB, wrong content
/// type, an exception thrown after the real sealer ran), read, 401 without a session, 403 for a non-admin and DELETE.
/// The service is substituted (container-free suite), so what happens below it (audit, stored row, persisted log capture,
/// the worker) is <c>BotAccountTokenBoundaryTests</c> in Infrastructure.Tests.
/// </summary>
public class BotAccountTokenResponseBoundaryTests : IClassFixture<ApiFactory>
{
    private const string Admin = "900001";
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJleHAiOjQxMDI0NDQ4MDAsInN1YiI6InRva2VuLWJvdW5kYXJ5LXByb2JlIn0.dG9rZW4tYm91bmRhcnktc2lnbmF0dXJl";

    private readonly ApiFactory _factory;

    public BotAccountTokenResponseBoundaryTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.BotAccount.ResetStubs();
        _factory.BotAccount.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new BotAccountStateDto(true, BotAccountStatus.Valid, null, null, "01M7XAHYKT63HSZMS71AW0C5TG", null, null, false, null));
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>())
            .Returns(call => call.Arg<TwitchPrincipalInfo>().TwitchUserId == Admin);
    }

    public static TheoryData<string, string> Bodies => new()
    {
        { "valid", $$"""{"token":"{{Token}}"}""" },
        { "broken json", $$"""{"token":"{{Token}}""" },
        { "unquoted value", $$"""{"token":{{Token}}}""" },
        { "wrong field type", """{"token":12345}""" },
        { "token as object", $$$"""{"token":{"value":"{{{Token}}}"}}""" },
        { "array body", $$"""["{{Token}}"]""" },
        { "null body", "null" },
        { "empty body", string.Empty },
        { "nine kilobytes", $$"""{"token":"{{Token}}.{{new string('A', 9 * 1024)}}"}""" },
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public async Task PutToken_EveryBodyAndEveryOutcome_LeaksNothingIntoTheAnswerOrAnyLog(string name, string body)
    {
        Assert.NotEmpty(name);
        using var logs = new TraceCapture();
        using var factory = Host(logs);
        var outcomes = new[]
        {
            BotAccountTokenSetOutcome.Set, BotAccountTokenSetOutcome.Malformed, BotAccountTokenSetOutcome.Expired,
            BotAccountTokenSetOutcome.Disabled, (BotAccountTokenSetOutcome)99,
        };

        foreach (var outcome in outcomes)
        {
            _factory.BotAccount.SetTokenAsync(Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>()).Returns(outcome);
            var answer = await SendAsync(factory, "PUT", "/api/admin/bot-account/token", body);

            AssertClean(answer, Watch());
        }

        AssertClean(logs, Watch());
    }

    [Theory]
    [InlineData("application/json", "{\"token\":\"@T@\"")]
    [InlineData("text/plain", "@T@")]
    [InlineData("application/x-www-form-urlencoded", "token=@T@")]
    public async Task PutToken_WrongContentTypeOrFraming_LeaksNothing(string contentType, string template)
    {
        using var logs = new TraceCapture();
        using var factory = Host(logs);
        var body = template.Replace("@T@", Token, StringComparison.Ordinal);

        var answer = await SendAsync(factory, "PUT", "/api/admin/bot-account/token", body, contentType: contentType);

        Assert.True((int)answer.StatusCode is >= 400 and < 500, $"Unexpected status {(int)answer.StatusCode}.");
        AssertClean(answer, Watch());
        AssertClean(logs, Watch());
    }

    // An exception behind the sealer, the way a database failure after the seal would surface: the real sealer of the
    // host runs on the token first, then the service throws. The exception middleware answers with the language-neutral
    // code only, and the line it logs names neither token nor seal.
    [Fact]
    public async Task PutToken_AnExceptionBehindTheSeal_AnswersTheGenericCode_AndLogsNoTokenAndNoSeal()
    {
        using var logs = new TraceCapture();
        using var factory = Host(logs);
        var sealer = factory.Services.GetRequiredService<IBotAccountTokenSealer>();
        var seals = new ConcurrentBag<string>();
        _factory.BotAccount.SetTokenAsync(Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns<Task<BotAccountTokenSetOutcome>>(call =>
            {
                seals.Add(sealer.Seal(call.Arg<string?>()!));
                throw new InvalidOperationException("forced failure behind the seal", new TimeoutException("inner failure"));
            });

        var answer = await SendAsync(factory, "PUT", "/api/admin/bot-account/token", $$"""{"token":"{{Token}}"}""");

        Assert.Equal(HttpStatusCode.InternalServerError, answer.StatusCode);
        Assert.Contains("unexpected_error", answer.Body, StringComparison.Ordinal);
        var seal = Assert.Single(seals);
        string[] watch = [.. Watch(), seal, seal[..24], seal[^24..]];
        AssertClean(answer, watch);
        AssertClean(logs, watch);
        Assert.Contains(logs.Lines, line => line.Exception is not null && line.Exception.Contains("forced failure behind the seal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadDeleteAndKillSwitch_LeakNothing_EvenWhenTheServiceThrows()
    {
        using var logs = new TraceCapture();
        using var factory = Host(logs);
        var junk = $$"""{"enabled":true,"token":"{{Token}}"}""";

        AssertClean(await SendAsync(factory, "GET", "/api/admin/bot-account"), Watch());
        AssertClean(await SendAsync(factory, "DELETE", "/api/admin/bot-account/token"), Watch());
        AssertClean(await SendAsync(factory, "PUT", "/api/admin/bot-account/kill-switch", junk), Watch());

        _factory.BotAccount.GetAsync(Arg.Any<CancellationToken>()).Returns<Task<BotAccountStateDto>>(_ => throw new InvalidOperationException("read failed"));
        _factory.BotAccount.DeleteTokenAsync(Arg.Any<AuditActor>(), Arg.Any<CancellationToken>()).Returns<Task<bool>>(_ => throw new InvalidOperationException("delete failed"));
        var failedRead = await SendAsync(factory, "GET", "/api/admin/bot-account");
        var failedDelete = await SendAsync(factory, "DELETE", "/api/admin/bot-account/token");

        Assert.Equal(HttpStatusCode.InternalServerError, failedRead.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, failedDelete.StatusCode);
        AssertClean(failedRead, Watch());
        AssertClean(failedDelete, Watch());
        AssertClean(logs, Watch());
    }

    // The session's absence and a non-admin session answer before any handler: with the token in the body they must not
    // echo it, and the filter's own lines must not name it.
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("100500", HttpStatusCode.Forbidden)]
    public async Task EveryRoute_WithoutASessionOrAdminRight_LeaksNothing(string? userId, HttpStatusCode expected)
    {
        using var logs = new TraceCapture();
        using var factory = Host(logs);
        var body = $$"""{"token":"{{Token}}","enabled":true}""";

        foreach (var (method, path) in new[]
        {
            ("GET", "/api/admin/bot-account"), ("PUT", "/api/admin/bot-account/token"),
            ("DELETE", "/api/admin/bot-account/token"), ("PUT", "/api/admin/bot-account/kill-switch"),
        })
        {
            var answer = await SendAsync(factory, method, path, method == "PUT" ? body : null, userId: userId);

            Assert.Equal(expected, answer.StatusCode);
            AssertClean(answer, Watch());
        }

        AssertClean(logs, Watch());
        Assert.Empty(_factory.BotAccount.ReceivedCalls());
    }

    // The whole token, its payload and signature segments, the header word, and the long padding of the 9 KB case.
    private static string[] Watch()
    {
        var parts = Token.Split('.');
        return ["Bearer", Token, parts[1], parts[2], new string('A', 200)];
    }

    private static void AssertClean(Answer answer, string[] watch)
    {
        foreach (var value in watch)
        {
            Assert.False(
                answer.Everything.Contains(value, StringComparison.OrdinalIgnoreCase),
                $"The answer ({(int)answer.StatusCode}) contains a forbidden value of {value.Length} characters.");
        }
    }

    private static void AssertClean(TraceCapture logs, string[] watch)
    {
        // The paths above all log at trace level in some category; an empty capture would make the search meaningless.
        Assert.NotEmpty(logs.Lines);
        Assert.Contains(logs.Lines, line => line.Level < LogLevel.Information);
        foreach (var line in logs.Lines)
        {
            foreach (var value in watch)
            {
                Assert.False(
                    line.AllText.Contains(value, StringComparison.OrdinalIgnoreCase),
                    $"A {line.Level} line of {line.Category} contains a forbidden value of {value.Length} characters.");
            }
        }
    }

    private WebApplicationFactory<Program> Host(TraceCapture logs) =>
        _factory.WithBotAccountEnabled().WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            // A rule for this provider alone outranks the appsettings category rules, so every category reaches it.
            logging.AddFilter<TraceCapture>(null, LogLevel.Trace);
            logging.AddProvider(logs);
        }));

    private static async Task<Answer> SendAsync(
        WebApplicationFactory<Program> factory, string method, string path, string? body = null, string? userId = Admin, string contentType = "application/json")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (userId is not null)
        {
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            request.Headers.Add(TestAuthHandler.LoginHeader, "someadmin");
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var headers = string.Join('\n', response.Headers.Concat(response.Content.Headers).Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));
        return new Answer(response.StatusCode, text, $"{(int)response.StatusCode} {response.ReasonPhrase}\n{headers}\n{text}");
    }

    private sealed record Answer(HttpStatusCode StatusCode, string Body, string Everything);

    // Everything a console line could show, plus the scopes the framework opens around a request.
    private sealed class TraceCapture : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentQueue<Line> _lines = new();
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public IReadOnlyList<Line> Lines => [.. _lines];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        public sealed record Line(string Category, LogLevel Level, string Message, string? Exception, string Rest)
        {
            public string AllText => string.Join('\n', Category, Message, Exception ?? string.Empty, Rest);
        }

        private sealed class Logger(TraceCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var rest = new StringBuilder();
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    foreach (var pair in values)
                    {
                        rest.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
                    }
                }

                owner._scopes.ForEachScope(
                    (scope, builder) =>
                    {
                        builder.Append(scope).Append('\n');
                        if (scope is IEnumerable<KeyValuePair<string, object?>> scopeValues)
                        {
                            foreach (var pair in scopeValues)
                            {
                                builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
                            }
                        }
                    },
                    rest);
                owner._lines.Enqueue(new Line(category, logLevel, formatter(state, exception), exception?.ToString(), rest.ToString()));
            }
        }
    }
}

internal static class SubstituteReset
{
    // Forget the stubs of an earlier test on the shared substitute: calls and configured results both.
    public static void ResetStubs(this IBotAccountService service)
    {
        service.ClearSubstitute(ClearOptions.All);
    }
}
