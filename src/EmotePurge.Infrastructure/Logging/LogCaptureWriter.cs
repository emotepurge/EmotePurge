using System.Globalization;
using System.Text;
using System.Text.Json;
using EmotePurge.Core.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// Moves captured entries from the <see cref="LogCaptureBuffer"/> into Postgres (spec 5.5). The whole loop runs
/// inside <see cref="LogCaptureSuppression"/>, so nothing logged on its execution context (its own data path,
/// the Redis hint) is captured again.
/// <list type="bullet">
/// <item><b>Batches:</b> up to 500 entries, collected for at most 1 s after the first one, written as one statement.</item>
/// <item><b>Idempotence:</b> a transient failure (anything that is not deterministic, including a lost
/// acknowledgement) is retried once after 2 s; the store skips <c>CaptureId</c>s already stored. A second failure
/// discards the batch and counts every entry as <c>write_unconfirmed</c>.</item>
/// <item><b>Record isolation:</b> a deterministic failure (SQLSTATE class 22 or 54, or a driver encoding/cast error
/// before sending) halves the batch recursively down to single entries, at most 64 statements per original batch;
/// an entry that still fails, and whatever the statement budget leaves, counts as <c>rejected</c>.</item>
/// <item><b>Outage:</b> after an unconfirmed batch or a failed heartbeat the writer stops taking entries and probes
/// the database with heartbeats at 1, 2, 4 … 30 s; the buffer fills and the sink counts <c>queue_full</c>. Once a
/// probe succeeds the drop report is cut and written first, then the buffer.</item>
/// <item><b>Drop report:</b> at most one synthetic entry per 60 s, with its own <c>CaptureId</c> from the moment it is
/// cut; it is held and retried until stored, so its counts are written exactly once.</item>
/// <item><b>Heartbeat and coverage:</b> a status row after every stored batch and at least every 60 s, carrying the
/// in-memory coverage marker. The marker starts at the process start, or at the marker of a clean predecessor
/// (A37); every failed batch, heartbeat or drop report raises it to now.</item>
/// <item><b>Shutdown:</b> <see cref="StopAsync"/> drains the buffer for at most 3 s, on its own budget rather than what is
/// left of the host's, then writes a last heartbeat with
/// <c>CleanShutdownAtUtc</c> only if no Error was lost since the stop began, and names what was not stored once on the
/// console. It returns within the budget even if Postgres hangs.</item>
/// </list>
/// The writer's own trouble goes to its own category (<c>EmotePurge.Infrastructure.Logging.*</c>, never captured),
/// at most one line per five minutes and reason.
/// </summary>
public sealed class LogCaptureWriter : BackgroundService
{
    internal const string DropCategory = "EmotePurge.LogCapture";

    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private static readonly TimeSpan HandoverWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FinalHeartbeatReserve = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StopGrace = TimeSpan.FromMilliseconds(200);

    private readonly LogCaptureBuffer _buffer;
    private readonly LogCaptureDropCounters _drops;
    private readonly LogCaptureCoverage _coverage;
    private readonly LogCaptureIdentity _identity;
    private readonly ILogCaptureStore _store;
    private readonly LogsAppendedNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<LogCaptureWriter> _logger;
    private readonly Timings _timings;

    // Database I/O is cancelled at the drain deadline only, so a batch in flight at the stop still completes.
    private readonly CancellationTokenSource _ioCts = new();
    private readonly CancellationTokenSource _finalCts = new();
    private readonly Lock _consoleLock = new();
    private readonly Dictionary<Trouble, long> _lastConsoleTicks = [];

    private CapturedLogEntry? _heldDropEntry;
    private long _heldDropTotal;
    private DateTime? _lastDropCutUtc;
    private DateTime _lastHeartbeatUtc = DateTime.MinValue;
    private bool _healthy = true;
    private TimeSpan _backoff;
    private bool _bufferCompleted;
    private int _inFlight;
    private long _discardedAtStop;
    private long _markerAtStopTicks;
    private volatile bool _drainFinished;

