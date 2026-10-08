using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
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

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string body)
    {
        using var client = _factory.CreateClient();
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
