using EmotePurge.Core.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// Publishes the thin admin hint <see cref="LiveEvents.LogsAppended"/> after the writer committed a batch
/// (spec 5.6). Only a hint: no dirty flag, no trailing announcement, no retry — the log page is correct
/// through its periodic REST reconcile, the hint only makes it faster. At most one publish attempt per
/// <see cref="MinimumInterval"/>; a hint inside that window is dropped. Fire and forget: the writer never
/// waits for Redis, and a failure is swallowed and logged at most once per five minutes to this class's own
/// category, which the capture excludes.
/// <para>
/// Recursion brake (A10): a batch made only of synthetic drop reports or of entries from the delivery path of
/// live events (<c>RedisLiveEventStream</c>, <c>LiveEndpoints</c>) announces nothing, so a delivery that keeps
/// failing with a warning per event cannot drive itself through batch → hint → delivery → warning.
/// </para>
/// The publisher (and the logger) are resolved from the provider on first use, not at construction: the
/// sink is built with the logger factory, and the Redis multiplexer behind the publisher logs itself.
/// </summary>
internal sealed class LogsAppendedNotifier(IServiceProvider services, TimeProvider time)
{
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(5);
    private static readonly string Payload = new LiveEvent(LiveEvents.LogsAppended).Serialize();

    private static readonly HashSet<string> DeliveryCategories = new(StringComparer.Ordinal)
    {
        "EmotePurge.Infrastructure.Redis.RedisLiveEventStream",
        "EmotePurge.Api.Endpoints.LiveEndpoints",
    };

    private long _lastPublishTicks;
    private long _lastFailureLogTicks;

    /// <summary>The most recent fire-and-forget publish; tests await it, production never does.</summary>
    internal Task? LastPublish { get; private set; }

    public void OnBatchCommitted(IReadOnlyList<CapturedLogEntry> batch)
    {
        if (!AnnouncesSomething(batch))
        {
            return;
        }

        var now = time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastPublishTicks);
        if (last != 0 && now - last < MinimumInterval.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastPublishTicks, now, last) != last)
        {
            return;
        }

        // Task.Run: even a publisher that blocks before returning its task cannot hold the writer up.
        LastPublish = Task.Run(PublishAsync);
    }

    private async Task PublishAsync()
    {
        try
        {
            var publisher = services.GetService<IRedisPublisher>();
            if (publisher is not null)
            {
                await publisher.PublishAsync(LiveEvents.Channel, Payload);
            }
        }
        catch (Exception exception)
        {
            LogFailure(exception);
        }
    }

    private void LogFailure(Exception exception)
    {
        try
        {
            var now = time.GetUtcNow().UtcTicks;
            var last = Interlocked.Read(ref _lastFailureLogTicks);
            if ((last != 0 && now - last < FailureLogInterval.Ticks)
                || Interlocked.CompareExchange(ref _lastFailureLogTicks, now, last) != last)
            {
                return;
            }

            services.GetService<ILoggerFactory>()?.CreateLogger<LogsAppendedNotifier>().LogWarning(
                "Publishing the logs.appended hint failed ({Failure}); the log page still reconciles on its own.",
                exception.GetType().Name);
        }
        catch (Exception)
        {
            // A failure while reporting the failure is dropped; the hint is optional.
        }
    }

    private static bool AnnouncesSomething(IReadOnlyList<CapturedLogEntry> batch)
    {
        foreach (var entry in batch)
        {
            if (!entry.IsSynthetic && !DeliveryCategories.Contains(entry.Category))
            {
                return true;
            }
        }

        return false;
    }
}
