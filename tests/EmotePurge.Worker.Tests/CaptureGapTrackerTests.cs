using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Worker.WishCapture;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Plan #486 2.7: the coarse gap model. Pure, no clock: every call names its time.
public sealed class CaptureGapTrackerTests
{
    private static readonly DateTime Stamp = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly WishCaptureGapChannel A = new("chan-a", "1001", "alpha");
    private static readonly WishCaptureGapChannel B = new("chan-b", "1002", "bravo");
    private static readonly IReadOnlyList<WishCaptureGapChannel> Both = [A, B];

    [Fact]
    public void Start_WithAStampAndAnEmptyRoster_OpensARestartGapForEveryChannelOfTheSet()
    {
        var tracker = new CaptureGapTracker();

        tracker.Start(Stamp, Both);
        var closed = tracker.Sample(Both, [], Stamp.AddMinutes(10));

        Assert.Empty(closed);
        Assert.Equal(2, tracker.OpenGaps.Count);
        Assert.All(tracker.OpenGaps.Values, gap =>
        {
            Assert.Equal(Stamp, gap.FromUtc);
            Assert.Equal(WishCaptureGapReason.WorkerRestart, gap.Reason);
        });
    }

    [Fact]
    public void Start_WithoutAStamp_OpensNothing()
    {
        var tracker = new CaptureGapTracker();

        tracker.Start(null, Both);

        Assert.Empty(tracker.OpenGaps);
    }

    [Fact]
    public void ARestartGap_ReachesFromTheStampToEachChannelsOwnConfirmedJoin()
    {
        var tracker = new CaptureGapTracker();
        tracker.Start(Stamp, Both);

        var first = tracker.Sample(Both, [Joined("alpha"), Pending("bravo")], Stamp.AddMinutes(3));
        var second = tracker.Sample(Both, [Joined("alpha"), Joined("bravo")], Stamp.AddMinutes(7));

        var a = Assert.Single(first);
        Assert.Equal(new WishCaptureGapRecord("chan-a", Stamp, Stamp.AddMinutes(3), WishCaptureGapReason.WorkerRestart), a);
        var b = Assert.Single(second);
        Assert.Equal(new WishCaptureGapRecord("chan-b", Stamp, Stamp.AddMinutes(7), WishCaptureGapReason.WorkerRestart), b);
        Assert.Empty(tracker.OpenGaps);
    }

    [Fact]
    public void AHandshakeWithJoinsStillOpen_ClosesNothing()
    {
        var tracker = StartedWithoutGaps();
        var lost = Stamp.AddMinutes(1);

        tracker.ConnectionLost(lost);
        tracker.RejoinRoundCompleted();
        var closed = tracker.Sample(Both, [Pending("alpha"), Pending("bravo")], lost.AddMinutes(3));

        Assert.Empty(closed);
        Assert.Equal(2, tracker.OpenGaps.Count);
        Assert.All(tracker.OpenGaps.Values, gap => Assert.Equal(WishCaptureGapReason.Disconnect, gap.Reason));
    }

    // Sol-R2-P2-6: between the signal and the client swap the roster still carries the old client's confirmations.
    [Fact]
    public void WhileAConnectionGapIsOpen_ASampleClosesNothing_EvenWithConfirmedEntries()
    {
        var tracker = StartedWithoutGaps();
        var lost = Stamp.AddMinutes(1);

        tracker.ConnectionLost(lost);
        var closed = tracker.Sample(Both, [Joined("alpha"), Joined("bravo")], lost.AddMinutes(5));

        Assert.Empty(closed);
        Assert.True(tracker.IsConnectionGapOpen);
        Assert.Equal(2, tracker.OpenGaps.Count);
    }

    // The last frame lies four minutes back (Twitch's server PING), but the gap begins at the signal: 20 seconds.
    [Fact]
    public void AShortDeliberateReconnect_LeavesNoRow()
    {
        var tracker = StartedWithoutGaps();
        var signal = Stamp.AddMinutes(30);

        tracker.ConnectionLost(signal);
        tracker.RejoinRoundCompleted();
        var closed = tracker.Sample(Both, [Joined("alpha"), Joined("bravo")], signal.AddSeconds(20));

        Assert.Empty(closed);
        Assert.Empty(tracker.OpenGaps);
    }

    [Fact]
    public void AFiveMinuteDisconnect_GivesOneRowPerChannelFromTheSignal()
    {
        var tracker = StartedWithoutGaps();
        var signal = Stamp.AddMinutes(30);

        tracker.ConnectionLost(signal);
        tracker.RejoinRoundCompleted();
        var closed = tracker.Sample(Both, [Joined("alpha"), Joined("bravo")], signal.AddMinutes(5));

        Assert.Equal(2, closed.Count);
        Assert.All(closed, gap =>
        {
            Assert.Equal(signal, gap.FromUtc);
            Assert.Equal(signal.AddMinutes(5), gap.ToUtc);
            Assert.Equal(WishCaptureGapReason.Disconnect, gap.Reason);
        });
    }

