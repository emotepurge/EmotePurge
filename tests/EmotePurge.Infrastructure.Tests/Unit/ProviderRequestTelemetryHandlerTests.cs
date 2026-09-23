using System.Net;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Telemetry;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The outgoing half of the rate-limit telemetry: one <see cref="DelegatingHandler"/> per typed
/// client, counting what the provider answered. Container-free — an <see cref="HttpMessageHandler"/>
/// stub is the whole external world here.
/// </summary>
/// <remarks>
/// The two failure cases at the bottom are the point of the class as much as the counting is. This
/// handler sits in the request path of every Twitch and 7TV call the app makes; a telemetry sink that
/// throws or faults must not be able to turn a working provider call into a failed one. Counting a
/// rate limit that never happened is a wrong number, causing one is an outage.
/// </remarks>
public class ProviderRequestTelemetryHandlerTests
{
    [Fact]
    public async Task ASuccessfulResponse_IsCountedWithItsProviderAndCallSource()
    {
        var telemetry = new RecordingTelemetry();
        using var client = CreateClient(
            telemetry,
            RateLimitProviders.SevenTv,
            RateLimitCallSources.SevenTvRest,
            new HttpResponseMessage(HttpStatusCode.OK));

        using var response = await client.GetAsync("users/twitch/123");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal(RateLimitProviders.SevenTv, observation.ProviderName);
        Assert.Equal(RateLimitCallSources.SevenTvRest, observation.CallSource);
        Assert.Equal(200, observation.StatusCode);
        Assert.Null(observation.RetryAfterSeconds);
        // 7TV sends no Ratelimit-* headers at all; nothing may be invented for it.
        Assert.Null(observation.RateLimitLimit);
        Assert.Null(observation.RateLimitRemaining);
        Assert.Null(observation.RateLimitReset);
    }

    /// <summary>
    /// A real provider 429 — the thing this whole round was opened to be able to see, and which no
    /// investigation of #33/#35 ever found evidence of. Its <c>Retry-After</c> and Twitch's
    /// <c>Ratelimit-*</c> headers travel along as the sample they are: kept verbatim, never parsed
    /// into a decision.
    /// </summary>
    [Fact]
    public async Task AProvider429_IsCountedWithItsRetryAfterAndHeaderSample()
    {
        var telemetry = new RecordingTelemetry();
        var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        rateLimited.Headers.TryAddWithoutValidation("Retry-After", "12");
        rateLimited.Headers.TryAddWithoutValidation("Ratelimit-Limit", "800");
        rateLimited.Headers.TryAddWithoutValidation("Ratelimit-Remaining", "0");
        rateLimited.Headers.TryAddWithoutValidation("Ratelimit-Reset", "1756500000");

        using var client = CreateClient(
            telemetry,
            RateLimitProviders.Twitch,
            RateLimitCallSources.TwitchHelix,
            rateLimited);

        using var response = await client.GetAsync("moderation/channels");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal(RateLimitProviders.Twitch, observation.ProviderName);
        Assert.Equal(RateLimitCallSources.TwitchHelix, observation.CallSource);
        Assert.Equal(429, observation.StatusCode);
        Assert.Equal(12, observation.RetryAfterSeconds);
        Assert.Equal("800", observation.RateLimitLimit);
        Assert.Equal("0", observation.RateLimitRemaining);
        Assert.Equal("1756500000", observation.RateLimitReset);
    }

    /// <summary>
    /// Twitch sends its budget headers on every Helix answer, not only on a 429 — that sample is what
    /// makes the monitoring page able to say "we were nowhere near the limit" rather than nothing.
    /// </summary>
    [Fact]
    public async Task AHeaderSample_IsAlsoTakenFromASuccessfulResponse()
    {
        var telemetry = new RecordingTelemetry();
        var ok = new HttpResponseMessage(HttpStatusCode.OK);
        ok.Headers.TryAddWithoutValidation("Ratelimit-Limit", "800");
        ok.Headers.TryAddWithoutValidation("Ratelimit-Remaining", "799");

        using var client = CreateClient(telemetry, RateLimitProviders.Twitch, RateLimitCallSources.TwitchAuth, ok);

        using var response = await client.GetAsync("oauth2/validate");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal(200, observation.StatusCode);
        Assert.Equal("800", observation.RateLimitLimit);
        Assert.Equal("799", observation.RateLimitRemaining);
        Assert.Null(observation.RateLimitReset);
    }

