using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// A request body Minimal API cannot bind is a client error. The global exception handler used to
/// flatten the <c>BadHttpRequestException</c> it arrives as into a 500 <c>unexpected_error</c>
/// (found in the #245 live run: <c>"type":"Keep"</c> on the vote route, whose enum has no string
/// converter), while genuine server errors must keep answering 500.
/// </summary>
public class UnbindableBodyTests : IClassFixture<ApiFactory>
{
    private const string VotePath = "/api/channels/testchannel/vote-sessions/1/votes";

    private readonly ApiFactory _factory;

    public UnbindableBodyTests(ApiFactory factory)
    {
        _factory = factory;
        factory.VoteEligibility.EvaluateAsync(Arg.Any<TwitchPrincipalInfo>(), "testchannel", 1, Arg.Any<CancellationToken>())
            .Returns(VoteEligibilityResult.Allowed);
    }

    [Theory]
    [InlineData("""{"emoteId":"abc","type":"Keep"}""")]
    [InlineData("""{"emoteId":"abc","type":""")]
    [InlineData("""{"emoteId":"abc","type":99999999999}""")]
    public async Task Vote_Answers400InvalidRequestBody_ForABodyThatCannotBeBound(string body)
    {
        var response = await PostAsync(VotePath, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Contact_Answers400InvalidRequestBody_ForTruncatedJson()
    {
        var response = await PostAsync("/api/contact", """{"name":"Jane","email":""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Contact_StillBindsAndAnswers_ForAWellFormedBody()
    {
        // A filled honeypot is answered 204 before any service runs - proof the body bound and the
        // handler was reached.
        var response = await PostAsync(
            "/api/contact",
            """{"name":"Bot","email":"bot@example.com","message":"A spam message long enough to pass.","turnstileToken":"t","website":"x"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AGenuineServerError_StillAnswers500UnexpectedError_WithoutLeakingTheException()
    {
        _factory.VoteEligibility.EvaluateAsync(Arg.Any<TwitchPrincipalInfo>(), "testchannel", 1, Arg.Any<CancellationToken>())
            .Returns<VoteEligibilityResult>(_ => throw new InvalidOperationException("secret-detail"));

        var response = await PostAsync(VotePath, """{"emoteId":"abc","type":0}""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(ApiErrorCodes.UnexpectedError, JsonDocument.Parse(text).RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("secret-detail", text);
    }

    [Fact]
    public async Task UnbindableBody_LogsNothingAtErrorLevel_ButAGenuineExceptionDoes()
    {
        var log = new ErrorCapture();
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        using var client = factory.CreateClient();

        using var bad = await SendAsync(client, VotePath, """{"emoteId":"abc","type":"Keep"}""");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Empty(log.Errors);

        _factory.VoteEligibility.EvaluateAsync(Arg.Any<TwitchPrincipalInfo>(), "testchannel", 1, Arg.Any<CancellationToken>())
            .Returns<VoteEligibilityResult>(_ => throw new InvalidOperationException("boom"));
        using var crash = await SendAsync(client, VotePath, """{"emoteId":"abc","type":0}""");
        Assert.Equal(HttpStatusCode.InternalServerError, crash.StatusCode);
        Assert.Contains(log.Errors, e => e.Contains("ExceptionHandlerMiddleware"));
    }

    private sealed class ErrorCapture : ILoggerProvider
    {
        private readonly List<string> _errors = [];

        public IReadOnlyList<string> Errors
        {
            get
            {
                lock (_errors)
                {
                    return _errors.ToList();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, this);

        public void Dispose()
        {
        }

        private sealed class Capture(string category, ErrorCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                {
                    lock (owner._errors)
                    {
                        owner._errors.Add(category);
                    }
                }
            }
        }
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string body)
    {
        using var client = _factory.CreateClient();
        return await SendAsync(client, path, body);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        // A fresh id per request: the vote rate limiter partitions by user.
        request.Headers.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString("N"));
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return await client.SendAsync(request);
    }
}
