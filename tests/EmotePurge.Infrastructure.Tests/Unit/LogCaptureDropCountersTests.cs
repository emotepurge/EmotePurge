using EmotePurge.Infrastructure.Logging;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class LogCaptureDropCountersTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CutPending_NothingRecorded_ReturnsNull()
    {
        var counters = new LogCaptureDropCounters();

        Assert.Null(counters.CutPending());
        Assert.Equal(0, counters.PendingTotal);
    }

    [Fact]
    public void CutPending_ReturnsEveryReasonWithFirstAndLastDrop_ThenStartsOver()
    {
        var counters = new LogCaptureDropCounters();
        counters.Record(LogDropReason.QueueFull, T0);
        counters.Record(LogDropReason.QueueFull, T0.AddSeconds(1));
        counters.Record(LogDropReason.Budget, T0.AddSeconds(2));
        counters.Record(LogDropReason.CaptureFailed, T0.AddSeconds(3));
        counters.Record(LogDropReason.Reentrant, T0.AddSeconds(4));
        counters.Record(LogDropReason.Rejected, T0.AddSeconds(5));
        counters.Record(LogDropReason.WriteUnconfirmed, T0.AddSeconds(6));

        Assert.Equal(7, counters.PendingTotal);
        var snapshot = counters.CutPending();

        Assert.Equal(new LogDropSnapshot(2, 1, 1, 1, 1, 1, T0, T0.AddSeconds(6)), snapshot);
        Assert.Equal(7, snapshot!.Total);
        Assert.Equal(0, counters.PendingTotal);
        Assert.Null(counters.CutPending());

        counters.Record(LogDropReason.Budget, T0.AddMinutes(1));
        Assert.Equal(new LogDropSnapshot(0, 1, 0, 0, 0, 0, T0.AddMinutes(1), T0.AddMinutes(1)), counters.CutPending());
    }

    [Fact]
    public async Task Concurrent_RecordAndCut_LoseNothing()
    {
        var counters = new LogCaptureDropCounters();
        var cut = 0L;
        using var stop = new CancellationTokenSource();
        var cutter = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                cut += counters.CutPending()?.Total ?? 0;
            }
        });

        Parallel.For(0, 100_000, i => counters.Record((LogDropReason)(i % 6), T0.AddTicks(i)));
        stop.Cancel();
        await cutter;
        cut += counters.CutPending()?.Total ?? 0;

        Assert.Equal(100_000, cut);
    }

    [Fact]
    public void Snapshot_OutOfOrderRecords_SpanEarliestToLatest()
    {
        var counters = new LogCaptureDropCounters();
        counters.Record(LogDropReason.QueueFull, T0.AddSeconds(5));
        counters.Record(LogDropReason.QueueFull, T0);

        var snapshot = counters.CutPending()!;
        Assert.Equal(T0, snapshot.FirstDropUtc);
        Assert.Equal(T0.AddSeconds(5), snapshot.LastDropUtc);
    }

    [Theory]
    [InlineData(nameof(LogDropReason.QueueFull), "queuefull", "queue_full")]
    [InlineData(nameof(LogDropReason.Budget), "budget", "budget")]
    [InlineData(nameof(LogDropReason.CaptureFailed), "capturefailed", "capture_failed")]
    [InlineData(nameof(LogDropReason.Reentrant), "reentrant", "reentrant")]
    [InlineData(nameof(LogDropReason.Rejected), "rejected", "rejected")]
    [InlineData(nameof(LogDropReason.WriteUnconfirmed), "writeunconfirmed", "write_unconfirmed")]
    public void ReasonNames_FollowTheSpec(string reasonName, string property, string wire)
    {
        var reason = Enum.Parse<LogDropReason>(reasonName);
        Assert.Equal(property, LogDropReasonNames.PropertyName(reason));
        Assert.Equal(wire, LogDropReasonNames.WireName(reason));
    }

    [Fact]
    public void Snapshot_CountByReason()
    {
        var snapshot = new LogDropSnapshot(1, 2, 3, 4, 5, 6, T0, T0);

        Assert.Equal(1, snapshot.Count(LogDropReason.QueueFull));
        Assert.Equal(2, snapshot.Count(LogDropReason.Budget));
        Assert.Equal(3, snapshot.Count(LogDropReason.CaptureFailed));
        Assert.Equal(4, snapshot.Count(LogDropReason.Reentrant));
        Assert.Equal(5, snapshot.Count(LogDropReason.Rejected));
        Assert.Equal(6, snapshot.Count(LogDropReason.WriteUnconfirmed));
        Assert.Equal(21, snapshot.Total);
    }

    // --- coverage marker ---

    [Fact]
    public void Coverage_OnlyRises()
    {
        var coverage = new LogCaptureCoverage();
        coverage.Initialize(T0);
        Assert.Equal(T0, coverage.LastIncompleteAtUtc);
        Assert.Equal(DateTimeKind.Utc, coverage.LastIncompleteAtUtc.Kind);

        coverage.MarkIncomplete(T0.AddMinutes(-1));
        Assert.Equal(T0, coverage.LastIncompleteAtUtc);

        coverage.MarkIncomplete(T0.AddMinutes(1));
        Assert.Equal(T0.AddMinutes(1), coverage.LastIncompleteAtUtc);
    }

    [Fact]
    public void Coverage_InitializeDoesNotLowerAMarkSetByAnEarlierDrop()
    {
        var coverage = new LogCaptureCoverage();
        coverage.MarkIncomplete(T0);

        coverage.Initialize(T0.AddHours(-1));

        Assert.Equal(T0, coverage.LastIncompleteAtUtc);
    }

    [Fact]
    public void Coverage_ConcurrentMarks_KeepTheMaximum()
    {
        var coverage = new LogCaptureCoverage();

        Parallel.For(0, 10_000, i => coverage.MarkIncomplete(T0.AddTicks(i)));

        Assert.Equal(T0.AddTicks(9_999), coverage.LastIncompleteAtUtc);
    }
}
