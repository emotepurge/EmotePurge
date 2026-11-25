using Xunit;
using EmotePurge.Core.Services;
using EmotePurge.Worker;

namespace EmotePurge.Worker.Tests;

public class ChannelJoinTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static double Waiting(ChannelJoinTracker tracker, string name, double atSeconds) =>
        tracker.Snapshot(At(atSeconds)).Single(s => s.Name == name).UnconfirmedSeconds;

    private static ChannelJoinTracker Connected()
    {
        var tracker = new ChannelJoinTracker();
        tracker.SetConnected(At(0));
        return tracker;
    }

    // ---- accrual clock ----

    [Fact]
    public void Intent_WhileConnected_StartsRunningImmediately()
    {
        var tracker = Connected();

        Assert.True(tracker.RecordIntent("a", At(10)));

        Assert.Equal(5, Waiting(tracker, "a", 15));
    }

    [Fact]
    public void Intent_ForExistingChannel_ChangesNothing()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));

        Assert.False(tracker.RecordIntent("a", At(50)));

        Assert.Equal(60, Waiting(tracker, "a", 60));
    }

    [Fact]
    public void Intent_WhileDisconnected_DoesNotRun_ThenRunsFromConnect()
    {
        var tracker = new ChannelJoinTracker();
        tracker.RecordIntent("a", At(0));

        Assert.Equal(0, Waiting(tracker, "a", 100));

        tracker.SetConnected(At(100));

        Assert.Equal(7, Waiting(tracker, "a", 107));
    }

    [Fact]
    public void LongDisconnect_RightAfterIntent_CountsZero()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(10));
        tracker.SetDisconnected(At(10));

        Assert.Equal(0, Waiting(tracker, "a", 100_000));
    }

    [Fact]
    public void Accrual_SumsOnlyTheConnectedSections()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.SetDisconnected(At(10)); // 10 s counted
        tracker.SetConnected(At(1_000));
        tracker.SetDisconnected(At(1_005)); // +5
        tracker.SetConnected(At(5_000));

        Assert.Equal(15 + 3, Waiting(tracker, "a", 5_003));
    }

    [Fact]
    public void SetConnected_Twice_DoesNotDoubleCount()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.SetConnected(At(50));

        Assert.Equal(60, Waiting(tracker, "a", 60));
    }

    [Fact]
    public void SetDisconnected_Twice_DoesNotDoubleAdd()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.SetDisconnected(At(10));
        tracker.SetDisconnected(At(500));

        Assert.Equal(10, Waiting(tracker, "a", 1_000));
    }

    [Fact]
    public void Snapshot_MidSection_IncludesTheRunningPart()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.SetDisconnected(At(4));
        tracker.SetConnected(At(100));

        Assert.Equal(4 + 6, Waiting(tracker, "a", 106));
    }

    [Fact]
    public void ClockGoingBackwards_YieldsZeroNeverNegative()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(100));

        Assert.Equal(0, Waiting(tracker, "a", 50));

        tracker.SetDisconnected(At(40));
        Assert.Equal(0, Waiting(tracker, "a", 1_000));
    }

    [Fact]
    public void FormerlyConfirmedChannel_StartsAtZeroAfterDisconnect()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.MarkConfirmed("a");
        tracker.SetDisconnected(At(500));
        tracker.SetConnected(At(600));

        Assert.False(tracker.IsConfirmed("a"));
        Assert.Equal(5, Waiting(tracker, "a", 605));
    }

    [Fact]
    public void NeverConfirmedChannel_KeepsItsSumAcrossDisconnect()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.SetDisconnected(At(30));
        tracker.SetConnected(At(90));

        Assert.Equal(30, Waiting(tracker, "a", 90));
    }

    [Fact]
    public void Confirmation_ResetsTheClock_AndClearsTheReason()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.RecordFailure("a", TwitchJoinFailureReasons.NoConfirmation);

        Assert.True(tracker.MarkConfirmed("a"));

        var state = tracker.Snapshot(At(99)).Single();
        Assert.True(state.Confirmed);
        Assert.Equal(0, state.UnconfirmedSeconds);
        Assert.Null(state.Reason);
    }

    [Fact]
    public void Remove_DropsTheEntry_AndALaterIntentStartsFresh()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));

        Assert.True(tracker.Remove("a"));
        Assert.False(tracker.Contains("a"));

        tracker.RecordIntent("a", At(100));
        Assert.Equal(10, Waiting(tracker, "a", 110));
    }

    [Fact]
    public void Confirmation_AfterRemove_DoesNotInsert()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.Remove("a");

        Assert.False(tracker.MarkConfirmed("a"));
        Assert.False(tracker.Contains("a"));
    }

    [Fact]
    public void Names_AreCaseInsensitive()
    {
        var tracker = Connected();
        tracker.RecordIntent("HandOfBlood", At(0));

        Assert.True(tracker.Contains("handofblood"));
        Assert.False(tracker.RecordIntent("HANDOFBLOOD", At(1)));
        tracker.BeginAttempt("HANDOFBLOOD");
        Assert.True(tracker.MarkConfirmed("handofblood"));
        Assert.True(tracker.IsConfirmed("HandOfBlood"));
        Assert.Single(tracker.Names());
    }

    [Fact]
    public void Snapshot_IsOrderedOrdinalByName()
    {
        var tracker = Connected();
        tracker.RecordIntent("b", At(0));
        tracker.RecordIntent("a", At(0));
        tracker.RecordIntent("c", At(0));

        Assert.Equal(["a", "b", "c"], tracker.Snapshot(At(1)).Select(s => s.Name));
    }

    // ---- attempt window ----

    [Fact]
    public void Echo_WithoutOpenAttempt_DoesNotConfirm()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));

        Assert.False(tracker.MarkConfirmed("a"));
        Assert.False(tracker.IsConfirmed("a"));
    }

    [Fact]
    public void Echo_AfterTimeoutInTheSameAttempt_StillConfirms()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.RecordFailure("a", TwitchJoinFailureReasons.NoConfirmation);

        Assert.True(tracker.MarkConfirmed("a"));
    }

    [Fact]
    public void PartOnTheWire_ClosesTheAttempt()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.RecordPartSent("a");

        Assert.False(tracker.MarkConfirmed("a"));
    }

    [Fact]
    public void BeginAttempt_OnConfirmedChannel_ChangesNothing()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.MarkConfirmed("a");

        tracker.BeginAttempt("a");

        Assert.True(tracker.IsConfirmed("a"));
        Assert.False(tracker.MarkConfirmed("a"));
    }

    [Fact]
    public void BeginAttempt_OnUnknownChannel_DoesNotInsert()
    {
        var tracker = Connected();

        tracker.BeginAttempt("ghost");

        Assert.False(tracker.Contains("ghost"));
    }

    [Fact]
    public void Disconnect_ClosesAllAttempts()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.SetDisconnected(At(1));
        tracker.SetConnected(At(2));

        Assert.False(tracker.MarkConfirmed("a"));
    }

    [Fact]
    public void LateEcho_AfterLeaveAndReactivation_DoesNotConfirmTheNewIntent()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.Remove("a");
        tracker.RecordIntent("a", At(10)); // re-wanted, no JOIN on the wire yet

        Assert.False(tracker.MarkConfirmed("a"));
        Assert.False(tracker.IsConfirmed("a"));
    }

    // ---- PART echo ----

    [Fact]
    public void PartEcho_OnConfirmedChannel_WithdrawsTheConfirmation_ClockRuns()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.MarkConfirmed("a");

        tracker.MarkParted("a", At(100));

        Assert.False(tracker.IsConfirmed("a"));
        Assert.Equal(8, Waiting(tracker, "a", 108));
        // a new attempt is possible again
        tracker.BeginAttempt("a");
        Assert.True(tracker.MarkConfirmed("a"));
    }

    [Fact]
    public void PartEcho_OnConfirmedChannel_WhileDisconnected_DoesNotRun()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.MarkConfirmed("a");
        tracker.SetDisconnected(At(10));

        // confirmation is already gone after the disconnect; the echo must not start a clock
        tracker.MarkParted("a", At(20));

        Assert.Equal(0, Waiting(tracker, "a", 1_000));
    }

    [Fact]
    public void PartEcho_OnUnconfirmedChannel_LeavesClockAndAttemptUntouched()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");

        tracker.MarkParted("a", At(50));

        Assert.Equal(60, Waiting(tracker, "a", 60));
        Assert.True(tracker.MarkConfirmed("a")); // attempt still open
    }

    [Fact]
    public void PartEcho_OnUnknownChannel_DoesNothing()
    {
        var tracker = Connected();

        tracker.MarkParted("ghost", At(1));

        Assert.False(tracker.Contains("ghost"));
    }

    // ---- reasons ----

    [Fact]
    public void Limit_ThenTimeoutInTheSameAttempt_KeepsTheLimit()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");

        Assert.Equal(JoinFailureOutcome.Changed,
            tracker.RecordFailure("a", TwitchJoinFailureReasons.ConcurrentChannelLimit));
        Assert.Equal(JoinFailureOutcome.Repeated,
            tracker.RecordFailure("a", TwitchJoinFailureReasons.NoConfirmation));

        Assert.Equal(TwitchJoinFailureReasons.ConcurrentChannelLimit, tracker.Snapshot(At(1)).Single().Reason);
    }

    [Fact]
    public void Suspended_NewAttempt_Timeout_ReplacesTheReason()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.RecordFailure("a", TwitchJoinFailureReasons.ChannelSuspended);

        tracker.BeginAttempt("a");
        var outcome = tracker.RecordFailure("a", TwitchJoinFailureReasons.NoConfirmation);

        Assert.Equal(JoinFailureOutcome.Changed, outcome);
        Assert.Equal(TwitchJoinFailureReasons.NoConfirmation, tracker.Snapshot(At(1)).Single().Reason);
    }

    [Fact]
    public void OldReason_StaysVisibleUntilTheNewAttemptHasAnOutcome()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.RecordFailure("a", TwitchJoinFailureReasons.Banned);

        tracker.BeginAttempt("a");

        Assert.Equal(TwitchJoinFailureReasons.Banned, tracker.Snapshot(At(1)).Single().Reason);
    }

    [Fact]
    public void Timeout_Twice_IsARepeat()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");

        Assert.Equal(JoinFailureOutcome.Changed,
            tracker.RecordFailure("a", TwitchJoinFailureReasons.NoConfirmation));
        Assert.Equal(JoinFailureOutcome.Repeated,
            tracker.RecordFailure("a", TwitchJoinFailureReasons.NoConfirmation));
    }

    [Fact]
    public void SameConcreteReason_Twice_IsARepeat_DifferentOneChanges()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));

        Assert.Equal(JoinFailureOutcome.Changed, tracker.RecordFailure("a", TwitchJoinFailureReasons.Banned));
        Assert.Equal(JoinFailureOutcome.Repeated, tracker.RecordFailure("a", TwitchJoinFailureReasons.Banned));
        Assert.Equal(JoinFailureOutcome.Changed,
            tracker.RecordFailure("a", TwitchJoinFailureReasons.ChannelSuspended));
    }

    [Fact]
    public void Failure_OnConfirmedChannel_IsIgnored()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.MarkConfirmed("a");

        Assert.Equal(JoinFailureOutcome.Ignored, tracker.RecordFailure("a", TwitchJoinFailureReasons.Banned));
        Assert.Null(tracker.Snapshot(At(1)).Single().Reason);
    }

    [Fact]
    public void Failure_OnUnknownChannel_IsIgnored_AndDoesNotInsert()
    {
        var tracker = Connected();

        Assert.Equal(JoinFailureOutcome.Ignored, tracker.RecordFailure("ghost", TwitchJoinFailureReasons.Banned));
        Assert.False(tracker.Contains("ghost"));
    }

    [Fact]
    public void Reason_SurvivesDisconnect_UntilConfirmation()
    {
        var tracker = Connected();
        tracker.RecordIntent("a", At(0));
        tracker.BeginAttempt("a");
        tracker.RecordFailure("a", TwitchJoinFailureReasons.ConcurrentChannelLimit);
        tracker.SetDisconnected(At(5));
        tracker.SetConnected(At(6));

        Assert.Equal(TwitchJoinFailureReasons.ConcurrentChannelLimit, tracker.Snapshot(At(7)).Single().Reason);
    }

    // ---- concurrency ----

    [Fact]
    public async Task ParallelIntentsAgainstConnectionFlips_LoseNoTransition_AndNeverLeaveAnIdleClock()
    {
        var tracker = new ChannelJoinTracker();
        const int channels = 400;

        var intents = Task.Run(() => Parallel.For(0, channels, i => tracker.RecordIntent($"c{i}", T0)));
        var flips = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                tracker.SetConnected(T0);
                tracker.SetDisconnected(T0);
            }
        });
        await Task.WhenAll(intents, flips);

        Assert.Equal(channels, tracker.Names().Count);

        // Connection is up at the end: every channel must be accruing, none may be stuck without a clock.
        tracker.SetConnected(T0);
        Assert.All(tracker.Snapshot(At(10)), s => Assert.Equal(10, s.UnconfirmedSeconds));
    }
}
