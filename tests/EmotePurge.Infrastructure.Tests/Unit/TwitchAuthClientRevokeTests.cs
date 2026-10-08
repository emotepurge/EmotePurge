using System.Net;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Twitch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="TwitchAuthClient.RevokeTokenAsync"/> is the best-effort half of self-service account
/// deletion: whatever Twitch (or the network) does, it answers a bool and never throws, because the
/// deletion it follows has already committed.
/// </summary>
public class TwitchAuthClientRevokeTests
{
    [Fact]
    public async Task RevokeTokenAsync_PostsClientIdAndTokenAsAForm_AndReturnsTrueOnSuccess()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var (client, _) = CreateClient(handler);

        var revoked = await client.RevokeTokenAsync("tok/en+1=");

        Assert.True(revoked);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://id.twitch.tv/oauth2/revoke", request.Uri);
        Assert.DoesNotContain("tok", request.Uri);
        Assert.Equal("client_id=test-client&token=tok%2Fen%2B1%3D", request.Body);
    }

    [Fact]
    public async Task RevokeTokenAsync_ReturnsFalse_AndWarnsWithoutTheToken_WhenTwitchRejectsIt()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var (client, logger) = CreateClient(handler);

        var revoked = await client.RevokeTokenAsync("secret-token-value");

        Assert.False(revoked);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain("secret-token-value", entry.Message);
    }

    [Fact]
    public async Task RevokeTokenAsync_ReturnsFalse_InsteadOfThrowing_WhenTheRequestFails()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("network down"));
        var (client, logger) = CreateClient(handler);

        var revoked = await client.RevokeTokenAsync("token");

        Assert.False(revoked);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task RevokeTokenAsync_ReturnsFalse_InsteadOfThrowing_WhenTheClientIdIsNotConfigured()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var (client, _) = CreateClient(handler, clientId: null);

        var revoked = await client.RevokeTokenAsync("token");

        Assert.False(revoked);
        Assert.Empty(handler.Requests);
    }

    private static (TwitchAuthClient Client, RecordingLogger<TwitchAuthClient> Logger) CreateClient(
        RecordingHandler handler, string? clientId = "test-client")
    {
        var settings = new Dictionary<string, string?> { ["Auth:Twitch:ClientId"] = clientId };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var logger = new RecordingLogger<TwitchAuthClient>();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://id.twitch.tv/") };
        return (new TwitchAuthClient(httpClient, configuration, logger), logger);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            return respond(request);
        }
    }
}
