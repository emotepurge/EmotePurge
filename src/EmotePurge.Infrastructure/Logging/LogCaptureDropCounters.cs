namespace EmotePurge.Infrastructure.Logging;

/// <summary>Why an entry was not stored (spec §5.3).</summary>
internal enum LogDropReason
{
    QueueFull,
    Budget,
    CaptureFailed,
    Reentrant,
    Rejected,
    WriteUnconfirmed,
}

/// <summary>Drops counted since the previous cut; becomes one synthetic drop entry.</summary>
internal sealed record LogDropSnapshot(
    long QueueFull,
    long Budget,
    long CaptureFailed,
    long Reentrant,
    long Rejected,
    long WriteUnconfirmed,
    DateTime FirstDropUtc,
    DateTime LastDropUtc)
{
    public long Total => QueueFull + Budget + CaptureFailed + Reentrant + Rejected + WriteUnconfirmed;

    public long Count(LogDropReason reason) => reason switch
    {
        LogDropReason.QueueFull => QueueFull,
        LogDropReason.Budget => Budget,
        LogDropReason.CaptureFailed => CaptureFailed,
        LogDropReason.Reentrant => Reentrant,
        LogDropReason.Rejected => Rejected,
        LogDropReason.WriteUnconfirmed => WriteUnconfirmed,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}

/// <summary>
/// Names of the drop reasons: the property key of the synthetic drop entry (lower case, no separator,
/// e.g. <c>queuefull</c>) and the wire/message form (snake case, e.g. <c>queue_full</c>).
/// </summary>
internal static class LogDropReasonNames
{
    /// <summary>Property key of the drop entry's total; the summary sums it next to the per-reason keys.</summary>
    public const string TotalPropertyName = "dropped";

    /// <summary>All reasons in the order the drop message lists them.</summary>
    public static readonly IReadOnlyList<LogDropReason> All =
    [
        LogDropReason.QueueFull,
        LogDropReason.Budget,
        LogDropReason.CaptureFailed,
        LogDropReason.Reentrant,
        LogDropReason.Rejected,
        LogDropReason.WriteUnconfirmed,
    ];

    public static string PropertyName(LogDropReason reason) => reason switch
    {
        LogDropReason.QueueFull => "queuefull",
        LogDropReason.Budget => "budget",
        LogDropReason.CaptureFailed => "capturefailed",
        LogDropReason.Reentrant => "reentrant",
        LogDropReason.Rejected => "rejected",
        LogDropReason.WriteUnconfirmed => "writeunconfirmed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    public static string WireName(LogDropReason reason) => reason switch
    {
        LogDropReason.QueueFull => "queue_full",
        LogDropReason.Budget => "budget",
        LogDropReason.CaptureFailed => "capture_failed",
        LogDropReason.Reentrant => "reentrant",
        LogDropReason.Rejected => "rejected",
        LogDropReason.WriteUnconfirmed => "write_unconfirmed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}

/// <summary>
/// Process-local drop counters. <see cref="Record"/> runs on the logging hot path and is lock-free: the
/// per-reason counters only ever grow, and <see cref="CutPending"/> (the writer, the only consumer)
/// reports the difference to its previous cut, so every drop lands in exactly one snapshot. The first
/// and last drop time are best effort: a drop racing a cut may lend its time to the neighbouring
/// snapshot, never its count.
/// </summary>
internal sealed class LogCaptureDropCounters
{
    private const int ReasonCount = 6;

    private readonly long[] _recorded = new long[ReasonCount];
    private readonly long[] _cut = new long[ReasonCount];
    private readonly Lock _cutLock = new();

    private long _firstPendingTicks;
    private long _lastPendingTicks;
    private long _previousLastTicks;

    /// <summary>Drops recorded and not yet cut.</summary>
    public long PendingTotal
    {
        get
        {
            long total = 0;
            for (var i = 0; i < ReasonCount; i++)
            {
                // Cut before recorded: a cut only ever stores a recorded value read earlier and both only
                // grow, so no term can go negative under a concurrent cut; the clamp is a belt on top.
                var cut = Interlocked.Read(ref _cut[i]);
                total += Interlocked.Read(ref _recorded[i]) - cut;
            }

            return Math.Max(0, total);
        }
    }

    public void Record(LogDropReason reason, DateTime nowUtc)
    {
        // Times before the count: a cut that sees the count also sees a time (unless an earlier cut took it).
        var ticks = Math.Max(1, nowUtc.Ticks);
        LowerTo(ref _firstPendingTicks, ticks);
        RaiseTo(ref _lastPendingTicks, ticks);
        Interlocked.Increment(ref _recorded[(int)reason]);
    }

    /// <summary>Takes everything recorded since the previous cut, or <c>null</c> if nothing was dropped.</summary>
    public LogDropSnapshot? CutPending()
    {
        lock (_cutLock)
        {
            Span<long> recorded = stackalloc long[ReasonCount];
            Span<long> counts = stackalloc long[ReasonCount];
            long total = 0;
            for (var i = 0; i < ReasonCount; i++)
            {
                recorded[i] = Interlocked.Read(ref _recorded[i]);
                counts[i] = recorded[i] - _cut[i];
                total += counts[i];
            }

            if (total == 0)
            {
                return null;
            }

            for (var i = 0; i < ReasonCount; i++)
            {
                Interlocked.Exchange(ref _cut[i], recorded[i]);
            }

            var first = Interlocked.Exchange(ref _firstPendingTicks, 0);
            var last = Interlocked.Exchange(ref _lastPendingTicks, 0);
            if (first == 0 && last == 0)
            {
                // Only after a race with the previous cut, which took this drop's time.
                first = last = _previousLastTicks;
            }
            else if (first == 0)
            {
                first = last;
            }
            else
            {
                last = Math.Max(last, first);
            }

            _previousLastTicks = last;
            return new LogDropSnapshot(
                counts[(int)LogDropReason.QueueFull],
                counts[(int)LogDropReason.Budget],
                counts[(int)LogDropReason.CaptureFailed],
                counts[(int)LogDropReason.Reentrant],
                counts[(int)LogDropReason.Rejected],
                counts[(int)LogDropReason.WriteUnconfirmed],
                new DateTime(first, DateTimeKind.Utc),
                new DateTime(last, DateTimeKind.Utc));
        }
    }

    private static void LowerTo(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (current == 0 || value < current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    private static void RaiseTo(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