    [Fact]
    public void ADisconnectDuringAnOpenRestartGap_KeepsOneGapWithTheFirstReason()
    {
        var tracker = new CaptureGapTracker();
        tracker.Start(Stamp, [A]);

        tracker.ConnectionLost(Stamp.AddMinutes(2));
        tracker.RejoinRoundCompleted();
        var closed = tracker.Sample([A], [Joined("alpha")], Stamp.AddMinutes(4));

        var gap = Assert.Single(closed);
        Assert.Equal(new WishCaptureGapRecord("chan-a", Stamp, Stamp.AddMinutes(4), WishCaptureGapReason.WorkerRestart), gap);
    }

    [Fact]
    public void AChannelStillOpenAfterTheRejoinRound_KeepsItsGap_WithoutASecondNotJoined()
    {
        var tracker = StartedWithoutGaps();
        var signal = Stamp.AddMinutes(30);

        tracker.ConnectionLost(signal);
        tracker.RejoinRoundCompleted();
        tracker.Sample(Both, [Joined("alpha"), Pending("bravo")], signal.AddMinutes(2));
        tracker.Sample(Both, [Joined("alpha"), Pending("bravo")], signal.AddMinutes(3));
        var closed = tracker.Sample(Both, [Joined("alpha"), Joined("bravo")], signal.AddMinutes(9));

        var gap = Assert.Single(closed);
        Assert.Equal(new WishCaptureGapRecord("chan-b", signal, signal.AddMinutes(9), WishCaptureGapReason.Disconnect), gap);
    }

    [Fact]
    public void AnUnconfirmedChannelWithoutAGap_OpensNotJoinedFromNow_AndClosesAtItsJoin()
    {
        var tracker = StartedWithoutGaps();
        var now = Stamp.AddMinutes(5);

        Assert.Empty(tracker.Sample(Both, [Joined("alpha")], now));
        var closed = tracker.Sample(Both, [Joined("alpha"), Joined("bravo")], now.AddMinutes(2));

        var gap = Assert.Single(closed);
        Assert.Equal(new WishCaptureGapRecord("chan-b", now, now.AddMinutes(2), WishCaptureGapReason.NotJoined), gap);
    }

    [Fact]
    public void AChannelThatLeavesTheSet_DropsItsOpenGap()
    {
        var tracker = new CaptureGapTracker();
        tracker.Start(Stamp, Both);

        tracker.Sample([A], [], Stamp.AddMinutes(2));
        var closed = tracker.Sample(Both, [Joined("alpha"), Joined("bravo")], Stamp.AddMinutes(10));

        // bravo came back into the set after its gap was dropped: no row from the stamp, and a new not_joined only
        // where the join was missing (it is confirmed now, so none).
        var gap = Assert.Single(closed);
        Assert.Equal("chan-a", gap.ChannelId);
        Assert.Empty(tracker.OpenGaps);
    }

    [Fact]
    public void TwoRebuildsInARow_KeepOneBeginning()
    {
        var tracker = StartedWithoutGaps();
        var first = Stamp.AddMinutes(30);

        tracker.ConnectionLost(first);
        tracker.RejoinRoundCompleted();
        tracker.ConnectionLost(first.AddMinutes(1));
        tracker.RejoinRoundCompleted();
        var closed = tracker.Sample([A], [Joined("alpha")], first.AddMinutes(4));

        var gap = Assert.Single(closed);
        Assert.Equal(first, gap.FromUtc);
    }

    [Fact]
    public void AConnectionLossBeforeTheStart_OnlyMarksTheConnectionGap()
    {
        var tracker = new CaptureGapTracker();

        tracker.ConnectionLost(Stamp);

        Assert.True(tracker.IsConnectionGapOpen);
        Assert.Empty(tracker.OpenGaps);
    }

    [Fact]
    public void TheRosterIsMatchedByChannelName_IgnoringCase()
    {
        var tracker = new CaptureGapTracker();
        tracker.Start(Stamp, [A]);

        var closed = tracker.Sample([A], [Joined("ALPHA")], Stamp.AddMinutes(2));

        Assert.Single(closed);
    }

    [Fact]
    public async Task TheEndOfARejoinRound_RequestsOneSample_AndTheAcknowledgementRearmsIt()
    {
        var tracker = new CaptureGapTracker();
        var before = tracker.SampleRequested;
        Assert.False(before.IsCompleted);

        tracker.ConnectionLost(Stamp);
        Assert.False(tracker.SampleRequested.IsCompleted);
        tracker.RejoinRoundCompleted();
        await before.WaitAsync(TimeSpan.FromSeconds(5));

        tracker.AcknowledgeSampleRequest();
        Assert.False(tracker.SampleRequested.IsCompleted);
        Assert.False(tracker.IsConnectionGapOpen);
    }

    private static CaptureGapTracker StartedWithoutGaps()
    {
        var tracker = new CaptureGapTracker();
        tracker.Start(null, Both);
        Assert.Empty(tracker.Sample(Both, [Joined("alpha"), Joined("bravo")], Stamp));
        return tracker;
    }

    private static TwitchRosterEntry Joined(string channel) => new(channel, true, null);

    private static TwitchRosterEntry Pending(string channel) => new(channel, false, null);
}
