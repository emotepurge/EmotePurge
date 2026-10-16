using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// Runs instead of the writer when <c>LogCapture:Enabled = false</c> (S9): every 60 s it writes this process's
/// status row with <c>CaptureEnabled = false</c>, so the monitoring row can tell "switched off" from "missing".
/// The coverage marker is the process start (nothing is captured, so nothing can be complete). It never blocks
/// the host start and swallows every database failure; a failure goes to its own category (never captured) at
/// most once per five minutes.
/// </summary>
public sealed class LogCaptureHeartbeat : BackgroundService
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(5);

    private readonly ILogCaptureStore _store;
    private readonly LogCaptureIdentity _identity;
    private readonly TimeProvider _time;
    private readonly ILogger<LogCaptureHeartbeat> _logger;
    private readonly TimeSpan _interval;

    private int _failedBeats;
    private DateTimeOffset? _lastFailureLogUtc;

    internal LogCaptureHeartbeat(
        ILogCaptureStore store,
        LogCaptureIdentity identity,
        TimeProvider time,
        ILogger<LogCaptureHeartbeat> logger,
        TimeSpan? interval = null)
    {
        _store = store;
        _identity = identity;
        _time = time;
        _logger = logger;
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>Heartbeats that failed so far (tests).</summary>
    internal int FailedBeats => Volatile.Read(ref _failedBeats);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var suppression = LogCaptureSuppression.Enter();

        // Never hold up the host start, not even for a connection attempt.
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await BeatAsync(stoppingToken);
                await Task.Delay(_interval, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Regular shutdown.
        }
    }

    private async Task BeatAsync(CancellationToken stoppingToken)
    {
        var now = _time.GetUtcNow();
        try
        {
            await _store.UpsertStatusAsync(
                new LogCaptureStatusWrite(
                    _identity.Source,
                    _identity.InstanceId,
                    _identity.ProcessStartedUtc,
                    now.UtcDateTime,
                    CaptureEnabled: false,
                    _identity.ProcessStartedUtc,
                    CleanShutdownAtUtc: null),
                stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _failedBeats);
            if (_lastFailureLogUtc is null || now - _lastFailureLogUtc >= FailureLogInterval)
            {
                _lastFailureLogUtc = now;
                _logger.LogWarning(
                    "Log capture status heartbeat (capture disabled) failed ({Failure}); the next interval retries.",
                    exception.GetType().Name);
            }
        }
    }
}
