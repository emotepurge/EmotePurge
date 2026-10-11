using Xunit;

namespace EmotePurge.Worker.Tests;

public class WorkerStatsTests
{
    private static readonly DateTime Now = new(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Initially_ReportsNothing()
    {
        var stats = new WorkerStats();

        Assert.Equal(0, stats.ConsecutiveFlushFailures);
        Assert.Null(stats.LastFlushSuccessUtc);
        Assert.Null(stats.LastFlushRowCount);
        Assert.Equal(0, stats.TotalChatLines);
    }

    [Fact]
    public void ChatLineCount_IsMonotoneAndReadWithoutResetting()
    {
        var stats = new WorkerStats();

        stats.RecordChatLine();
        stats.RecordChatLine();

        Assert.Equal(2, stats.TotalChatLines);
        Assert.Equal(2, stats.TotalChatLines);
    }

    [Fact]
    public void RecordFlushSuccess_RecordsRowCountAndTimestamp()
    {
        var stats = new WorkerStats();

        stats.RecordFlushSuccess(42, Now);

        Assert.Equal(Now, stats.LastFlushSuccessUtc);
        Assert.Equal(42, stats.LastFlushRowCount);
        Assert.Equal(0, stats.ConsecutiveFlushFailures);
    }

    [Fact]
    public void RecordFlushFailure_IncrementsAndReturnsTheStreak()
    {
        var stats = new WorkerStats();

        Assert.Equal(1, stats.RecordFlushFailure());
        Assert.Equal(2, stats.RecordFlushFailure());
        Assert.Equal(3, stats.RecordFlushFailure());
        Assert.Equal(3, stats.ConsecutiveFlushFailures);
    }

    [Fact]
    public void RecordFlushSuccess_ResetsTheFailureStreak()
    {
        // The streak drives UsageFlushWorker's requeue-vs-drop decision, so a recovered flush must
        // hand the next outage the full five attempts again rather than resuming mid-count.
        var stats = new WorkerStats();
        stats.RecordFlushFailure();
        stats.RecordFlushFailure();

        stats.RecordFlushSuccess(7, Now);

        Assert.Equal(0, stats.ConsecutiveFlushFailures);
        Assert.Equal(1, stats.RecordFlushFailure());
    }

    [Fact]
    public void RecordFlushFailure_LeavesTheLastSuccessIntact()
    {
        // The monitoring page's "last successful flush" must keep pointing at the last success, not
        // blank out the moment the current cycle fails — that timestamp is how long the outage is.
        var stats = new WorkerStats();
        stats.RecordFlushSuccess(5, Now);

        stats.RecordFlushFailure();

        Assert.Equal(Now, stats.LastFlushSuccessUtc);
        Assert.Equal(5, stats.LastFlushRowCount);
    }

    [Fact]
    public void RecordFlushSuccess_OverwritesTheEarlierSuccess()
    {
        var stats = new WorkerStats();
        stats.RecordFlushSuccess(5, Now);

        var later = Now.AddSeconds(30);
        stats.RecordFlushSuccess(0, later);

        Assert.Equal(later, stats.LastFlushSuccessUtc);
        Assert.Equal(0, stats.LastFlushRowCount);
    }

    [Fact]
    public void RecordIndeterminateSharedChatMessage_AccumulatesUntilTaken()
    {
        // The take resets to zero atomically (Interlocked.Exchange) — a second Take right after
        // must see 0, not the same count again.
        var stats = new WorkerStats();

        stats.RecordIndeterminateSharedChatMessage();
        stats.RecordIndeterminateSharedChatMessage();

        Assert.Equal(2, stats.TakeIndeterminateSharedChatMessagesSinceLastFlush());
        Assert.Equal(0, stats.TakeIndeterminateSharedChatMessagesSinceLastFlush());
    }

    [Fact]
    public void RecordSplicedIrcLine_AccumulatesUntilTaken()
    {
        // Same guarantee as the indeterminate counter above: the take resets to zero atomically
        // (Interlocked.Exchange) — a second Take right after must see 0, not the same count again.
        var stats = new WorkerStats();

        stats.RecordSplicedIrcLine();
        stats.RecordSplicedIrcLine();
        stats.RecordSplicedIrcLine();

        Assert.Equal(3, stats.TakeSplicedIrcLinesSinceLastFlush());
        Assert.Equal(0, stats.TakeSplicedIrcLinesSinceLastFlush());
    }

    [Fact]
    public void TwitchRebuildCount_StartsAtZero()
    {
        var stats = new WorkerStats();

        Assert.Equal(0, stats.TwitchRebuildCount);
    }

    [Fact]
    public void TwitchRebuildAttemptFailureCount_StartsAtZero()
    {
        var stats = new WorkerStats();

        Assert.Equal(0, stats.TwitchRebuildAttemptFailureCount);
    }

    [Fact]
    public void RecordTwitchRebuild_IncrementsByOneAndNeverResetsOnRead()
    {
        // Unlike the flush counters above, this is a since-process-start total (Plan #68, Task 2,
        // concept 7.4) — reading it must not zero it the way Take...SinceLastFlush does.
        var stats = new WorkerStats();

        stats.RecordTwitchRebuild();
        Assert.Equal(1, stats.TwitchRebuildCount);
        Assert.Equal(1, stats.TwitchRebuildCount);

        stats.RecordTwitchRebuild();
        Assert.Equal(2, stats.TwitchRebuildCount);
    }

    [Fact]
    public void RecordTwitchRebuildAttemptFailure_IncrementsByOneAndNeverResetsOnRead()
    {
        var stats = new WorkerStats();

        stats.RecordTwitchRebuildAttemptFailure();
        Assert.Equal(1, stats.TwitchRebuildAttemptFailureCount);
        Assert.Equal(1, stats.TwitchRebuildAttemptFailureCount);

        stats.RecordTwitchRebuildAttemptFailure();
        Assert.Equal(2, stats.TwitchRebuildAttemptFailureCount);
    }

    [Fact]
    public void RecordFlushSuccess_LeavesTheTwitchRebuildCountersUntouched()
    {
        var stats = new WorkerStats();
        stats.RecordTwitchRebuild();
        stats.RecordTwitchRebuildAttemptFailure();

        stats.RecordFlushSuccess(5, Now);

        Assert.Equal(1, stats.TwitchRebuildCount);
        Assert.Equal(1, stats.TwitchRebuildAttemptFailureCount);
    }

    [Fact]
    public void RecordFlushFailure_LeavesTheTwitchRebuildCountersUntouched()
    {
        var stats = new WorkerStats();
        stats.RecordTwitchRebuild();
        stats.RecordTwitchRebuildAttemptFailure();

        stats.RecordFlushFailure();

        Assert.Equal(1, stats.TwitchRebuildCount);
        Assert.Equal(1, stats.TwitchRebuildAttemptFailureCount);
    }
}