    internal LogCaptureWriter(
        LogCaptureBuffer buffer,
        LogCaptureDropCounters drops,
        LogCaptureCoverage coverage,
        LogCaptureIdentity identity,
        ILogCaptureStore store,
        LogsAppendedNotifier notifier,
        TimeProvider time,
        ILogger<LogCaptureWriter> logger,
        Timings? timings = null)
    {
        _buffer = buffer;
        _drops = drops;
        _coverage = coverage;
        _identity = identity;
        _store = store;
        _notifier = notifier;
        _time = time;
        _logger = logger;
        _timings = timings ?? Timings.Default;
        _backoff = _timings.InitialBackoff;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Drains for at most <see cref="Timings.DrainBudget"/> and returns by then even if Postgres does not answer:
    /// database I/O is cancelled at the budget minus a reserve for the last heartbeat, and the wait for the loop
    /// is bounded on top, because a cancelled Npgsql command may itself wait for the server.
    /// <para>
    /// The host's token is deliberately not linked in. The host hands one shutdown budget to its hosted services
    /// in turn, and the writer stops last so it can store everyone else's shutdown lines; a slow service ahead of
    /// it would otherwise leave it an already-cancelled token and no drain at all, and no clean handover (A37).
    /// The writer's own bound keeps the stop finite, so a stop may run past the host's budget by at most
    /// <see cref="Timings.DrainBudget"/> plus a 200 ms grace; the container's stop grace period allows for that.
    /// </para>
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _markerAtStopTicks, _coverage.LastIncompleteAtUtc.Ticks);
        var ioBudget = _timings.DrainBudget - FinalHeartbeatReserve;
        _ioCts.CancelAfter(ioBudget > TimeSpan.Zero ? ioBudget : TimeSpan.Zero);
        _finalCts.CancelAfter(_timings.DrainBudget);

