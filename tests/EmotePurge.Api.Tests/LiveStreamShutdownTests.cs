using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EmotePurge.Api.Endpoints;
using EmotePurge.Core.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// An open SSE stream never finishes on its own, so the server's stop would wait for it until the host's
/// shutdown budget ran out, and every hosted service stopping after the server (the log capture writer's
/// drain among them) would get nothing. The stream must end as soon as the host signals
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/>. Each test runs on its own derived host,
/// because stopping the shared one would break the other tests of the fixture.
/// </summary>
public sealed class LiveStreamShutdownTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LiveStreamShutdownTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/channels/live-events")]
    [InlineData("/api/channels/somechannel/live")]
    public async Task ApplicationStopping_EndsAnOpenStream(string path)
    {
        var subscription = new CleanEndingSubscription();
        _factory.LiveEventStream
            .SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<LiveEvent, bool>>(), Arg.Any<CancellationToken>())
            .Returns(LiveEventSubscribeResult.Ok(subscription));

        // A keepalive far beyond the test's patience: the only way the stream can end in time is the stop.
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddSingleton(new LiveStreamKeepaliveOptions { KeepaliveInterval = TimeSpan.FromMinutes(5) })));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthHandler.UserIdHeader, "shutdown-" + path.Length);
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        using (var first = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            Assert.NotNull(await ReadNextDataLineAsync(reader, first.Token));
        }

        factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        using var ended = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.Null(await ReadNextDataLineAsync(reader, ended.Token));
        Assert.True(subscription.Disposed);
    }

    private static async Task<string?> ReadNextDataLineAsync(StreamReader reader, CancellationToken ct)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                return null;
            }

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return line["data: ".Length..];
            }
        }
    }

    /// <summary>Ends its enumeration cleanly on cancellation, as <c>RedisLiveEventStream</c> does.</summary>
    private sealed class CleanEndingSubscription : ILiveEventSubscription
    {
        private readonly Channel<LiveEvent> _channel = Channel.CreateUnbounded<LiveEvent>();

        public bool Disposed { get; private set; }

        public IAsyncEnumerable<LiveEvent> Events => ReadAsync();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<LiveEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            while (true)
            {
                LiveEvent next;
                try
                {
                    next = await _channel.Reader.ReadAsync(ct);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException)
                {
                    yield break;
                }

                yield return next;
            }
        }
    }
}
