using System.Collections.Concurrent;
using EmotePurge.Core.Services;
using EmotePurge.Worker.WishCapture;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Join-failure detection end to end (plan #516, T3): the manager runs on TwitchLib's real client, parser
// and join queue over a scripted transport (FakeIrcTransport), so a limit notice travels the same path
// as on the wire — OnMessage, OnSendReceiveData, the NOTICE branch — down to the stored reason and the
// log event. The accrual clock uses a fake clock; TwitchLib's join timer is real, which is why every
// case that needs a timeout waits about 6-7 seconds (its timer drops an entry older than 5s on a 1s tick,
// and there is no seam to shorten it).
public class TwitchChatManagerJoinFailureTransportTests
{
    private const string ChannelA = "probe_a";
    private const string ChannelB = "probe_b";

    // Recorded 2026-09-08 (join probe); the channel name is replaced per test.
    private static string LimitLine(string channel) =>
        $"@msg-id=msg_concurrent_channel_limit_reached :tmi.twitch.tv NOTICE #{channel} :You are connected to too many chat channels. Please close some existing chat connections to join a new chat.";

    private static string BannedLine(string channel) =>
        $"@msg-id=msg_banned :tmi.twitch.tv NOTICE #{channel} :You are permanently banned from talking in {channel}.";

    private static string SuspendedLine(string channel) =>
        $"@msg-id=msg_channel_suspended :tmi.twitch.tv NOTICE #{channel} :This channel has been suspended.";

    [Fact]
    public async Task LimitNotice_IsStoredAsConcurrentChannelLimit_LogsOneErrorEvent_AndSurvivesTheTimeout()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);

        await h.Transport.ReceiveAsync(LimitLine(ChannelA));

        Assert.Equal(TwitchJoinFailureReasons.ConcurrentChannelLimit, h.Reason(ChannelA));
        var error = Assert.Single(h.Logs, l => l.Level == LogLevel.Error);
        Assert.Equal(TwitchChatManager.JoinRejectedEvent, error.EventId);
        Assert.Contains(ChannelA, error.Message);
        Assert.Contains(TwitchJoinFailureReasons.ConcurrentChannelLimit, error.Message);

        // The same attempt then times out (TwitchLib never answers a limit notice with a callback):
        // the concrete reason stays and nothing loud is added.
        await h.WaitUntilAsync(() => h.TimerFired(ChannelA));
        Assert.Equal(TwitchJoinFailureReasons.ConcurrentChannelLimit, h.Reason(ChannelA));
        Assert.Single(h.Events, l => l.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task JoinWithoutAnyAnswer_IsStoredAsNoConfirmation_AndLogsOneWarningEvent()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);

        await h.WaitUntilAsync(() => h.Reason(ChannelA) is not null);

        Assert.Equal(TwitchJoinFailureReasons.NoConfirmation, h.Reason(ChannelA));
        var warning = Assert.Single(h.Events);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(TwitchChatManager.JoinUnconfirmedEvent, warning.EventId);
    }

    [Fact]
    public async Task SuspendedNotice_IsStoredAsChannelSuspended_FromTheRawNotice_AndEndsTheAttempt()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);

        await h.Transport.ReceiveAsync(SuspendedLine(ChannelA));

        Assert.Equal(TwitchJoinFailureReasons.ChannelSuspended, h.Reason(ChannelA));
        Assert.False(h.Manager.Tracker.IsAttemptOpen(ChannelA));
        var warning = Assert.Single(h.Events);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(TwitchChatManager.JoinRejectedEvent, warning.EventId);

        // TwitchLib drops the suspended attempt from its join timer, so no timeout follows: the channel
        // stays suspended (and excluded from the alarm) until a new JOIN has an outcome of its own.
        await Task.Delay(TimeSpan.FromSeconds(7));
        Assert.Equal(TwitchJoinFailureReasons.ChannelSuspended, h.Reason(ChannelA));
        Assert.Single(h.Events);
    }

    [Fact]
    public async Task ASuspension_ThenTheQueuedRetryBeforeTheCallback_ThatGetsNoReply_EndsAsNoConfirmation()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelB); // on the wire, holds TwitchLib's queue until its 366
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.Manager.JoinChannelAsync(ChannelA); // queued twice: TwitchLib checks joined channels, not its queue
        Assert.Equal(0, h.Transport.JoinsSentFor(ChannelA));

        await h.Transport.ReceiveAsync(h.NamesEnd(ChannelB));
        await h.WaitUntilAsync(() => h.Transport.JoinsSentFor(ChannelA) == 1, TimeSpan.FromSeconds(3));

        // TwitchLib answers the suspension by sending the queued retry first and raising its callback
        // after it — both inside this one receive.
        await h.Transport.ReceiveAsync(SuspendedLine(ChannelA));
        Assert.Equal(2, h.Transport.JoinsSentFor(ChannelA));
        Assert.True(h.Manager.Tracker.IsAttemptOpen(ChannelA));
        Assert.Equal(TwitchJoinFailureReasons.ChannelSuspended, h.Reason(ChannelA));

        // The retry stays silent: its own timeout replaces the suspension, so an overdue channel alarms.
        await h.WaitUntilAsync(() => h.Reason(ChannelA) == TwitchJoinFailureReasons.NoConfirmation);
        h.Clock.Advance(TimeSpan.FromSeconds(421));
        var entry = h.Entry(ChannelA);
        Assert.False(entry.JoinConfirmed);
        Assert.Equal(421, entry.UnconfirmedSeconds);
        Assert.Equal(TwitchJoinFailureReasons.NoConfirmation, entry.JoinFailureReason);
        Assert.Equal(
            [TwitchChatManager.JoinRejectedEvent, TwitchChatManager.JoinUnconfirmedEvent],
            h.Events.Where(l => l.Message.Contains(ChannelA)).Select(l => l.EventId));
    }

    [Fact]
    public async Task TheFailureCallbackWithDetails_BooksNothing()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);

        await h.Manager.OnFailureToReceiveJoinConfirmation(
            h.Manager.CurrentClient, 0, new TwitchLib.Client.Events.OnFailureToReceiveJoinConfirmationArgs(
                new TwitchLib.Client.Exceptions.FailureToReceiveJoinConfirmationException(ChannelA, "This channel has been suspended.")));

        Assert.Null(h.Reason(ChannelA));
        Assert.True(h.Manager.Tracker.IsAttemptOpen(ChannelA));
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task OwnJoinEcho_Confirms_ResetsTheClock_AndClearsTheReason()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.Transport.ReceiveAsync(LimitLine(ChannelA));
        h.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(20, h.Entry(ChannelA).UnconfirmedSeconds);

        await h.Transport.ReceiveAsync(h.JoinEcho(ChannelA));

        var entry = h.Entry(ChannelA);
        Assert.True(entry.JoinConfirmed);
        Assert.Null(entry.UnconfirmedSeconds);
        Assert.Null(entry.JoinFailureReason);
    }

    [Fact]
    public async Task Welcome366_AdvancesTheJoinQueue_ButConfirmsNothing()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.Manager.JoinChannelAsync(ChannelB);
        Assert.Equal(0, h.Transport.JoinsSentFor(ChannelB));

        await h.Transport.ReceiveAsync(h.NamesEnd(ChannelA));

        await h.WaitUntilAsync(() => h.Transport.JoinsSentFor(ChannelB) == 1, TimeSpan.FromSeconds(3));
        Assert.False(h.Entry(ChannelA).JoinConfirmed);
    }

    [Fact]
    public async Task WithoutA366AndWithoutAnAnswer_TheNextJoinWaitsForTheTimeout()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.Manager.JoinChannelAsync(ChannelB);
        var started = DateTime.UtcNow;

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(0, h.Transport.JoinsSentFor(ChannelB));

        await h.WaitUntilAsync(() => h.Transport.JoinsSentFor(ChannelB) == 1);
        Assert.True(DateTime.UtcNow - started > TimeSpan.FromSeconds(4.5));
        Assert.Equal(TwitchJoinFailureReasons.NoConfirmation, h.Reason(ChannelA));
    }

    [Fact]
    public async Task ASilentChannel_AndAChannelWithTheLimitNotice_EachKeepTheirOwnReason()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.Manager.JoinChannelAsync(ChannelB);

        // A is silent; its timeout releases B's JOIN, which Twitch answers with the limit notice.
        await h.WaitUntilAsync(() => h.Transport.JoinsSentFor(ChannelB) == 1);
        await h.Transport.ReceiveAsync(LimitLine(ChannelB));

        Assert.Equal(TwitchJoinFailureReasons.NoConfirmation, h.Reason(ChannelA));
        Assert.Equal(TwitchJoinFailureReasons.ConcurrentChannelLimit, h.Reason(ChannelB));
    }

    [Fact]
    public async Task TheSilentJoinSeam_SwallowsTheJoinOfAListedChannel_YetTheAttemptCountsAsOpen()
    {
        await using var h = await Harness.StartAsync(silentJoinChannels: $"{ChannelA.ToUpperInvariant()}, other");
        Assert.Contains(h.Logs, l => l.Level == LogLevel.Warning && l.Message.Contains("SilentJoinChannels"));

        await h.Manager.JoinChannelAsync(ChannelA);
        await h.Manager.JoinChannelAsync(ChannelB);

        Assert.Equal(0, h.Transport.JoinsSentFor(ChannelA));
        Assert.True(h.Manager.Tracker.IsAttemptOpen(ChannelA));

        await h.WaitUntilAsync(() => h.Reason(ChannelA) is not null);
        Assert.Equal(TwitchJoinFailureReasons.NoConfirmation, h.Reason(ChannelA));
        Assert.Equal(1, h.Logs.Count(l => l.EventId == TwitchChatManager.JoinUnconfirmedEvent));

        // An unlisted channel goes out unchanged.
        await h.WaitUntilAsync(() => h.Transport.JoinsSentFor(ChannelB) == 1);
    }

    [Fact]
    public async Task NoSilentChannelsConfigured_NothingIsSwallowedAndNoWarningIsLogged()
    {
        await using var h = await Harness.StartAsync();

        await h.Manager.JoinChannelAsync(ChannelA);

        Assert.Equal(1, h.Transport.JoinsSentFor(ChannelA));
        Assert.DoesNotContain(h.Logs, l => l.Message.Contains("SilentJoinChannels"));
    }

    [Fact]
    public async Task DisconnectAndNewHandshake_FreezeTheClock_AndStartAConfirmedChannelAtZero()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.Transport.ReceiveAsync(h.JoinEcho(ChannelA));
        await h.Transport.ReceiveAsync(h.NamesEnd(ChannelA));
        await h.Manager.JoinChannelAsync(ChannelB); // goes out once the queue advanced, stays unanswered
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(30, h.Entry(ChannelB).UnconfirmedSeconds);

        await h.Transport.DropAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(100));

        Assert.Equal(30, h.Entry(ChannelB).UnconfirmedSeconds);
        var former = h.Entry(ChannelA);
        Assert.False(former.JoinConfirmed);
        Assert.Equal(0, former.UnconfirmedSeconds);

        await h.Transport.ReceiveAsync(":tmi.twitch.tv 004 justinfan1 :-");
        h.Clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(40, h.Entry(ChannelB).UnconfirmedSeconds);
        Assert.Equal(10, h.Entry(ChannelA).UnconfirmedSeconds);
    }

    [Fact]
    public async Task ADuplicateJoinAfterABanNotice_NeverReachesTheWire_AndTheTimeoutKeepsTheBan()
    {
        await using var h = await Harness.StartAsync();
        var stamp = h.Manager.CaptureLeaveStamp();
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.Transport.ReceiveAsync(BannedLine(ChannelA));
        Assert.Equal(TwitchJoinFailureReasons.Banned, h.Reason(ChannelA));

        // TwitchLib still lists the channel as joined (a ban entry removes nothing), so both calls are
        // dropped by it and the tracker never sees a second attempt.
        await h.Manager.EnsureJoinedAsync(ChannelA, stamp);
        await h.Manager.JoinChannelAsync(ChannelA);
        Assert.Equal(1, h.Transport.JoinsSentFor(ChannelA));

        await h.WaitUntilAsync(() => h.TimerFired(ChannelA));
        Assert.Equal(TwitchJoinFailureReasons.Banned, h.Reason(ChannelA));
        var warning = Assert.Single(h.Events);
        Assert.Equal(TwitchChatManager.JoinRejectedEvent, warning.EventId);
    }

    [Fact]
    public async Task ADelayedOldJoinEcho_ThenItsPartEcho_AndASilentNewJoin_LeaveTheChannelUnconfirmedWithItsClockRunning()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA); // attempt 1; its echo is late, TwitchLib gives up on it

        await h.WaitUntilAsync(() => h.Reason(ChannelA) is not null);
        await h.Manager.LeaveChannelAsync(ChannelA);
        await h.Manager.JoinChannelAsync(ChannelA); // re-activation: attempt 2, sent on TwitchLib's next tick
        await h.WaitUntilAsync(() => h.Transport.JoinsSentFor(ChannelA) == 2, TimeSpan.FromSeconds(5));

        // Twitch answers in send order: the old JOIN's echo (and its names list) lands inside the new
        // attempt, the PART echo follows.
        await h.Transport.ReceiveAsync(h.JoinEcho(ChannelA));
        await h.Transport.ReceiveAsync(h.NamesEnd(ChannelA));
        Assert.True(h.Entry(ChannelA).JoinConfirmed);
        await h.Transport.ReceiveAsync(h.PartEcho(ChannelA));

        var afterPart = h.Entry(ChannelA);
        Assert.False(afterPart.JoinConfirmed);
        Assert.Equal(0, afterPart.UnconfirmedSeconds);

        // The convergence net joins again; this JOIN stays silent.
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        await h.Manager.EnsureJoinedAsync(ChannelA, h.Manager.CaptureLeaveStamp());
        Assert.Equal(3, h.Transport.JoinsSentFor(ChannelA));
        await h.WaitUntilAsync(() => h.Reason(ChannelA) is not null);

        var entry = h.Entry(ChannelA);
        Assert.Equal(TwitchJoinFailureReasons.NoConfirmation, entry.JoinFailureReason);
        Assert.Equal(60, entry.UnconfirmedSeconds);
        h.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(75, h.Entry(ChannelA).UnconfirmedSeconds);
    }

    // Codex review round 2: leave and re-activation while the first JOIN is still awaited, so our PART
    // and the new JOIN are both on the wire before Twitch answers the first one, late. Twitch answers in
    // send order: the old JOIN echo, our PART echo, the new JOIN echo. The old echo belongs to the
    // membership the PART ends and must not consume the new attempt; the new echo confirms.
    [Fact]
    public async Task ALateOldJoinEcho_OurPartEcho_AndTheNewJoinEcho_ConfirmTheNewAttempt()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA); // attempt 1, still awaited by TwitchLib
        await h.Manager.LeaveChannelAsync(ChannelA);
        Assert.Equal(1, h.Transport.PartsSentFor(ChannelA));
        await h.Manager.JoinChannelAsync(ChannelA); // queued behind attempt 1, sent after its timeout
        await h.WaitUntilAsync(() => h.Transport.JoinsSentFor(ChannelA) == 2);
        Assert.True(h.Manager.Tracker.IsAttemptOpen(ChannelA));

        await h.Transport.ReceiveAsync(h.JoinEcho(ChannelA));
        await h.Transport.ReceiveAsync(h.NamesEnd(ChannelA));
        Assert.False(h.Entry(ChannelA).JoinConfirmed);
        Assert.True(h.Manager.Tracker.IsAttemptOpen(ChannelA));

        await h.Transport.ReceiveAsync(h.PartEcho(ChannelA));
        Assert.False(h.Entry(ChannelA).JoinConfirmed);
        Assert.True(h.Manager.Tracker.IsAttemptOpen(ChannelA));

        h.Clock.Advance(TimeSpan.FromSeconds(3));
        await h.Transport.ReceiveAsync(h.JoinEcho(ChannelA));
        await h.Transport.ReceiveAsync(h.NamesEnd(ChannelA));

        var entry = h.Entry(ChannelA);
        Assert.True(entry.JoinConfirmed);
        Assert.Null(entry.UnconfirmedSeconds);
        Assert.Null(entry.JoinFailureReason);
    }

    [Fact]
    public async Task AMissingPartEcho_BlocksJoinEchoesOnlyForTheGrace_ThenTheNextJoinEchoConfirms()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.Manager.LeaveChannelAsync(ChannelA);
        Assert.Equal(1, h.Transport.PartsSentFor(ChannelA)); // Twitch never echoes this PART
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.WaitUntilAsync(() => h.Transport.JoinsSentFor(ChannelA) == 2);

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        await h.Transport.ReceiveAsync(h.JoinEcho(ChannelA));
        await h.Transport.ReceiveAsync(h.NamesEnd(ChannelA));
        Assert.False(h.Entry(ChannelA).JoinConfirmed); // inside the grace: still old membership

        h.Clock.Advance(TimeSpan.FromSeconds(6));
        await h.Transport.ReceiveAsync(h.JoinEcho(ChannelA));
        await h.Transport.ReceiveAsync(h.NamesEnd(ChannelA));
        Assert.True(h.Entry(ChannelA).JoinConfirmed);

        // Codex review round 3, P1: the confirmation dropped the expired PART, so Twitch parting us
        // later withdraws it instead of being taken for that PART's echo.
        await h.Transport.ReceiveAsync(h.PartEcho(ChannelA));
        Assert.False(h.Entry(ChannelA).JoinConfirmed);
    }

    // No further echo is coming once the grace has passed (TwitchLib lists the channel as joined and
    // drops a plain JOIN), so the convergence net asks the watchdog for a rebuild — once — and the fresh
    // client's JOIN confirms.
    [Fact]
    public async Task AnExpiredMissingPartEcho_RequestsOneRebuild_AndTheRebuiltClientsJoinConfirms()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.Manager.LeaveChannelAsync(ChannelA);
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.WaitUntilAsync(() => h.Transport.JoinsSentFor(ChannelA) == 2);
        await h.Transport.ReceiveAsync(h.JoinEcho(ChannelA)); // ignored: our PART looks pending
        Assert.False(h.Entry(ChannelA).JoinConfirmed);

        h.Clock.Advance(TimeSpan.FromSeconds(60));
        await h.Manager.EnsureJoinedAsync(ChannelA, h.Manager.CaptureLeaveStamp());
        await h.Manager.EnsureJoinedAsync(ChannelA, h.Manager.CaptureLeaveStamp());

        Assert.Equal(1, h.Transport.PartsSentFor(ChannelA)); // no hand-built PART any more
        var warning = Assert.Single(h.Logs, l => l.EventId == TwitchChatManager.PartEchoMissingEvent);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.DoesNotContain(ChannelA, warning.Message);
        using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var request = await h.Manager.WaitForReconnectRequestAsync(wait.Token);
            Assert.Equal(TwitchSessionEndReason.PartEchoMissing, request.Reason);
        }

        Assert.Single(h.Logs, l => l.Message.StartsWith("Verlust-Signal abgelegt", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Logs, l => l.Message.StartsWith("Weiteres Verlust-Signal", StringComparison.Ordinal));

        // The watchdog's rebuild: a fresh client on a fresh transport. Its disconnect cleared the
        // pending PART, so the new JOIN echo confirms and a later PART from Twitch is Twitch's own.
        var outcome = await h.Manager.ReconnectOnceAsync(CancellationToken.None);
        Assert.True(outcome.HandshakeCompleted);
        await h.Manager.EnsureJoinedAsync(ChannelA, h.Manager.CaptureLeaveStamp());
        Assert.Equal(1, h.Transport.JoinsSentFor(ChannelA));
        await h.Transport.ReceiveAsync(h.JoinEcho(ChannelA));
        Assert.True(h.Entry(ChannelA).JoinConfirmed);

        await h.Transport.ReceiveAsync(h.PartEcho(ChannelA));
        Assert.False(h.Entry(ChannelA).JoinConfirmed);
        Assert.Single(h.Logs, l => l.EventId == TwitchChatManager.PartEchoMissingEvent);
    }

    [Fact]
    public async Task ATwitchInitiatedPart_OfAConfirmedChannel_WithdrawsTheConfirmation_AndTheNextEnsureSendsAgain()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.Transport.ReceiveAsync(h.JoinEcho(ChannelA));
        await h.Transport.ReceiveAsync(h.NamesEnd(ChannelA));
        Assert.True(h.Entry(ChannelA).JoinConfirmed);

        await h.Transport.ReceiveAsync(h.PartEcho(ChannelA));

        Assert.False(h.Entry(ChannelA).JoinConfirmed);
        await h.Manager.EnsureJoinedAsync(ChannelA, h.Manager.CaptureLeaveStamp());
        Assert.Equal(2, h.Transport.JoinsSentFor(ChannelA));
    }

    [Fact]
    public async Task AFailureOfAReplacedClient_BooksNothing()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);
        var stale = new TwitchLib.Client.TwitchClient();

        await h.Manager.OnFailureToReceiveJoinConfirmation(
            stale, 0, new TwitchLib.Client.Events.OnFailureToReceiveJoinConfirmationArgs(
                new TwitchLib.Client.Exceptions.FailureToReceiveJoinConfirmationException(ChannelA)));

        Assert.Null(h.Reason(ChannelA));
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task ALeaveBetweenTheNoticeAndTheBooking_BooksNothing()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.Manager.LeaveChannelAsync(ChannelA);

        await h.Transport.ReceiveAsync(LimitLine(ChannelA));

        Assert.Empty(h.Manager.GetRoster());
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task ARepeatedFailure_LogsOnlyAtDebug()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);

        await h.Transport.ReceiveAsync(LimitLine(ChannelA));
        await h.Transport.ReceiveAsync(LimitLine(ChannelA));

        Assert.Single(h.Events);
        Assert.Contains(h.Logs, l => l.Level == LogLevel.Debug && l.Message.Contains("repeated"));
    }

    // Codex review round 4, P2: the first JOIN was never answered, the leave's PART is never echoed,
    // and the channel is wanted again only after the grace. No JOIN echo was held back, so TwitchLib
    // does not list the channel: the convergence net's plain JOIN goes out and confirms, and nothing
    // tears the healthy connection down.
    [Fact]
    public async Task AnExpiredPartThatHeldNoEchoBack_LetsTheNormalJoinConfirm_WithoutARebuild()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA); // never answered
        await h.Manager.LeaveChannelAsync(ChannelA);
        Assert.Equal(1, h.Transport.PartsSentFor(ChannelA)); // never echoed

        h.Clock.Advance(TimeSpan.FromSeconds(60));
        await h.Manager.EnsureJoinedAsync(ChannelA, h.Manager.CaptureLeaveStamp());
        await h.WaitUntilAsync(() => h.Transport.JoinsSentFor(ChannelA) == 2);
        await h.Transport.ReceiveAsync(h.JoinEcho(ChannelA));
        await h.Transport.ReceiveAsync(h.NamesEnd(ChannelA));

        Assert.True(h.Entry(ChannelA).JoinConfirmed);
        Assert.DoesNotContain(h.Logs, l => l.EventId == TwitchChatManager.PartEchoMissingEvent);
        Assert.DoesNotContain(h.Logs, l => l.Message.Contains("Verlust-Signal", StringComparison.Ordinal));
        using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Manager.WaitForReconnectRequestAsync(wait.Token));
    }

    // Codex review round 4, P2: the request belongs to the client whose PART held the echo back. Here
    // the watchdog replaces that client after the marker is taken and before the request is offered
    // (driven from the Warning that sits exactly between the two); the request must be discarded, not
    // stamped with the successor's generation and tear the fresh connection down.
    [Fact]
    public async Task ARebuildRequestForAReplacedClient_IsDiscarded_AndDoesNotHitTheSuccessor()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.Manager.LeaveChannelAsync(ChannelA);
        await h.Manager.JoinChannelAsync(ChannelA);
        await h.WaitUntilAsync(() => h.Transport.JoinsSentFor(ChannelA) == 2);
        await h.Transport.ReceiveAsync(h.JoinEcho(ChannelA)); // held back: our PART looks pending
        h.Clock.Advance(TimeSpan.FromSeconds(60));

        Task<TwitchConnectOutcome>? rebuild = null;
        h.OnLog = line =>
        {
            if (line.EventId == TwitchChatManager.PartEchoMissingEvent)
            {
                // ReconnectOnceAsync swaps the client synchronously before its first await.
                rebuild = h.Manager.ReconnectOnceAsync(CancellationToken.None);
            }
        };
        await h.Manager.EnsureJoinedAsync(ChannelA, h.Manager.CaptureLeaveStamp());
        h.OnLog = null;

        Assert.NotNull(rebuild);
        Assert.True((await rebuild).HandshakeCompleted);
        Assert.Single(h.Logs, l => l.EventId == TwitchChatManager.PartEchoMissingEvent);
        var discarded = Assert.Single(h.Logs, l => l.Message.StartsWith("Verlust-Signal verworfen", StringComparison.Ordinal));
        Assert.Contains("Client #1", discarded.Message);
        Assert.DoesNotContain(h.Logs, l => l.Message.StartsWith("Verlust-Signal abgelegt", StringComparison.Ordinal));
        using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Manager.WaitForReconnectRequestAsync(wait.Token));
    }

    private sealed record LogLine(LogLevel Level, EventId EventId, string Message);

    private sealed class CollectingLogger : ILogger<TwitchChatManager>
    {
        private readonly ConcurrentQueue<LogLine> _lines = new();

        public IReadOnlyCollection<LogLine> Lines => _lines;

        // Runs synchronously inside the logging call, on the logging thread.
        public Action<LogLine>? OnLog { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = new LogLine(logLevel, eventId, formatter(state, exception));
            _lines.Enqueue(line);
            OnLog?.Invoke(line);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CollectingLogger _logger = new();
        private readonly CancellationTokenSource _cts = new();

        private Harness(string? silentJoinChannels)
        {
            Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
            // A fresh transport per client, as in production: a rebuild (ReconnectOnceAsync) gets its
            // own, and Transport always names the current client's.
            Transport = null!;
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:Debug:SilentJoinChannels"] = silentJoinChannels })
                .Build();
            Manager = new TwitchChatManager(
                _logger,
                NullLoggerFactory.Instance,
                Substitute.For<IEmoteMatchCache>(),
                Substitute.For<IEmoteUsageCounter>(),
                Substitute.For<IBotChatterDetector>(),
                Substitute.For<IExcludedChatterFilter>(),
                new WorkerStats(),
                new WishOptions(),
                Substitute.For<IRewardObservationSink>(),
                Clock,
                () => Transport = new FakeIrcTransport(),
                configuration);
        }

        public FakeTimeProvider Clock { get; }

        public FakeIrcTransport Transport { get; private set; }

        public TwitchChatManager Manager { get; }

        public IReadOnlyCollection<LogLine> Logs => _logger.Lines;

        public Action<LogLine>? OnLog
        {
            get => _logger.OnLog;
            set => _logger.OnLog = value;
        }

        // The lines that carry a join-failure event id (the ones the log viewer counts).
        public IReadOnlyList<LogLine> Events => _logger.Lines.Where(l => l.EventId.Name is not null).ToList();

        public string User => Manager.CurrentClient.TwitchUsername;

        public static async Task<Harness> StartAsync(string? silentJoinChannels = null)
        {
            var harness = new Harness(silentJoinChannels);
            harness.Manager.Initialize();
            await harness.Manager.ConnectAsync(harness._cts.Token);
            Assert.True(harness.Manager.IsConnected);
            return harness;
        }

        public string JoinEcho(string channel) => $":{User}!{User}@{User}.tmi.twitch.tv JOIN #{channel}";

        public string NamesEnd(string channel) => $":tmi.twitch.tv 366 {User} #{channel} :End of /NAMES list";

        public string PartEcho(string channel) => $":{User}!{User}@{User}.tmi.twitch.tv PART #{channel}";

        public TwitchRosterEntry Entry(string channel) =>
            Manager.GetRoster().Single(e => string.Equals(e.ChannelName, channel, StringComparison.OrdinalIgnoreCase));

        public string? Reason(string channel) =>
            Manager.GetRoster().SingleOrDefault(e => string.Equals(e.ChannelName, channel, StringComparison.OrdinalIgnoreCase))
                .JoinFailureReason;

        // TwitchLib's join timer has fired for the channel once its own "listed as joined" entry is gone,
        // which a second JOIN would then no longer be dropped for; observable as a reason or, after a
        // concrete reason, as the debug line of the repeated failure.
        public bool TimerFired(string channel) =>
            Logs.Any(l => l.Level == LogLevel.Debug && l.Message.Contains("repeated") && l.Message.Contains(channel));

        public async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
                await Task.Delay(100);
            }
        }

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
