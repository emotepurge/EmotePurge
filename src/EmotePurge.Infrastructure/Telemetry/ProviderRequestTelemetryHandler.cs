using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Telemetry;

/// <summary>
/// Carries the one <see cref="HttpRequestOptions"/> key this handler checks before recording
/// anything (spec 2026-09-09, section 6 telemetry vertrag). Set on a request to silence the shared
/// handler for it — the request's own code reports itself instead, under its own call source, with
/// whatever it learns from the parsed response body that the handler, seeing only the raw HTTP
/// status, cannot: 7TV disguises an overload as HTTP 200 with <c>extensions.status: 429</c>, which
/// this handler would otherwise file as a plain success.
/// </summary>
public static class ProviderTelemetrySuppression
{
    public static readonly HttpRequestOptionsKey<bool> OptionsKey = new("EmotePurge.SuppressProviderTelemetry");
}

/// <summary>
/// Counts what Twitch and 7TV actually answered, at the outgoing boundary — one instance per typed
/// client, each carrying its own call source.
/// </summary>
/// <remarks>
/// <para>
/// Registered per client rather than derived from the request URI: "which of our clients made this
/// call" is the question an operator has, and a host name cannot answer it — a Helix pagination and a
/// token refresh both go to Twitch and behave nothing alike.
/// </para>
/// <para>
/// This observes only. There is no reservation, no pre-emptive rejection and no backoff here: the
/// spec's step 4 builds a picture of provider cost, not a coordinator. Twitch's <c>Ratelimit-*</c>
/// headers travel along verbatim as a sample of what was last seen, explicitly not as a budget
/// anything is allowed to act on — with more than one process calling the same provider they are not
/// shared state, and parsing them into a decision here would invent one.
/// </para>
/// <para>
/// A call that got no answer at all (timeout, connection failure) is deliberately not counted: there
/// is no status code to file it under, and folding it into any existing bucket would mix transport
/// failures into the one number that means "the provider rate-limited us". The typed clients already
/// log those themselves.
/// </para>
/// </remarks>
public sealed class ProviderRequestTelemetryHandler(
    string providerName,
    string callSource,
    IRateLimitTelemetry telemetry) : DelegatingHandler
{
    private const string LimitHeader = "Ratelimit-Limit";

    private const string RemainingHeader = "Ratelimit-Remaining";

    private const string ResetHeader = "Ratelimit-Reset";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (request.Options.TryGetValue(ProviderTelemetrySuppression.OptionsKey, out var suppressed) && suppressed)
        {
            return response;
        }

        // Fire-and-forget by contract (RateLimitTelemetryExtensions): a counter must not add a Redis
        // round trip to every provider call, and must never be able to fail one.
        telemetry.ReportProviderResponse(new ProviderResponseObservation(
            providerName,
            callSource,
            (int)response.StatusCode,
            ReadRetryAfterSeconds(response),
            ReadHeader(response, LimitHeader),
            ReadHeader(response, RemainingHeader),
            ReadHeader(response, ResetHeader)));

        return response;
    }

    /// <summary>
    /// The provider's own <c>Retry-After</c>, in seconds. Both forms are accepted — a delta and an
    /// HTTP date — because the header allows both and which one arrives is the provider's choice.
    /// Internal rather than private: <c>SevenTvApiClient</c>'s own telemetry reporting for the
    /// suppressed foreign-channel-import requests (this class's doc comment) reads the exact same
    /// header the exact same way, and a second copy of this parsing would be the kind of drift this
    /// project's decision log already has an entry about.
    /// </summary>
    internal static int? ReadRetryAfterSeconds(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds));
        }

        if (retryAfter.Date is { } date)
        {
            // Never below one second: a date already in the past means "now", and a reported zero
            // reads on the monitoring page as "no wait" rather than as the rate limit it was.
            return Math.Max(1, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
        }

        return null;
    }

    /// <summary>
    /// One header, kept as the string it arrived as. Absent for 7TV, which sends none of them — and
    /// absent is the honest answer there, not a zero.
    /// </summary>
    internal static string? ReadHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()
            : null;
}