        using var bound = new CancellationTokenSource(_timings.DrainBudget + StopGrace);
        await base.StopAsync(bound.Token);
        ReportRemainder();
    }

    public override void Dispose()
    {
        base.Dispose();

        // A loop still stuck in a frozen database after a bounded stop keeps its token sources.
        if (ExecuteTask is null || ExecuteTask.IsCompleted)
        {
            _ioCts.Dispose();
            _finalCts.Dispose();
        }
    }

    /// <summary>The synthetic drop report: a fixed record from the moment it is cut (spec 5.3, L12).</summary>
    internal static CapturedLogEntry BuildDropEntry(LogDropSnapshot snapshot, DateTime nowUtc)
    {
        var breakdown = new StringBuilder();
        foreach (var reason in LogDropReasonNames.All)
        {
            var count = snapshot.Count(reason);
            if (count > 0)
            {
                breakdown.Append(breakdown.Length == 0 ? string.Empty : ", ")
                    .Append(LogDropReasonNames.WireName(reason)).Append('=').Append(count.ToString(CultureInfo.InvariantCulture));
            }
        }

        var message = string.Create(CultureInfo.InvariantCulture, $"{snapshot.Total} log entries were dropped ({breakdown})");

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("dropped", snapshot.Total);
            json.WriteNumber("queuefull", snapshot.QueueFull);
            json.WriteNumber("budget", snapshot.Budget);
            json.WriteNumber("writeunconfirmed", snapshot.WriteUnconfirmed);
            json.WriteNumber("capturefailed", snapshot.CaptureFailed);
            json.WriteNumber("reentrant", snapshot.Reentrant);
            json.WriteNumber("rejected", snapshot.Rejected);
            json.WriteString("firstdroputc", snapshot.FirstDropUtc.ToString(UtcFormat, CultureInfo.InvariantCulture));
            json.WriteString("lastdroputc", snapshot.LastDropUtc.ToString(UtcFormat, CultureInfo.InvariantCulture));
            json.WriteEndObject();
        }

        return new CapturedLogEntry(
            Guid.CreateVersion7(new DateTimeOffset(nowUtc, TimeSpan.Zero)),
            nowUtc,
            LogEntryLevel.Warning,
            DropCategory,
            EventId: null,
            EventName: null,
            message,
            ExceptionType: null,
            Exception: null,
            Encoding.UTF8.GetString(stream.ToArray()),
            Truncated: false,
            IsSynthetic: true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var suppression = LogCaptureSuppression.Enter();

        // Never hold up the host start: everything below runs after StartAsync has returned.
        await Task.Yield();
        var io = _ioCts.Token;
        var final = _finalCts.Token;
        try
        {
            await SeedCoverageAsync(io);
            await TryHeartbeatAsync(cleanShutdownAtUtc: null, io);
            await RunAsync(stoppingToken, io);
        }
        catch (Exception exception)
        {
            Report(Trouble.Unexpected, exception);
        }

        await DrainAsync(io, final);
    }

    /// <summary>
    /// Seeds the coverage marker before the first heartbeat (spec 11, A37): the process start, unless the newest
    /// other instance of this source captured, shut down cleanly and did so at most 5 min before this process
    /// started — then its marker carries over. The marker only ever rises, so an Error dropped before the writer
    /// started keeps its mark.
    /// </summary>
    private async Task SeedCoverageAsync(CancellationToken io)
    {
        var seed = _identity.ProcessStartedUtc;
        try
        {
            var previous = await _store.ReadLatestOtherInstanceAsync(_identity.Source, _identity.InstanceId, io);
            if (previous is { CaptureEnabled: true, CleanShutdownAtUtc: { } cleanShutdown }
                && cleanShutdown >= _identity.ProcessStartedUtc - HandoverWindow)
            {
                seed = previous.LastIncompleteAtUtc;
            }
        }
        catch (Exception exception)
        {
            Report(Trouble.Unreachable, exception);
        }

        _coverage.Initialize(seed);
    }

    private async Task RunAsync(CancellationToken stoppingToken, CancellationToken io)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_healthy)
                {
                    await Task.Delay(_backoff, _time, stoppingToken);
                    _backoff = _backoff * 2 < _timings.MaxBackoff ? _backoff * 2 : _timings.MaxBackoff;
                    if (!await TryHeartbeatAsync(cleanShutdownAtUtc: null, io))
                    {
                        continue;
                    }
                }

                // After a recovery the drop report goes first, then the buffer.
                await TryReportDropsAsync(io);
                if (!_healthy)
                {
                    continue;
                }

                var batch = await CollectAsync(stoppingToken);
                if (batch.Count > 0)
                {
                    if (await WriteBatchAsync(batch, io))
                    {
                        await TryHeartbeatAsync(cleanShutdownAtUtc: null, io);
                    }
                }
                else if (Now - _lastHeartbeatUtc >= _timings.HeartbeatInterval)
                {
                    await TryHeartbeatAsync(cleanShutdownAtUtc: null, io);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Never let a defect stop the host (BackgroundService would); back off and go on.
                Report(Trouble.Unexpected, exception);
                _healthy = false;
            }
        }
    }

    private async Task<List<CapturedLogEntry>> CollectAsync(CancellationToken stoppingToken)
    {
        var batch = new List<CapturedLogEntry>();
        TakeAvailable(batch);
        if (batch.Count == 0)
        {
            if (_bufferCompleted)
            {
                // The sink is closed: nothing will arrive, only heartbeats remain.
                await DelayQuietlyAsync(_timings.BatchWindow, stoppingToken);
                return batch;
            }

            if (!await WaitForDataAsync(_timings.BatchWindow, stoppingToken))
            {
                return batch;
            }

            TakeAvailable(batch);
        }

        var windowEnd = Now + _timings.BatchWindow;
        while (batch.Count < _timings.MaxBatchSize)
        {
            var remaining = windowEnd - Now;
            if (remaining <= TimeSpan.Zero || !await WaitForDataAsync(remaining, stoppingToken))
            {
                break;
            }

            TakeAvailable(batch);
        }

        return batch;
    }

    private void TakeAvailable(List<CapturedLogEntry> batch)
    {
        while (batch.Count < _timings.MaxBatchSize && _buffer.Reader.TryRead(out var entry))
        {
            batch.Add(entry);
        }
    }

    /// <summary>Waits for an entry; <c>false</c> on timeout, stop or a completed buffer, never throws.</summary>
    private async Task<bool> WaitForDataAsync(TimeSpan timeout, CancellationToken stoppingToken)
    {
        if (timeout <= TimeSpan.Zero || stoppingToken.IsCancellationRequested)
        {
            return false;
        }

        using var timeoutCts = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);
        try
        {
            if (await _buffer.Reader.WaitToReadAsync(linked.Token))
            {
                return true;
            }

            _bufferCompleted = true;
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task DelayQuietlyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _time, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stop or deadline: the caller decides what comes next.
        }
    }

    /// <summary>
    /// Writes one batch with retry and isolation; returns <c>false</c> if part of it stayed unconfirmed (the
    /// database is then considered unreachable).
    /// </summary>
    private async Task<bool> WriteBatchAsync(List<CapturedLogEntry> batch, CancellationToken io)
    {
        Volatile.Write(ref _inFlight, batch.Count);
        var stored = new List<CapturedLogEntry>(batch.Count);
        var rejected = new List<CapturedLogEntry>();
        var unconfirmed = new List<CapturedLogEntry>();
        Exception? lastFailure = null;
        var statements = 0;
        var parts = new Stack<(int Start, int Count)>();
        parts.Push((0, batch.Count));

        while (parts.TryPop(out var part))
        {
            var slice = batch.GetRange(part.Start, part.Count);
            if (statements >= _timings.MaxStatementsPerBatch)
            {
                rejected.AddRange(slice);
                continue;
            }

            statements++;
            var (result, failure) = await TryInsertAsync(slice, io);
            if (result == WriteResult.Transient && statements < _timings.MaxStatementsPerBatch)
            {
                // One idempotent repeat: a commit whose acknowledgement was lost writes nothing twice.
                await DelayQuietlyAsync(_timings.RetryDelay, io);
                statements++;
                (result, failure) = await TryInsertAsync(slice, io);
            }

            switch (result)
            {
                case WriteResult.Stored:
                    stored.AddRange(slice);
                    break;
                case WriteResult.Deterministic when slice.Count == 1:
                    lastFailure = failure;
                    rejected.Add(slice[0]);
                    break;
                case WriteResult.Deterministic:
                    lastFailure = failure;
                    var half = part.Count / 2;
                    parts.Push((part.Start + half, part.Count - half));
                    parts.Push((part.Start, half));
                    break;
                default:
                    lastFailure = failure;
                    unconfirmed.AddRange(slice);
                    while (parts.TryPop(out var rest))
                    {
                        unconfirmed.AddRange(batch.GetRange(rest.Start, rest.Count));
                    }

                    break;
            }
        }

        var now = Now;
        foreach (var entry in rejected)
        {
            _drops.Record(LogDropReason.Rejected, now);
            if (entry.Level >= LogEntryLevel.Error)
            {
                _coverage.MarkIncomplete(now);
            }
        }

        foreach (var _ in unconfirmed)
        {
            _drops.Record(LogDropReason.WriteUnconfirmed, now);
        }

        // Counted as drops first, then no longer in flight: a stop report sees every entry in one of the two.
        Volatile.Write(ref _inFlight, 0);

        if (rejected.Count > 0)
        {
            Report(Trouble.Rejected, lastFailure, rejected.Count);
        }

        if (unconfirmed.Count > 0)
        {
            _coverage.MarkIncomplete(now);
            _healthy = false;
            Report(Trouble.Unconfirmed, lastFailure, unconfirmed.Count);
        }

        if (stored.Count > 0)
        {
            _notifier.OnBatchCommitted(stored);
        }

        return unconfirmed.Count == 0;
    }

    private async Task<(WriteResult Result, Exception? Failure)> TryInsertAsync(IReadOnlyList<CapturedLogEntry> entries, CancellationToken io)
    {
        try
        {
            await _store.InsertAsync(entries, io);
            return (WriteResult.Stored, null);
        }
        catch (Exception exception) when (IsDeterministic(exception))
        {
            return (WriteResult.Deterministic, exception);
        }
        catch (Exception exception)
        {
            return (WriteResult.Transient, exception);
        }
    }

    /// <summary>
    /// Writes the held drop report, or cuts a new one when drops are pending and the last cut is at least
    /// <see cref="Timings.DropReportInterval"/> old. A transient failure keeps it held (same <c>CaptureId</c>,
    /// same counts) and marks the database unreachable.
    /// </summary>
    private async Task TryReportDropsAsync(CancellationToken io)
    {
        if (_heldDropEntry is null)
        {
            if (_drops.PendingTotal == 0)
            {
                return;
            }

            var now = Now;
            if (_lastDropCutUtc is { } lastCut && now - lastCut < _timings.DropReportInterval)
            {
                return;
            }

            var snapshot = _drops.CutPending();
            if (snapshot is null)
            {
                return;
            }

            _heldDropEntry = BuildDropEntry(snapshot, now);
            _heldDropTotal = snapshot.Total;
            _lastDropCutUtc = now;
        }

        var (result, failure) = await TryInsertAsync([_heldDropEntry], io);
        switch (result)
        {
            case WriteResult.Stored:
                _heldDropEntry = null;
                _heldDropTotal = 0;
                break;
            case WriteResult.Deterministic:
                Report(Trouble.DropReportRejected, failure, _heldDropTotal);
                _heldDropEntry = null;
                _heldDropTotal = 0;
                break;
            default:
                _coverage.MarkIncomplete(Now);
                _healthy = false;
                Report(Trouble.Unreachable, failure);
                break;
        }
    }

    private async Task<bool> TryHeartbeatAsync(DateTime? cleanShutdownAtUtc, CancellationToken ct)
    {
        var now = Now;
        _lastHeartbeatUtc = now;
        var status = new LogCaptureStatusWrite(
            _identity.Source,
            _identity.InstanceId,
            _identity.ProcessStartedUtc,
            now,
            CaptureEnabled: true,
            _coverage.LastIncompleteAtUtc,
            cleanShutdownAtUtc);
        try
        {
            await _store.UpsertStatusAsync(status, ct);
            if (!_healthy)
            {
                _healthy = true;
                _backoff = _timings.InitialBackoff;
            }

            return true;
        }
        catch (Exception exception)
        {
            _coverage.MarkIncomplete(Now);
            _healthy = false;
            Report(Trouble.Unreachable, exception);
            return false;
        }
    }

    /// <summary>
    /// The stop: drop report (if due), then the buffer, until it is empty or the I/O deadline passes; whatever
    /// is left is discarded and counted. The last heartbeat says "clean" only if no Error was lost since the
    /// stop began (the marker did not move and no Error was left behind).
    /// </summary>
    private async Task DrainAsync(CancellationToken io, CancellationToken final)
    {
        try
        {
            await TryReportDropsAsync(io);
            while (!io.IsCancellationRequested)
            {
                var batch = new List<CapturedLogEntry>();
                TakeAvailable(batch);
                if (batch.Count == 0 || !await WriteBatchAsync(batch, io))
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            Report(Trouble.Unexpected, exception);
        }

        var (discarded, discardedError) = DiscardRemainder();
        var now = Now;
        if (discardedError)
        {
            _coverage.MarkIncomplete(now);
        }

        Volatile.Write(ref _discardedAtStop, discarded);
        var lostError = discardedError || _coverage.LastIncompleteAtUtc.Ticks > Volatile.Read(ref _markerAtStopTicks);
        await TryHeartbeatAsync(lostError ? null : now, final);
        _drainFinished = true;
    }

    private (long Count, bool HasError) DiscardRemainder()
    {
        // Bounded by what is queued now: a process that keeps logging cannot keep this loop going.
        var queued = _buffer.Reader.Count;
        long count = 0;
        var hasError = false;
        while (count < queued && _buffer.Reader.TryRead(out var entry))
        {
            count++;
            hasError |= entry.Level >= LogEntryLevel.Error;
        }

        return (count, hasError);
    }

    /// <summary>Names, once and on the console only, what this process could not store (spec 5.5, A8).</summary>
    private void ReportRemainder()
    {
        var unwritten = _drainFinished
            ? Volatile.Read(ref _discardedAtStop)
            : _buffer.Reader.Count + Volatile.Read(ref _inFlight);
        var dropped = _drops.PendingTotal + (_heldDropEntry is null ? 0 : _heldDropTotal);
        if (unwritten + dropped == 0)
        {
            return;
        }

        _logger.LogWarning(
            "Log capture stopped; {Remaining} log entries were not stored ({Unwritten} unwritten at the stop, {Dropped} dropped and not reported). The console still has the original lines.",
            unwritten + dropped,
            unwritten,
            dropped);
    }

    private void Report(Trouble trouble, Exception? failure, long count = 0)
    {
        var now = _time.GetUtcNow().UtcTicks;
        lock (_consoleLock)
        {
            if (_lastConsoleTicks.TryGetValue(trouble, out var last) && now - last < _timings.ConsoleInterval.Ticks)
            {
                return;
            }

            _lastConsoleTicks[trouble] = now;
        }

        var reason = Describe(failure);
        switch (trouble)
        {
            case Trouble.Unreachable:
                _logger.LogWarning("Log capture cannot reach Postgres ({Failure}); entries stay queued and the writer retries with backoff.", reason);
                break;
            case Trouble.Unconfirmed:
                _logger.LogWarning("Log capture could not confirm a batch of {Count} entries after one retry ({Failure}); they count as write_unconfirmed.", count, reason);
                break;
            case Trouble.Rejected:
                _logger.LogWarning("Postgres rejected {Count} log entries ({Failure}); they count as rejected, the rest of their batch was stored.", count, reason);
                break;
            case Trouble.DropReportRejected:
                _logger.LogWarning("Postgres rejected the drop report for {Count} dropped log entries ({Failure}); the report is discarded.", count, reason);
                break;
            default:
                _logger.LogWarning("Log capture writer hit an unexpected failure ({Failure}); it backs off and goes on.", reason);
                break;
        }
    }

    private static bool IsDeterministic(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.SqlState.StartsWith("22", StringComparison.Ordinal)
                    || postgres.SqlState.StartsWith("54", StringComparison.Ordinal);
            }

            // Raised by the driver while encoding a value, before anything is sent (EncoderFallbackException
            // is an ArgumentException): the same batch would fail the same way again.
            if (current is ArgumentException or InvalidCastException or FormatException)
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(Exception? failure) => failure switch
    {
        null => "unknown",
        PostgresException postgres => $"{nameof(PostgresException)} {postgres.SqlState}",
        _ => failure.GetType().Name,
    };

    private enum Trouble
    {
        Unreachable,
        Unconfirmed,
        Rejected,
        DropReportRejected,
        Unexpected,
    }

    private enum WriteResult
    {
        Stored,
        Deterministic,
        Transient,
    }

    /// <summary>The writer's pinned timings (spec 5.5); tests shorten them, production uses <see cref="Default"/>.</summary>
    internal sealed record Timings
    {
        public static readonly Timings Default = new();

        public int MaxBatchSize { get; init; } = 500;

        public TimeSpan BatchWindow { get; init; } = TimeSpan.FromSeconds(1);

        public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

        public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

        public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

        public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(60);

        public TimeSpan DropReportInterval { get; init; } = TimeSpan.FromSeconds(60);

        public TimeSpan DrainBudget { get; init; } = TimeSpan.FromSeconds(3);

        public TimeSpan ConsoleInterval { get; init; } = TimeSpan.FromMinutes(5);

        public int MaxStatementsPerBatch { get; init; } = 64;
    }
}
