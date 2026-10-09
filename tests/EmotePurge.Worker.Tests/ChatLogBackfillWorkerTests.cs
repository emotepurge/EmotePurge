using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Worker.ChatLogBackfill;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The backfill loop against an in-memory service and a scripted archive (#350, spec 4.2–4.7). Every
// wait runs on a FakeTimeProvider, so "no request before X" is a statement about the worker's own
// clock, not about how fast this machine is. What the database decides (pause length, threshold,
// claim checks, block plausibility) is the service's contract and pinned in the Infrastructure tests;
// here the fake follows that contract and the tests show the worker acts on what it gets back.
public class ChatLogBackfillWorkerTests
{
    private static readonly DateOnly WindowFrom = new(2026, 9, 1);

    [Fact]
    public async Task Disabled_LogsOnce_AndTouchesNothing()
    {
        await using var rig = new BackfillRig(o => o.Enabled = false);
        rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));

        await rig.StartAsync();
        await rig.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(rig.Store.Instances);
        Assert.Empty(rig.Archive.Requests);
        var entry = Assert.Single(rig.Logger.Entries);
        Assert.Equal((LogLevel.Information, "Chat-log backfill disabled."), entry);
    }

    [Fact]
    public async Task ARun_IsReadBlockByBlock_CountedAgainstItsSnapshot_AndCommitted()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(8));
        rig.Archive.Handler = async (request, onMessage, _) =>
        {
            if (request.FromUtc.Day != 1)
            {
                return FakeArchive.Status(ChatLogDayStatus.NoLogDay, 404);
            }

            var at = request.FromUtc.AddHours(12);
            await onMessage(Message(at, "u1", "Kappa PogU"));
            await onMessage(Message(at, BackfillRig.ExcludedChatter, "Kappa"));
            await onMessage(Message(at.AddDays(1), "u2", "Kappa"));
            return FakeArchive.Complete(bytes: 4096, messages: 3);
        };

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);

        // Two requests, exactly the planned ranges, for the run's Twitch id, spaced by RequestDelaySeconds.
        Assert.Collection(
            rig.Archive.Requests,
            r =>
            {
                Assert.Equal("tw-zokka", r.TwitchChannelId);
                Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), r.FromUtc);
                Assert.Equal(new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc), r.ToUtcExclusive);
                Assert.Equal(256L * 1024 * 1024, r.MaxBytes);
            },
            r =>
            {
                Assert.Equal(new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc), r.FromUtc);
                Assert.Equal(new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc), r.ToUtcExclusive);
            });
        Assert.True(rig.Archive.Requests[1].AtUtc - rig.Archive.Requests[0].AtUtc >= TimeSpan.FromSeconds(rig.Options.RequestDelaySeconds));

        // The excluded chatter counts nothing; the client's MessageCount is booked as is (D47); a 404
        // block is committed empty.
        Assert.Collection(
            run.Commits,
            c =>
            {
                Assert.Equal(
                    [
                        new ChatLogBackfillAggregate("e1", new DateOnly(2026, 9, 1), 1, 0, 0),
                        new ChatLogBackfillAggregate("e1", new DateOnly(2026, 9, 2), 1, 0, 0),
                        new ChatLogBackfillAggregate("e2", new DateOnly(2026, 9, 1), 1, 0, 0)
                    ],
                    c.Rows);
                Assert.Equal(4096, c.Bytes);
                Assert.Equal(3, c.Messages);
            },
            c =>
            {
                Assert.Empty(c.Rows);
                Assert.Equal(0, c.Messages);
            });

        // Claim, two commits: progress three times, usage.flushed per commit (spec 6).
        Assert.Equal(
            [LiveEvents.BackfillProgress, LiveEvents.UsageFlushed, LiveEvents.BackfillProgress, LiveEvents.UsageFlushed, LiveEvents.BackfillProgress],
            rig.Published);
        Assert.Equal(1, rig.ClientResolutions);
    }

    [Fact]
    public async Task TheLoopInstanceDoesEverything_TheMonitorInstanceOnlyAsksWhetherTheRunIsActive()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(14));
        rig.Archive.Handler = async (_, _, ct) =>
        {
            // Long enough reads for the monitor to poll during them.
            await Task.Delay(TimeSpan.FromSeconds(5), rig.Clock, ct);
            return FakeArchive.Complete(10, 1);
        };

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);

        var instances = rig.Store.Instances;
        Assert.Equal(2, instances.Count);
        var loop = instances[0];
        var monitor = instances[1];
        Assert.Equal(nameof(IChatLogBackfillService.TryAcquireLoopLockAsync), loop.Calls[0]);
        Assert.Equal(nameof(IChatLogBackfillService.ResetInterruptedRunsAsync), loop.Calls[1]);
        Assert.Contains(nameof(IChatLogBackfillService.ClaimNextAsync), loop.Calls);
        Assert.Equal(2, loop.Calls.Count(c => c == nameof(IChatLogBackfillService.ReplaceBlockAsync)));
        Assert.NotEmpty(monitor.Calls);
        Assert.All(monitor.Calls, c => Assert.Equal(nameof(IChatLogBackfillService.IsRunActiveAsync), c));
    }

    [Fact]
    public async Task WhileAnotherLoopHoldsTheLock_NothingIsResetOrClaimed_AndTheWarningComesOnce()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        rig.Store.SetLockHeldElsewhere(true);

        await rig.StartAsync();
        await rig.DriveForAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));

        var calls = rig.Store.Instances.Single().Calls;
        Assert.True(calls.Count >= 5);
        Assert.All(calls, c => Assert.Equal(nameof(IChatLogBackfillService.TryAcquireLoopLockAsync), c));
        Assert.Empty(rig.Archive.Requests);
        Assert.Single(rig.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("holds the lock", StringComparison.Ordinal));

        // The holder goes away: the lock is taken within one idle poll, then reset, then claim.
        rig.Store.SetLockHeldElsewhere(false);
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed, limit: TimeSpan.FromMinutes(2));
        var after = rig.Store.Instances[0].Calls.SkipWhile(c => c == nameof(IChatLogBackfillService.TryAcquireLoopLockAsync)).ToList();
        Assert.Equal(nameof(IChatLogBackfillService.ResetInterruptedRunsAsync), after[0]);
    }

    // EPIC AC 33 at the worker level: two loops on one database. Only the lock holder resets, claims
    // and reads; the other warns once and makes no request; once the holder is gone (its scope, and with
    // it the lock connection, disposed) the other takes over within one idle poll.
    [Fact]
    public async Task TwoLoops_OnlyTheLockHolderWorks_TheOtherTakesOverWhenItStops()
    {
        await using var first = new BackfillRig();
        await using var second = new BackfillRig(sharingWith: first);

        await first.StartAsync();
        await first.DriveForAsync(TimeSpan.FromSeconds(5));
        await second.StartAsync();
        await first.DriveForAsync(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(5));

        var run = first.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        first.Signal.Set();
        second.Signal.Set();
        await first.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);

        var secondsInstance = first.Store.Instances[1];
        Assert.All(secondsInstance.Calls, c => Assert.Equal(nameof(IChatLogBackfillService.TryAcquireLoopLockAsync), c));
        Assert.Single(first.Archive.Requests);
        Assert.Empty(second.Archive.Requests);
        Assert.Single(second.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("holds the lock", StringComparison.Ordinal));

        await first.StopAsync();
        var stoppedAt = first.Now;
        var next = first.Store.AddRun(WindowFrom, WindowFrom.AddDays(7), channelName: "other");
        await first.DriveUntilAsync(() => next.Status == ChatLogBackfillRunStatus.Completed);

        Assert.Single(second.Archive.Requests);
        Assert.True(second.Archive.Requests[0].AtUtc - stoppedAt <= TimeSpan.FromSeconds(second.Options.IdlePollSeconds + 1));
        Assert.Contains(nameof(IChatLogBackfillService.ResetInterruptedRunsAsync), secondsInstance.Calls);
        Assert.Single(first.Archive.Requests);
    }

    [Fact]
    public async Task TheStatusMonitor_AbortsTheInFlightRead_WithinCancelPollSeconds_WithoutAnyNudge()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(14));
        DateTime? abortedAt = null;
        rig.Archive.Handler = (_, _, ct) => FakeArchive.UntilCancelled(ct, () => abortedAt = rig.Now);

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => rig.Archive.Requests.Count == 1);
        await rig.DriveForAsync(TimeSpan.FromSeconds(7), TimeSpan.FromMilliseconds(500));
        Assert.Null(abortedAt);

        // A cancel (or a leave) flips only the row; the Redis signal is never set.
        rig.Store.SetStatus(run, ChatLogBackfillRunStatus.Cancelled);
        var flippedAt = rig.Now;
        await rig.DriveUntilAsync(() => abortedAt is not null, TimeSpan.FromMilliseconds(250));

        Assert.True(abortedAt!.Value - flippedAt <= TimeSpan.FromSeconds(rig.Options.CancelPollSeconds) + TimeSpan.FromMilliseconds(250));

        // Never another request, nothing committed, nothing failed.
        await rig.DriveForAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10));
        Assert.Single(rig.Archive.Requests);
        Assert.Empty(run.Commits);
        Assert.DoesNotContain(nameof(IChatLogBackfillService.FailAsync), rig.Store.Instances[0].Calls);
        Assert.Equal(ChatLogBackfillRunStatus.Cancelled, run.Status);
    }

    [Fact]
    public async Task ACancelDuringARetryWait_EndsTheRun_BeforeTheRetry()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Status(ChatLogDayStatus.TransportFailure, 503));

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.BlockAttempts == 1, TimeSpan.FromMilliseconds(500));
        await rig.DriveForAsync(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500));
        rig.Store.SetStatus(run, ChatLogBackfillRunStatus.Cancelled);
        var flippedAt = rig.Now;

        // AC 8(d): the monitor aborts the 30 s wait within CancelPollSeconds — without it the run would
        // only notice at the pre-request check, 20 s later.
        await rig.DriveUntilAsync(() => rig.Logger.Entries.Any(e => e.Message.Contains("stopped", StringComparison.Ordinal)), TimeSpan.FromMilliseconds(250));
        Assert.True(rig.Now - flippedAt <= TimeSpan.FromSeconds(rig.Options.CancelPollSeconds) + TimeSpan.FromMilliseconds(250));
        await rig.DriveForAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1));

        Assert.Single(rig.Archive.Requests);
        Assert.DoesNotContain(nameof(IChatLogBackfillService.FailAsync), rig.Store.Instances[0].Calls);
    }

    [Fact]
    public async Task TransportFailures_AreRetriedAfter30And120Seconds_ThenFailTheRunWithTheLastStatus()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        var statuses = new Queue<int>([502, 504, 503]);
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Status(ChatLogDayStatus.TransportFailure, statuses.Dequeue(), bytes: 100));

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Failed);

        var at = rig.Archive.Requests.Select(r => r.AtUtc).ToList();
        Assert.Equal(3, at.Count);
        Assert.InRange(at[1] - at[0], TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(33));
        Assert.InRange(at[2] - at[1], TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(123));
        Assert.Equal(ChatLogBackfillWorker.TransportFailureErrorCode, run.ErrorCode);
        Assert.Equal(503, run.ErrorHttpStatus);

        // Each attempt's bytes were booked by RecordBlockAttemptAsync, none twice by the failure.
        Assert.Equal(300, run.BytesReceived);
    }

    [Fact]
    public async Task PersistedAttempts_CarryOverARestart_TheThirdAttemptFailsWithoutAWait()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));

        // Two attempts before the restart: the row says so, the worker's memory is fresh.
        run.BlockAttempts = 2;
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Status(ChatLogDayStatus.BodyTimeout, 200));

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Failed);

        Assert.Single(rig.Archive.Requests);
        Assert.Equal(ChatLogBackfillWorker.TransportFailureErrorCode, run.ErrorCode);
    }

    [Fact]
    public async Task A429_PausesTheRun_OutsideTheRun_AndTheSameBlockIsFetchedOnlyWhenDue()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        var answers = new Queue<ChatLogRangeResult>(
            [FakeArchive.Status(ChatLogDayStatus.RateLimited, 429, retryAfter: TimeSpan.FromSeconds(120)), FakeArchive.Complete(10, 1)]);
        rig.Archive.Handler = (_, _, _) => Task.FromResult(answers.Dequeue());

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Paused);
        var pausedUntil = run.PausedUntilUtc!.Value;
        Assert.InRange(pausedUntil - rig.Archive.Requests[0].AtUtc, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(121));

        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);

        Assert.Equal(2, rig.Archive.Requests.Count);
        Assert.Equal(rig.Archive.Requests[0].FromUtc, rig.Archive.Requests[1].FromUtc);
        Assert.True(rig.Archive.Requests[1].AtUtc >= pausedUntil);
        Assert.Equal(2, run.Claims);

        // Claim, pause, re-claim (resume), commit.
        Assert.Equal(4, rig.Published.Count(p => p == LiveEvents.BackfillProgress));
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("paused by a 429", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePauseLength_FollowsThePersistedCount_NotTheWorkersMemory()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));

        // Three pauses on this block before a restart; no Retry-After this time.
        run.PauseCount = 3;
        var answers = new Queue<ChatLogRangeResult>([FakeArchive.Status(ChatLogDayStatus.RateLimited, 429), FakeArchive.Complete(1, 1)]);
        rig.Archive.Handler = (_, _, _) => Task.FromResult(answers.Dequeue());

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed, TimeSpan.FromSeconds(5));

        // 60 s · 2^3 = 480 s: the worker waited what the row decided.
        Assert.True(rig.Archive.Requests[1].AtUtc - rig.Archive.Requests[0].AtUtc >= TimeSpan.FromSeconds(480));
        Assert.Equal(0, run.PauseCount);
    }

    [Fact]
    public async Task TheEleventhConsecutive429_FailsTheRun_AndNothingIsReadAgain()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        run.PauseCount = 10;
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Status(ChatLogDayStatus.RateLimited, 429));

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Failed);
        await rig.DriveForAsync(TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));

        Assert.Single(rig.Archive.Requests);
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("rate_limited", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheProviderCooldown_HoldsAYoungerRun_EvenAfterTheHeadIsGone()
    {
        await using var rig = new BackfillRig();

        // Run A was paused and then cancelled; the archive's cooldown outlives it (D31).
        var cancelledHead = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        rig.Store.SetStatus(cancelledHead, ChatLogBackfillRunStatus.Cancelled);
        var younger = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7), channelName: "other");
        var cooldownUntil = rig.Now.AddMinutes(5);
        rig.Store.CooldownUntilUtc = cooldownUntil;

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => younger.Status == ChatLogBackfillRunStatus.Completed, TimeSpan.FromSeconds(5));

        var request = Assert.Single(rig.Archive.Requests);
        Assert.True(request.AtUtc >= cooldownUntil);
        Assert.Equal("tw-other", request.TwitchChannelId);
    }

    public static TheoryData<string> UncommittedResults => ["RunNotActive-cancelled", "LiveRowConflict", "ChannelGone"];

    [Theory]
    [MemberData(nameof(UncommittedResults))]
    public async Task AnUncommittedBlock_StopsTheRunWithoutAFurtherRequest_AndOnlyTheRightCasesRecordAnError(string result)
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(14));
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Complete(10, 1));
        rig.Store.ReplaceOverride = r =>
        {
            switch (result)
            {
                case "RunNotActive-cancelled":
                    rig.Store.SetStatus(r, ChatLogBackfillRunStatus.Cancelled);
                    return new ChatLogBackfillBlockResult.RunNotActive();
                case "LiveRowConflict":
                    return new ChatLogBackfillBlockResult.LiveRowConflict();
                default:
                    rig.Store.SetStatus(r, ChatLogBackfillRunStatus.Cancelled);
                    return new ChatLogBackfillBlockResult.ChannelGone();
            }
        };

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status != ChatLogBackfillRunStatus.Running && run.Status != ChatLogBackfillRunStatus.Queued);
        await rig.DriveForAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10));

        Assert.Single(rig.Archive.Requests);
        var expected = result == "LiveRowConflict" ? ChatLogBackfillWorker.LiveRowConflictErrorCode : null;
        Assert.Equal(expected, run.ErrorCode);

        // A live-row conflict books the block's bytes with the failure (operator decision B, D47).
        Assert.Equal(expected is null ? 0 : 10, run.BytesReceived);
        Assert.Equal(expected is null ? 0 : 1, rig.Store.Instances[0].Calls.Count(c => c == nameof(IChatLogBackfillService.FailAsync)));
    }

    [Theory]
    [InlineData(ChatLogDayStatus.MalformedResponse, ChatLogBackfillWorker.MalformedResponseErrorCode)]
    [InlineData(ChatLogDayStatus.LineTooLong, ChatLogBackfillWorker.MalformedResponseErrorCode)]
    [InlineData(ChatLogDayStatus.ByteCapExceeded, ChatLogBackfillWorker.BlockTooLargeErrorCode)]
    public async Task AnUnreadableOrOversizedBlock_FailsTheRun_BookingItsBytes(ChatLogDayStatus status, string errorCode)
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(14));
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Status(status, 200, bytes: 777));

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Failed);

        Assert.Equal(errorCode, run.ErrorCode);
        Assert.Equal(200, run.ErrorHttpStatus);
        Assert.Equal(777, run.BytesReceived);
        Assert.Single(rig.Archive.Requests);
        Assert.Equal(LiveEvents.BackfillProgress, rig.Published[^1]);
    }

    [Fact]
    public async Task AnEmptySnapshot_FailsTheRunBeforeAnyRequest()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7), snapshot: []);

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Failed);

        Assert.Equal(ChatLogBackfillWorker.SnapshotMissingErrorCode, run.ErrorCode);
        Assert.Empty(rig.Archive.Requests);
    }

    [Fact]
    public async Task ARunNoLongerRunningBeforeItsRequest_MakesNoRequest()
    {
        await using var rig = new BackfillRig(o => o.RequestDelaySeconds = 30);
        var first = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        var second = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7), channelName: "other");

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => first.Status == ChatLogBackfillRunStatus.Completed && second.Status == ChatLogBackfillRunStatus.Running);

        // The second run waits out the spacing; it is cancelled meanwhile and its request never happens.
        rig.Store.SetStatus(second, ChatLogBackfillRunStatus.Cancelled);
        await rig.DriveForAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1));

        Assert.Single(rig.Archive.Requests);
    }

    [Fact]
    public async Task AnUnexpectedError_FailsTheRunAsWorkerError_AndTheLoopGoesOn()
    {
        await using var rig = new BackfillRig();
        var broken = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        var next = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7), channelName: "other");
        rig.Store.ReplaceOverride = r => r.Id == broken.Id ? throw new InvalidOperationException("boom") : null;

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => next.Status == ChatLogBackfillRunStatus.Completed);

        Assert.Equal(ChatLogBackfillWorker.WorkerErrorCode, broken.ErrorCode);
        Assert.False(rig.ExecuteTask!.IsCompleted);

        // Neither run's monitor outlives its run.
        var monitorCalls = MonitorCalls(rig);
        await rig.DriveForAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(monitorCalls, MonitorCalls(rig));
    }

    [Fact]
    public async Task ALoopFailure_CostsOneIdlePoll_AndTheLockIsCheckedAndTheResetRepeated()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        var faults = 1;
        rig.Store.ClaimFault = () => faults-- > 0 ? new TimeoutException("database away") : null;

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);

        var calls = rig.Store.Instances[0].Calls;
        Assert.Equal(2, calls.Count(c => c == nameof(IChatLogBackfillService.ResetInterruptedRunsAsync)));
        Assert.True(rig.Archive.Requests[0].AtUtc - BackfillRig.Start.UtcDateTime >= TimeSpan.FromSeconds(rig.Options.IdlePollSeconds));
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("loop failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANudge_WakesTheIdleLoop_BeforeTheIdlePoll()
    {
        await using var rig = new BackfillRig();

        await rig.StartAsync();
        await rig.DriveForAsync(TimeSpan.FromSeconds(5));
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        var enqueuedAt = rig.Now;
        rig.Signal.Set();

        await rig.DriveUntilAsync(() => rig.Archive.Requests.Count == 1, TimeSpan.FromMilliseconds(500));

        Assert.True(rig.Archive.Requests[0].AtUtc - enqueuedAt < TimeSpan.FromSeconds(rig.Options.IdlePollSeconds));
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);
    }

    [Fact]
    public async Task Logs_NeverCarryMessageTextOrChatterIds()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        rig.Archive.Handler = async (request, onMessage, _) =>
        {
            await onMessage(Message(request.FromUtc.AddHours(1), "445566778", "SecretWords Kappa"));
            return FakeArchive.Complete(10, 1);
        };

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);

        Assert.NotEmpty(rig.Logger.Entries);
        Assert.DoesNotContain(rig.Logger.Entries, e => e.Message.Contains("445566778", StringComparison.Ordinal)
            || e.Message.Contains("SecretWords", StringComparison.Ordinal));
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("block 1/1", StringComparison.Ordinal));
    }

    // Operator decision A (2026-10-09): RunNotActive while the row still says running is not a cancel
    // and not yet a failure. The block is abandoned unwritten, the loop takes the lost-lock path and
    // resumes from the persisted WeeksDone; the third time in a row the run fails as worker_error,
    // booking the bytes of the read it could not commit (decision B).
    [Fact]
    public async Task RunNotActiveWhileStillRunning_IsAbandonedAndResumed_TheThirdInARowFailsAsWorkerError()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(14));
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Complete(500, 3));
        rig.Store.ReplaceOverride = _ => new ChatLogBackfillBlockResult.RunNotActive();

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Failed);

        Assert.Equal(ChatLogBackfillWorker.WorkerErrorCode, run.ErrorCode);
        Assert.Equal(500, run.BytesReceived);
        Assert.Empty(run.Commits);

        // Three reads of the same block, each resume one idle poll later, each after a fresh reset.
        var requests = rig.Archive.Requests;
        Assert.Equal(3, requests.Count);
        Assert.All(requests, r => Assert.Equal(requests[0].FromUtc, r.FromUtc));
        Assert.True(requests[1].AtUtc - requests[0].AtUtc >= TimeSpan.FromSeconds(rig.Options.IdlePollSeconds));
        Assert.Equal(3, rig.Store.Instances[0].Calls.Count(c => c == nameof(IChatLogBackfillService.ResetInterruptedRunsAsync)));
        Assert.Equal(3, run.Claims);
    }

    [Fact]
    public async Task ACommittedBlock_ResetsTheAbandonCount()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(21));
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Complete(10, 1));

        // Two refusals before every commit: never three in a row.
        var refusals = 0;
        rig.Store.ReplaceOverride = _ =>
        {
            if (refusals++ % 3 < 2)
            {
                return new ChatLogBackfillBlockResult.RunNotActive();
            }

            return null;
        };

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed, TimeSpan.FromSeconds(5));

        Assert.Null(run.ErrorCode);
        Assert.Equal(3, run.Commits.Count);
        Assert.Equal(9, rig.Archive.Requests.Count);
    }

    // D46: commits happen only under the lock. The loop's lock connection dies mid-read and a second
    // loop takes the lock: the first loop must not commit the block it read, and the healthy run must
    // not end as worker_error — the new holder resumes it.
    [Fact]
    public async Task ALockLostMidRun_IsNeverCommittedUnder_AndTheNewHolderCompletesTheRun()
    {
        await using var first = new BackfillRig();
        await using var second = new BackfillRig(sharingWith: first);
        var run = first.Store.AddRun(WindowFrom, WindowFrom.AddDays(14));
        first.Archive.Handler = async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), first.Clock, ct);
            return FakeArchive.Complete(10, 1);
        };
        second.Archive.Handler = first.Archive.Handler;

        await first.StartAsync();
        await first.DriveUntilAsync(() => first.Archive.Requests.Count == 1);
        first.Store.ReleaseLock(first.Store.Instances[0]);
        await second.StartAsync();
        await first.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));

        Assert.Null(run.ErrorCode);
        Assert.Equal([WindowFrom, WindowFrom.AddDays(7)], run.Commits.Select(c => c.From));
        Assert.DoesNotContain(nameof(IChatLogBackfillService.ReplaceBlockAsync), first.Store.Instances[0].Calls);
        Assert.Single(first.Archive.Requests);
    }

    [Fact]
    public async Task ALockHeldElsewhereBeforeTheCommit_AbandonsTheBlock_AndTheRunResumesAfterwards()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        var reads = 0;
        rig.Archive.Handler = (_, _, _) =>
        {
            if (reads++ == 0)
            {
                // Another process holds the lock by the time this read is done.
                rig.Store.ReleaseLock(rig.Store.Instances[0]);
                rig.Store.SetLockHeldElsewhere(true);
            }

            return Task.FromResult(FakeArchive.Complete(10, 1));
        };

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => rig.Logger.Entries.Any(e => e.Message.Contains("abandoned", StringComparison.Ordinal)));
        Assert.Empty(run.Commits);
        Assert.DoesNotContain(nameof(IChatLogBackfillService.ReplaceBlockAsync), rig.Store.Instances[0].Calls);

        // The other holder goes away without touching the row: this loop retakes the lock, resets and resumes.
        rig.Store.SetLockHeldElsewhere(false);
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);

        Assert.Null(run.ErrorCode);
        Assert.Single(run.Commits);
        Assert.Equal(2, rig.Archive.Requests.Count);
    }

    // P2-2: a failure before the block loop leaves nothing running behind it, and the run is resumed —
    // three times, then worker_error, without a single archive request.
    [Theory]
    [InlineData("snapshot")]
    [InlineData("client")]
    public async Task AFailureBeforeTheBlockLoop_LeaksNoMonitor_AndThreeInARowFailTheRun(string where)
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        if (where == "snapshot")
        {
            rig.Store.SnapshotFault = () => new InvalidOperationException("snapshot read failed");
        }
        else
        {
            rig.ClientResolutionFault = () => new InvalidOperationException("client misconfigured");
        }

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Failed);

        Assert.Equal(ChatLogBackfillWorker.WorkerErrorCode, run.ErrorCode);
        Assert.Equal(3, run.Claims);
        Assert.Empty(rig.Archive.Requests);

        // Only the loop's instance was ever resolved: no monitor was started for a run that never read.
        Assert.Single(rig.Store.Instances);
    }

    public static TheoryData<string> TransientErrors => ["timeout", "db"];

    [Theory]
    [MemberData(nameof(TransientErrors))]
    public async Task ATransientDatabaseErrorOnTheCommit_IsAbandonedAndResumed_NotAFailure(string kind)
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        var faults = 1;
        rig.Store.ReplaceOverride = _ => faults-- > 0
            ? throw (kind == "timeout" ? new TimeoutException("command timeout") : new TransientDbException())
            : null;

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);

        Assert.Null(run.ErrorCode);
        Assert.Single(run.Commits);
        Assert.Equal(2, rig.Archive.Requests.Count);
    }

    // The loop's first database contact is the lock, not the service's construction: an outage there
    // costs an idle poll and never escapes ExecuteAsync.
    [Fact]
    public async Task ADatabaseErrorOnTheFirstLockAttempt_CostsAnIdlePoll_NotTheHost()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        var faults = 1;
        rig.Store.LockFault = _ => faults-- > 0 ? new TimeoutException("database away") : null;

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);

        Assert.False(rig.ExecuteTask!.IsCompleted);
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("loop failed", StringComparison.Ordinal));
        Assert.True(rig.Archive.Requests[0].AtUtc - BackfillRig.Start.UtcDateTime >= TimeSpan.FromSeconds(rig.Options.IdlePollSeconds));
    }

    // A service that cannot even be resolved (a broken registration) is retried, never the host's end.
    [Fact]
    public async Task AServiceThatCannotBeResolved_IsRetriedAfterAnIdlePoll()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        var faults = 1;
        rig.ServiceResolutionFault = () => faults-- > 0 ? new InvalidOperationException("no registration") : null;

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);

        Assert.False(rig.ExecuteTask!.IsCompleted);
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not start its loop", StringComparison.Ordinal));
    }

    // Review P2: every write after a read is lock-guarded, not only the commit. A loop whose lock
    // connection died during the read must not record an attempt, a pause or a failure — those would
    // land on the run another loop has meanwhile reset and re-claimed.
    [Theory]
    [InlineData(ChatLogDayStatus.TransportFailure)]
    [InlineData(ChatLogDayStatus.RateLimited)]
    [InlineData(ChatLogDayStatus.MalformedResponse)]
    public async Task ALockLostDuringTheRead_WritesNothingForThatRead(ChatLogDayStatus status)
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));

        // On its last transport attempt: without the guard, this read's failure would fail the run.
        run.BlockAttempts = 2;
        rig.Archive.Handler = (_, _, _) =>
        {
            rig.Store.ReleaseLock(rig.Store.Instances[0]);
            rig.Store.SetLockHeldElsewhere(true);
            return Task.FromResult(FakeArchive.Status(status, status == ChatLogDayStatus.RateLimited ? 429 : 503, bytes: 64));
        };

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => rig.Logger.Entries.Any(e => e.Message.Contains("lost during a read", StringComparison.Ordinal)));
        await rig.DriveForAsync(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(10));

        var calls = rig.Store.Instances[0].Calls;
        Assert.DoesNotContain(nameof(IChatLogBackfillService.RecordBlockAttemptAsync), calls);
        Assert.DoesNotContain(nameof(IChatLogBackfillService.PauseAsync), calls);
        Assert.DoesNotContain(nameof(IChatLogBackfillService.FailAsync), calls);
        Assert.Equal(ChatLogBackfillRunStatus.Running, run.Status);
        Assert.Equal(2, run.BlockAttempts);
        Assert.Equal(0, run.BytesReceived);
        Assert.Null(rig.Store.CooldownUntilUtc);
        Assert.Single(rig.Archive.Requests);
    }

    // The failure recorded from the catch is a write after a read as well.
    [Fact]
    public async Task AnUnexpectedErrorAfterTheLockWasLost_RecordsNoFailure()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        rig.Store.ReplaceOverride = _ =>
        {
            rig.Store.ReleaseLock(rig.Store.Instances[0]);
            rig.Store.SetLockHeldElsewhere(true);
            throw new InvalidOperationException("boom");
        };

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => rig.Logger.Entries.Any(e => e.Message.Contains("lost before recording worker_error", StringComparison.Ordinal)));

        Assert.DoesNotContain(nameof(IChatLogBackfillService.FailAsync), rig.Store.Instances[0].Calls);
        Assert.Equal(ChatLogBackfillRunStatus.Running, run.Status);
    }

    // Review P3: the third strike while the lock is not held writes nothing; the next claim under the
    // lock records worker_error before any request, with 0 bytes (no read of its own).
    [Fact]
    public async Task AThirdStrikeWithoutTheLock_IsRecordedAtTheNextClaim_WithoutARequest()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Complete(500, 3));
        var refusals = 0;
        rig.Store.ReplaceOverride = _ =>
        {
            if (++refusals == 3)
            {
                // The lock goes away between the commit attempt and the strike's own lock check.
                rig.Store.ReleaseLock(rig.Store.Instances[0]);
                rig.Store.SetLockHeldElsewhere(true);
            }

            return new ChatLogBackfillBlockResult.RunNotActive();
        };

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => rig.Logger.Entries.Any(e => e.Message.Contains("3 of 3", StringComparison.Ordinal)));

        Assert.Equal(ChatLogBackfillRunStatus.Running, run.Status);
        Assert.DoesNotContain(nameof(IChatLogBackfillService.FailAsync), rig.Store.Instances[0].Calls);

        rig.Store.SetLockHeldElsewhere(false);
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Failed);

        Assert.Equal(ChatLogBackfillWorker.WorkerErrorCode, run.ErrorCode);
        Assert.Equal(0, run.BytesReceived);
        Assert.Equal(3, rig.Archive.Requests.Count);
        Assert.Equal(4, run.Claims);
    }

    // Review P3: a pending count belongs to the progress it was counted at. Another loop that moved the
    // run on meanwhile makes it stale — the next refusal is the first, not the third.
    [Fact]
    public async Task AnAbandonCount_IsDropped_WhenTheRunProgressedElsewhere()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(21));
        var calls = 0;
        rig.Store.ReplaceOverride = _ => ++calls <= 3 ? new ChatLogBackfillBlockResult.RunNotActive() : null;

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => rig.Logger.Entries.Any(e => e.Message.Contains("2 of 3", StringComparison.Ordinal)));

        // While this loop waits out its idle poll, another loop commits the first block.
        run.WeeksDone = 1;
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);

        Assert.Null(run.ErrorCode);
        Assert.Equal([WindowFrom.AddDays(7), WindowFrom.AddDays(14)], run.Commits.Select(c => c.From));
        Assert.Contains(rig.Logger.Entries, e => e.Message.Contains("1 of 3", StringComparison.Ordinal) && e.Message.Contains("not accept", StringComparison.Ordinal));
    }

    // Review P3: a transient error on a commit the server applied, landing on the third strike, must not
    // fail (and double-book) a run that just progressed: the failure is deferred to the next claim,
    // which sees the progress and drops the count.
    [Fact]
    public async Task ATransientErrorOnAnAppliedCommit_AtTheThirdStrike_DoesNotFailTheRun()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(14));
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Complete(100, 1));
        var calls = 0;
        rig.Store.ReplaceOverride = r =>
        {
            switch (++calls)
            {
                case <= 2:
                    return new ChatLogBackfillBlockResult.RunNotActive();
                case 3:
                    // The server commits; the client only sees the connection drop.
                    r.WeeksDone++;
                    r.BytesReceived += 100;
                    r.Commits.Add(new FakeCommit(WindowFrom, WindowFrom.AddDays(7), [], 100, 1));
                    throw new TimeoutException("connection dropped after commit");
                default:
                    return null;
            }
        };

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);

        Assert.Null(run.ErrorCode);
        Assert.Equal(200, run.BytesReceived);
        Assert.Equal([WindowFrom, WindowFrom.AddDays(7)], run.Commits.Select(c => c.From));
        Assert.DoesNotContain(nameof(IChatLogBackfillService.FailAsync), rig.Store.Instances[0].Calls);
    }

    // Review P3: a 429 books no bytes (D47), also when its pause write fails non-transiently.
    [Fact]
    public async Task A429WhosePauseCannotBeWritten_BooksNoBytes()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Status(ChatLogDayStatus.RateLimited, 429, bytes: 999));
        rig.Store.PauseFault = () => new InvalidOperationException("boom");

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Failed);

        Assert.Equal(ChatLogBackfillWorker.WorkerErrorCode, run.ErrorCode);
        Assert.Equal(0, run.BytesReceived);
    }

    // Check (spacing never below the configured delay): a timer may fire a little early against the
    // wall clock. The worker re-checks the clock after the wait, so the next request still starts no
    // earlier than RequestDelaySeconds after the previous one.
    [Fact]
    public async Task TheSpacing_HoldsEvenWhenTimersFireEarly()
    {
        await using var rig = new BackfillRig(workerClock: clock => new EarlyTimerTimeProvider(clock));
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(14));

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Commits.Count == 1, TimeSpan.FromMilliseconds(1));

        // Let the worker reach its spacing wait (registered at the first request's instant) before the
        // clock moves; otherwise the wait would only start after the jump below.
        await Task.Delay(200);
        rig.Clock.Advance(TimeSpan.FromSeconds(rig.Options.RequestDelaySeconds) - TimeSpan.FromMilliseconds(2));
        await rig.DriveUntilAsync(() => rig.Archive.Requests.Count == 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5));

        Assert.True(rig.Archive.Requests[1].AtUtc - rig.Archive.Requests[0].AtUtc >= TimeSpan.FromSeconds(rig.Options.RequestDelaySeconds));
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);
    }

    // Interpretation (c): a failure that cannot be recorded leaves the row running; it is an abandon
    // event, so the block is read again once later and the failure recorded then.
    [Fact]
    public async Task AFailureThatCouldNotBeRecorded_IsRecordedAtTheNextClaim()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Status(ChatLogDayStatus.MalformedResponse, 200, bytes: 50));
        var faults = 1;
        rig.Store.FailFault = () => faults-- > 0 ? new TimeoutException("database away") : null;

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Failed);

        Assert.Equal(ChatLogBackfillWorker.MalformedResponseErrorCode, run.ErrorCode);
        Assert.Equal(2, rig.Archive.Requests.Count);
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("recording the failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailureThatCanNeverBeRecorded_CostsAtMostThreeRequests()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Status(ChatLogDayStatus.MalformedResponse, 200));
        rig.Store.FailFault = () => new TimeoutException("database away");

        await rig.StartAsync();
        await rig.DriveForAsync(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5));

        Assert.Equal(3, rig.Archive.Requests.Count);
        Assert.Equal(ChatLogBackfillRunStatus.Running, run.Status);
        Assert.False(rig.ExecuteTask!.IsCompleted);
    }

    // Interpretation (f): a plan that does not fit the row is deterministic, so it fails at once.
    [Fact]
    public async Task APlanThatDoesNotFitTheRow_FailsAsWorkerError_WithoutARequest()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        run.WeeksTotal = 5;

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Failed);

        Assert.Equal(ChatLogBackfillWorker.WorkerErrorCode, run.ErrorCode);
        Assert.Equal(1, run.Claims);
        Assert.Empty(rig.Archive.Requests);
    }

    [Fact]
    public async Task AMonitorThatCannotReadTheRow_DoesNotCancelIt()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        rig.Archive.Handler = async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), rig.Clock, ct);
            return FakeArchive.Complete(10, 1);
        };
        rig.Store.IsRunActiveFault = instance => instance == rig.Store.Instances[0] ? null : new TimeoutException("database away");

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed, TimeSpan.FromMilliseconds(500));

        Assert.Single(run.Commits);
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("status monitor could not read", StringComparison.Ordinal));
    }

    // EPIC AC 28 at the worker: a run resumed with three pauses on the row commits its block, and the
    // next block's 429 pauses 60 s — the commit reset the row's count, and the worker waits what the
    // row now says.
    [Fact]
    public async Task ResumeThenCommitThen429_PausesSixtySeconds()
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(14));
        run.PauseCount = 3;
        var answers = new Queue<ChatLogRangeResult>(
            [FakeArchive.Complete(10, 1), FakeArchive.Status(ChatLogDayStatus.RateLimited, 429), FakeArchive.Complete(10, 1)]);
        rig.Archive.Handler = (_, _, _) => Task.FromResult(answers.Dequeue());

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Completed);

        var gap = rig.Archive.Requests[2].AtUtc - rig.Archive.Requests[1].AtUtc;
        Assert.InRange(gap, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(119));
    }

    [Fact]
    public async Task TransportRetryDelays_AreCappedAtMaxRetryAfterSeconds()
    {
        await using var rig = new BackfillRig(o =>
        {
            o.TransportRetries = 5;
            o.MaxRetryAfterSeconds = 100;
        });
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(7));
        rig.Archive.Handler = (_, _, _) => Task.FromResult(FakeArchive.Status(ChatLogDayStatus.TransportFailure, 503));

        await rig.StartAsync();
        await rig.DriveUntilAsync(() => run.Status == ChatLogBackfillRunStatus.Failed);

        var at = rig.Archive.Requests.Select(r => r.AtUtc).ToList();
        Assert.Equal(5, at.Count);
        Assert.InRange(at[1] - at[0], TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(33));
        for (var i = 2; i < at.Count; i++)
        {
            Assert.InRange(at[i] - at[i - 1], TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(103));
        }
    }

    private static int MonitorCalls(BackfillRig rig) => rig.Store.Instances.Skip(1).Sum(i => i.Calls.Count);

    private static ChatLogMessage Message(DateTime at, string userId, string text) => new(at, userId, [], "room1", null, false, text);

    private sealed class TransientDbException : System.Data.Common.DbException
    {
        public override bool IsTransient => true;
    }

    // Timers of a second or more fire 5 ms early, the way a coarse timer queue can against the wall clock.
    private sealed class EarlyTimerTimeProvider(TimeProvider inner) : TimeProvider
    {
        private static readonly TimeSpan Early = TimeSpan.FromMilliseconds(5);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime >= TimeSpan.FromSeconds(1) ? dueTime - Early : dueTime, period);
    }
}
