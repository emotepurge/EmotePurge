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

    public static TheoryData<string> UncommittedResults => ["RunNotActive-cancelled", "RunNotActive-still-running", "LiveRowConflict", "ChannelGone"];

    [Theory]
    [MemberData(nameof(UncommittedResults))]
    public async Task AnUncommittedBlock_StopsTheRunWithoutAFurtherRequest_AndOnlyTheRightCasesRecordAnError(string result)
    {
        await using var rig = new BackfillRig();
        var run = rig.Store.AddRun(WindowFrom, WindowFrom.AddDays(14));
        rig.Store.ReplaceOverride = r =>
        {
            switch (result)
            {
                case "RunNotActive-cancelled":
                    rig.Store.SetStatus(r, ChatLogBackfillRunStatus.Cancelled);
                    return new ChatLogBackfillBlockResult.RunNotActive();
                case "RunNotActive-still-running":
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
        var expected = result switch
        {
            "LiveRowConflict" => ChatLogBackfillWorker.LiveRowConflictErrorCode,

            // Still running after RunNotActive: the block did not fit the plan; stopping silently would
            // hand the head straight back to the claim.
            "RunNotActive-still-running" => ChatLogBackfillWorker.WorkerErrorCode,
            _ => null,
        };
        Assert.Equal(expected, run.ErrorCode);
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

    private static ChatLogMessage Message(DateTime at, string userId, string text) => new(at, userId, [], "room1", null, false, text);
}
