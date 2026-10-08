namespace EmotePurge.Core.Services;

/// <summary>
/// The synchronous, fire-and-forget face of <see cref="IRateLimitTelemetry"/> — how every caller in
/// the product path is meant to report.
/// </summary>
/// <remarks>
/// <para>
/// Awaiting a counter would put a Redis round trip into the request path of the very endpoints this
/// round exists to make cheaper: one on every rate-limited request, one on every provider call, one on
/// every cache lookup. Not awaiting it, on the other hand, is how an unobserved faulted task is
/// created — so the discarding happens here, once and deliberately, rather than as a bare <c>_ =</c>
/// at six call sites where the next reader cannot tell an intention from an oversight.
/// </para>
/// <para>
/// Two failure modes are covered, and they are not the same one. A sink that throws <em>before</em>
/// returning a task cannot be caught by an <c>await</c> that never happens — hence the try/catch. A
/// sink that returns a task which faults later leaks an unobserved exception unless someone looks at
/// it — hence the continuation. The store's own contract promises neither case can occur; this holds
/// for every other implementation too, including a test double and whatever is registered next.
/// </para>
/// <para>
/// Swallowed silently on purpose: the only implementation that can fail meaningfully already logs its
/// own failures with a structured German line, and a logger here would drag a logging package into
/// <c>EmotePurge.Core</c>, which is BCL-only by rule.
/// </para>
/// </remarks>
/// <remarks>
/// Named <c>Report…</c> rather than <c>Record…</c> on purpose: the awaitable twins on the interface are
/// called <c>Record…Async</c>, and a synchronous call that shares the stem makes analyzers (S6966) ask
/// for an <c>await</c> that would defeat the whole point of this class.
/// </remarks>
public static class RateLimitTelemetryExtensions
{
    /// <inheritdoc cref="IRateLimitTelemetry.RecordPolicyDecisionAsync"/>
    public static void ReportPolicyDecision(this IRateLimitTelemetry telemetry, RateLimitPolicyDecision decision)
        => Forget(() => telemetry.RecordPolicyDecisionAsync(decision, CancellationToken.None));

    /// <inheritdoc cref="IRateLimitTelemetry.RecordProviderResponseAsync"/>
    public static void ReportProviderResponse(this IRateLimitTelemetry telemetry, ProviderResponseObservation observation)
        => Forget(() => telemetry.RecordProviderResponseAsync(observation, CancellationToken.None));

    /// <inheritdoc cref="IRateLimitTelemetry.RecordCacheLookupAsync"/>
    public static void ReportCacheLookup(this IRateLimitTelemetry telemetry, string cacheName, bool hit)
        => Forget(() => telemetry.RecordCacheLookupAsync(cacheName, hit, CancellationToken.None));

    /// <summary>
    /// Starts the write and stops caring about it — but observes it, so a fault dies here instead of
    /// on the finalizer thread.
    /// </summary>
    /// <remarks>
    /// The cancellation token is deliberately <see cref="CancellationToken.None"/> everywhere above:
    /// the request's own token is cancelled the moment a client disconnects, and a caller giving up
    /// half way through is exactly the event worth counting.
    /// </remarks>
    private static void Forget(Func<Task> start)
    {
        try
        {
            var write = start();
            if (write.IsCompleted)
            {
                // Reading the property is what marks the exception observed; the task is already done,
                // so there is nothing to wait for.
                _ = write.Exception;
                return;
            }

            write.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch
        {
            // A telemetry sink that throws must never be able to fail the request it is counting.
        }
    }
}
