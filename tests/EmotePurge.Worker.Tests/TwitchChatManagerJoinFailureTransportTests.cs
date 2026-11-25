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
    public async Task SuspendedNotice_IsStoredAsChannelSuspended_ThroughTheCallbackDetails()
    {
        await using var h = await Harness.StartAsync();
        await h.Manager.JoinChannelAsync(ChannelA);

        await h.Transport.ReceiveAsync(SuspendedLine(ChannelA));

        Assert.Equal(TwitchJoinFailureReasons.ChannelSuspended, h.Reason(ChannelA));
        var warning = Assert.Single(h.Events);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(TwitchChatManager.JoinRejectedEvent, warning.EventId);
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

    private sealed record LogLine(LogLevel Level, EventId EventId, string Message);

    private sealed class CollectingLogger : ILogger<TwitchChatManager>
    {
        private readonly ConcurrentQueue<LogLine> _lines = new();

        public IReadOnlyCollection<LogLine> Lines => _lines;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue(new LogLine(logLevel, eventId, formatter(state, exception)));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CollectingLogger _logger = new();
        private readonly CancellationTokenSource _cts = new();

        private Harness(string? silentJoinChannels)
        {
            Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
            Transport = new FakeIrcTransport();
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
                () => Transport,
                configuration);
        }

        public FakeTimeProvider Clock { get; }

        public FakeIrcTransport Transport { get; }

        public TwitchChatManager Manager { get; }

        public IReadOnlyCollection<LogLine> Logs => _logger.Lines;

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