    [Fact]
    public async Task AThrowingTelemetrySink_DoesNotBreakTheProviderCall()
    {
        using var client = CreateClient(
            new ThrowingTelemetry(),
            RateLimitProviders.Twitch,
            RateLimitCallSources.TwitchHelix,
            new HttpResponseMessage(HttpStatusCode.OK));

        using var response = await client.GetAsync("users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AFaultingTelemetrySink_DoesNotBreakTheProviderCall()
    {
        using var client = CreateClient(
            new FaultingTelemetry(),
            RateLimitProviders.SevenTv,
            RateLimitCallSources.SevenTvRest,
            new HttpResponseMessage(HttpStatusCode.OK));

        using var response = await client.GetAsync("emote-sets/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A call that never got an answer is not counted: there is no status code to file it under, and
    /// inventing one would put transport failures into the same number as provider rate limits. The
    /// typed clients already log those themselves.
    /// </summary>
    [Fact]
    public async Task ATransportFailure_IsNotCounted_AndStillPropagates()
    {
        var telemetry = new RecordingTelemetry();
        var handler = new ProviderRequestTelemetryHandler(
            RateLimitProviders.Twitch,
            RateLimitCallSources.TwitchHelix,
            telemetry)
        {
            InnerHandler = new ThrowingHandler(),
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.twitch.tv/helix/") };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("users"));

        Assert.Empty(telemetry.Observations);
    }

    private static HttpClient CreateClient(
        IRateLimitTelemetry telemetry,
        string providerName,
        string callSource,
        HttpResponseMessage response)
    {
        var handler = new ProviderRequestTelemetryHandler(providerName, callSource, telemetry)
        {
            InnerHandler = new StubHandler(response),
        };

        return new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") };
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Verbindung fehlgeschlagen.");
    }

    private sealed class RecordingTelemetry : IRateLimitTelemetry
    {
        private readonly List<ProviderResponseObservation> _observations = [];

        public IReadOnlyList<ProviderResponseObservation> Observations => _observations;

        public Task RecordPolicyDecisionAsync(RateLimitPolicyDecision decision, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RecordProviderResponseAsync(ProviderResponseObservation observation, CancellationToken cancellationToken = default)
        {
            _observations.Add(observation);
            return Task.CompletedTask;
        }

        public Task RecordCacheLookupAsync(string cacheName, bool hit, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> ForgetPartitionAsync(string partition, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    /// <summary>Throws before it ever returns a task — the case an <c>await</c> cannot protect against.</summary>
    private sealed class ThrowingTelemetry : IRateLimitTelemetry
    {
        public Task RecordPolicyDecisionAsync(RateLimitPolicyDecision decision, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Telemetrie ist kaputt.");

        public Task RecordProviderResponseAsync(ProviderResponseObservation observation, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Telemetrie ist kaputt.");

        public Task RecordCacheLookupAsync(string cacheName, bool hit, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Telemetrie ist kaputt.");

        public Task<bool> ForgetPartitionAsync(string partition, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Telemetry is broken.");
    }

    /// <summary>Returns a task that faults — the case an unobserved fire-and-forget would leak.</summary>
    private sealed class FaultingTelemetry : IRateLimitTelemetry
    {
        public Task RecordPolicyDecisionAsync(RateLimitPolicyDecision decision, CancellationToken cancellationToken = default)
            => Faulted();

        public Task RecordProviderResponseAsync(ProviderResponseObservation observation, CancellationToken cancellationToken = default)
            => Faulted();

        public Task RecordCacheLookupAsync(string cacheName, bool hit, CancellationToken cancellationToken = default)
            => Faulted();

        public async Task<bool> ForgetPartitionAsync(string partition, CancellationToken cancellationToken = default)
        {
            await Faulted();
            return true;
        }

        private static async Task Faulted()
        {
            await Task.Yield();
            throw new InvalidOperationException("Telemetrie ist kaputt.");
        }
    }
}
