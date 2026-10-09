using System.Net;
using System.Text;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Twitch;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Arbitration finding (feat/channel-identity-44): <see cref="TwitchHelixClient.GetUsersAsync"/>
/// only caught <see cref="HttpRequestException"/> and <see cref="TaskCanceledException"/>, so a
/// 200 response with a malformed/truncated body threw <see cref="System.Text.Json.JsonException"/>
/// straight through <c>LookupByLoginAsync</c>/<c>ChannelService.JoinAsync</c> into the global
/// exception handler — a 500 instead of the "Unavailable, carry on as before" contract both callers
/// document. This pins the fix: such a body must come back as <c>null</c>, not throw.
/// </summary>
public class TwitchHelixClientTests
{
    [Fact]
    public async Task GetUsersAsync_WithMalformedJsonBody_ReturnsNull_InsteadOfThrowing()
    {
        var client = CreateClient(HttpStatusCode.OK, "{not valid json", "application/json");

        var result = await client.GetUsersAsync(["12345"], [], "app-token");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetUsersAsync_WithTruncatedJsonBody_ReturnsNull_InsteadOfThrowing()
    {
        // A body cut off mid-stream — e.g. a dropped connection after the status line landed but
        // before the payload finished — is valid-looking prefix, invalid JSON overall.
        var client = CreateClient(HttpStatusCode.OK, "{\"data\":[{\"id\":\"12345\",\"login\":\"foo", "application/json");

        var result = await client.GetUsersAsync(["12345"], [], "app-token");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetUsersAsync_WithWrongContentType_ReturnsNull_InsteadOfThrowing()
    {
        // A 200 answer that isn't actually Helix — e.g. an intermediary's HTML error page delivered
        // with a success status. ReadFromJsonAsync throws NotSupportedException for this, not
        // JsonException, because it never gets far enough to parse.
        var client = CreateClient(HttpStatusCode.OK, "<html><body>Service Unavailable</body></html>", "text/html");

        var result = await client.GetUsersAsync(["12345"], [], "app-token");

        Assert.Null(result);
    }

    // Issue #53: GetUsersAsync got the JsonException/NotSupportedException catch above during
    // #44's arbitration, but its four sibling methods share the exact same ReadFromJsonAsync call
    // and were left with the old HttpRequestException/TaskCanceledException-only catch — a
    // malformed body still threw straight through them. These four pin the fix for each.
    [Fact]
    public async Task GetUserInfoAsync_WithMalformedJsonBody_ReturnsNull_InsteadOfThrowing()
    {
        var client = CreateClient(HttpStatusCode.OK, "{not valid json", "application/json");

        var result = await client.GetUserInfoAsync("user-token");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetModeratedChannelsAsync_WithMalformedJsonBody_ReturnsNull_InsteadOfThrowing()
    {
        var client = CreateClient(HttpStatusCode.OK, "{not valid json", "application/json");

        var result = await client.GetModeratedChannelsAsync("user-token", "12345");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetModeratedChannelsAsync_WithMalformedSecondPage_ReturnsNull_InsteadOfThrowing()
    {
        // The whole call must fail, not just the broken page: a partial list here would be cached
        // and every moderated channel past the cut would silently read as "not moderated".
        var handler = new SequencedStubHandler(
            (HttpStatusCode.OK, "{\"data\":[{\"broadcaster_login\":\"foo\",\"broadcaster_id\":\"1\"}],\"pagination\":{\"cursor\":\"abc\"}}", "application/json"),
            (HttpStatusCode.OK, "{not valid json", "application/json"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.twitch.tv/helix/") };
        var client = new TwitchHelixClient(httpClient, new RecordingLogger<TwitchHelixClient>());

        var result = await client.GetModeratedChannelsAsync("user-token", "12345");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetUserSubscriptionStatusAsync_WithMalformedJsonBody_ReturnsNull_InsteadOfThrowing()
    {
        var client = CreateClient(HttpStatusCode.OK, "{not valid json", "application/json");

        var result = await client.GetUserSubscriptionStatusAsync("user-token", "1", "2");

        Assert.Null(result);
    }

    // #245 (plan P15): the live coverage keeps a locked or excluded channel out by the stream's
    // Helix user id, so the client has to carry user_id through rather than drop it.
    [Fact]
    public async Task GetLiveStreamsByLoginsAsync_ReadsTheUserIdOfEachStream()
    {
        var client = CreateClient(
            HttpStatusCode.OK,
            """{"data":[{"id":"9","user_id":"141981764","user_login":"twitchdev","started_at":"2026-10-08T12:00:00Z"}]}""",
            "application/json");

        var result = await client.GetLiveStreamsByLoginsAsync(["twitchdev"], "app-token");

        var stream = Assert.Single(result!);
        Assert.Equal("twitchdev", stream.UserLogin);
        Assert.Equal("141981764", stream.UserId);
        Assert.Equal(new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), stream.StartedAtUtc);
    }

    [Fact]
    public async Task GetLiveStreamsByLoginsAsync_WithMalformedJsonBody_ReturnsNull_InsteadOfThrowing()
    {
        var client = CreateClient(HttpStatusCode.OK, "{not valid json", "application/json");

        var result = await client.GetLiveStreamsByLoginsAsync(["foo"], "app-token");

        Assert.Null(result);
    }

    private static TwitchHelixClient CreateClient(HttpStatusCode statusCode, string body, string contentType)
    {
        var handler = new StubHandler(statusCode, body, contentType);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.twitch.tv/helix/") };
        return new TwitchHelixClient(httpClient, new RecordingLogger<TwitchHelixClient>());
    }

    private sealed class StubHandler(HttpStatusCode statusCode, string body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            };
            return Task.FromResult(response);
        }
    }

    // Returns one queued response per call, in order, then repeats the last one — used to drive a
    // paginating method through a good first page followed by a broken one.
    private sealed class SequencedStubHandler(params (HttpStatusCode StatusCode, string Body, string ContentType)[] responses) : HttpMessageHandler
    {
        private int _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (statusCode, body, contentType) = responses[Math.Min(_callCount, responses.Length - 1)];
            _callCount++;

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            };
            return Task.FromResult(response);
        }
    }
}
