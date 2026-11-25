using EmotePurge.Core.Services;
using EmotePurge.Worker.WishCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Communication.Interfaces;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The manager's switch to ChannelJoinTracker (plan #516, T2). No socket is opened: the handlers the
// TwitchLib events would call (handshake, JOIN/PART echo, outgoing wire line) are driven directly, with
// a fake clock, so every transition of the accrual clock and the attempt window is deterministic.
// Handshake and failure handling against a faked transport come with T3.
public class TwitchChatManagerJoinStateTests
{
    private const string Channel = "olaf_olaf_son";
    private const string Bot = "justinfan1";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Join_OnANeverConnectedManager_KeepsTheClockAtZero()
    {
        var manager = CreateManager();

        await manager.JoinChannelAsync(Channel);
        _clock.Advance(TimeSpan.FromMinutes(10));

        var entry = Assert.Single(manager.GetRoster());
        Assert.False(entry.JoinConfirmed);
        Assert.Equal(0, entry.UnconfirmedSeconds);
        Assert.Null(entry.JoinFailureReason);
    }

    [Fact]
    public async Task Leave_RemovesTheIntent_AndAStaleEnsureJoinedDoesNotRecreateIt()
    {
        var manager = CreateManager();
        await manager.JoinChannelAsync(Channel);
        var stamp = manager.CaptureLeaveStamp();

        await manager.LeaveChannelAsync(Channel);
        await manager.EnsureJoinedAsync(Channel, stamp);

        Assert.Empty(manager.GetRoster());
    }

    [Fact]
    public async Task Clock_RunsOnlyWhileConnected_FreezesOnDisconnect_AndResumesOnTheNextHandshake()
    {
        var manager = CreateManager();
        await manager.JoinChannelAsync(Channel);
        var client = manager.CurrentClient;

        _clock.Advance(TimeSpan.FromSeconds(100)); // disconnected: does not count
        await manager.OnConnected(client, 1);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(30, Seconds(manager));

        await manager.OnDisconnected(client, 1);
        _clock.Advance(TimeSpan.FromSeconds(500)); // frozen
        Assert.Equal(30, Seconds(manager));

        await manager.OnConnected(client, 1);
        _clock.Advance(TimeSpan.FromSeconds(12));
        Assert.Equal(42, Seconds(manager));
    }

    [Fact]
    public async Task Join_AfterTheHandshake_StartsItsClockImmediately()
    {
        var manager = CreateManager();
        await manager.OnConnected(manager.CurrentClient, 1);

        await manager.JoinChannelAsync(Channel);
        _clock.Advance(TimeSpan.FromSeconds(7));

        Assert.Equal(7, Seconds(manager));
    }

    [Fact]
    public async Task JoinEcho_WithoutAnAttemptOnTheWire_DoesNotConfirm()
    {
        var manager = CreateManager();
        await manager.JoinChannelAsync(Channel);

        await manager.OnJoinedChannel(manager.CurrentClient, 1, new OnJoinedChannelArgs(Channel, Bot));

        Assert.False(Assert.Single(manager.GetRoster()).JoinConfirmed);
    }

    [Fact]
    public async Task JoinEcho_AfterAJoinOnTheWire_ConfirmsAndResetsTheClock()
    {
        var manager = await ConnectedManagerWantingAsync(Channel);
        _clock.Advance(TimeSpan.FromSeconds(20));

        SendJoin(manager, Channel);
        await manager.OnJoinedChannel(manager.CurrentClient, 1, new OnJoinedChannelArgs(Channel, Bot));

        var entry = Assert.Single(manager.GetRoster());
        Assert.True(entry.JoinConfirmed);
        Assert.Null(entry.UnconfirmedSeconds);
    }

    [Fact]
    public async Task JoinEcho_ForAChannelNoLongerWanted_DoesNotResurrectIt()
    {
        var manager = await ConnectedManagerWantingAsync(Channel);
        SendJoin(manager, Channel);
        await manager.LeaveChannelAsync(Channel);

        await manager.OnJoinedChannel(manager.CurrentClient, 1, new OnJoinedChannelArgs(Channel, Bot));

        Assert.Empty(manager.GetRoster());
    }

    [Fact]
    public async Task Signals_OfAReplacedClient_BookNothing()
    {
        var manager = await ConnectedManagerWantingAsync(Channel);
        var stale = new TwitchClient();

        manager.OnOutgoingChannelCommand(stale, 0, OutgoingChannelCommand.Join, Channel);
        await manager.OnJoinedChannel(stale, 0, new OnJoinedChannelArgs(Channel, Bot));
        Assert.False(Assert.Single(manager.GetRoster()).JoinConfirmed);

        SendJoin(manager, Channel);
        await manager.OnJoinedChannel(manager.CurrentClient, 1, new OnJoinedChannelArgs(Channel, Bot));
        await manager.OnLeftChannel(stale, 0, new OnLeftChannelArgs(Channel, Bot));
        Assert.True(Assert.Single(manager.GetRoster()).JoinConfirmed);
    }

    [Fact]
    public async Task PartEcho_ForAConfirmedWantedChannel_WithdrawsTheConfirmation_AndTheNextEnsureRetries()
    {
        var manager = await ConnectedManagerWantingAsync(Channel);
        SendJoin(manager, Channel);
        await manager.OnJoinedChannel(manager.CurrentClient, 1, new OnJoinedChannelArgs(Channel, Bot));

        await manager.OnLeftChannel(manager.CurrentClient, 1, new OnLeftChannelArgs(Channel, Bot));

        var entry = Assert.Single(manager.GetRoster());
        Assert.False(entry.JoinConfirmed);
        Assert.Equal(0, entry.UnconfirmedSeconds);
        _clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(9, Seconds(manager));
    }

    [Fact]
    public async Task PartEcho_AfterOurOwnLeave_DoesNotRecreateTheChannel()
    {
        var manager = await ConnectedManagerWantingAsync(Channel);
        SendJoin(manager, Channel);
        await manager.OnJoinedChannel(manager.CurrentClient, 1, new OnJoinedChannelArgs(Channel, Bot));
        await manager.LeaveChannelAsync(Channel);

        await manager.OnLeftChannel(manager.CurrentClient, 1, new OnLeftChannelArgs(Channel, Bot));

        Assert.Empty(manager.GetRoster());
    }

    [Fact]
    public async Task LateJoinEcho_AfterLeaveAndReactivation_DoesNotConfirmTheNewWish()
    {
        var manager = await ConnectedManagerWantingAsync(Channel);
        SendJoin(manager, Channel);
        await manager.LeaveChannelAsync(Channel);
        await manager.JoinChannelAsync(Channel); // re-wanted, no JOIN on the wire yet

        await manager.OnJoinedChannel(manager.CurrentClient, 1, new OnJoinedChannelArgs(Channel, Bot));

        Assert.False(Assert.Single(manager.GetRoster()).JoinConfirmed);
    }

    [Fact]
    public async Task Disconnect_ClosesTheAttempt_SoALateEchoOfTheOldClientConnectionDoesNotConfirm()
    {
        var manager = await ConnectedManagerWantingAsync(Channel);
        SendJoin(manager, Channel);
        await manager.OnDisconnected(manager.CurrentClient, 1);
        await manager.OnConnected(manager.CurrentClient, 1);

        await manager.OnJoinedChannel(manager.CurrentClient, 1, new OnJoinedChannelArgs(Channel, Bot));

        Assert.False(Assert.Single(manager.GetRoster()).JoinConfirmed);
    }

    // The doubled wire JOIN (finding R3-H2): TwitchLib enqueues a JOIN it has not sent yet without
    // looking at the queue, so the same channel can reach the wire twice. Inside the same attempt the
    // second line must not clear the concrete reason the first already earned.
    [Fact]
    public async Task DoubledWireJoin_WithinTheAttempt_KeepsTheConcreteReason()
    {
        var manager = await ConnectedManagerWantingAsync(Channel);
        SendJoin(manager, Channel);
        Assert.Equal(JoinFailureOutcome.Changed, manager.Tracker.RecordFailure(Channel, TwitchJoinFailureReasons.Banned));

        _clock.Advance(TimeSpan.FromSeconds(2));
        SendJoin(manager, Channel);

        Assert.Equal(JoinFailureOutcome.Repeated, manager.Tracker.RecordFailure(Channel, TwitchJoinFailureReasons.NoConfirmation));
        Assert.Equal(TwitchJoinFailureReasons.Banned, Assert.Single(manager.GetRoster()).JoinFailureReason);
    }

    [Fact]
    public async Task WireJoin_AfterTheAttemptWindow_IsANewAttempt_AndASilentOutcomeReplacesTheOldReason()
    {
        var manager = await ConnectedManagerWantingAsync(Channel);
        SendJoin(manager, Channel);
        manager.Tracker.RecordFailure(Channel, TwitchJoinFailureReasons.Banned);

        _clock.Advance(TimeSpan.FromSeconds(7));
        SendJoin(manager, Channel);

        Assert.Equal(JoinFailureOutcome.Changed, manager.Tracker.RecordFailure(Channel, TwitchJoinFailureReasons.NoConfirmation));
    }

    [Fact]
    public async Task WireJoin_AfterAPartOnTheWire_IsANewAttempt()
    {
        var manager = await ConnectedManagerWantingAsync(Channel);
        SendJoin(manager, Channel);
        manager.Tracker.RecordFailure(Channel, TwitchJoinFailureReasons.Banned);

        manager.OnOutgoingChannelCommand(manager.CurrentClient, 1, OutgoingChannelCommand.Part, Channel);
        SendJoin(manager, Channel);

        Assert.Equal(JoinFailureOutcome.Changed, manager.Tracker.RecordFailure(Channel, TwitchJoinFailureReasons.NoConfirmation));
    }

    [Fact]
    public async Task Initialize_AttachesTheWireHook_AndTheWiredHookReachesTheTracker()
    {
        var manager = CreateManager();
        manager.Initialize();
        await manager.OnConnected(manager.CurrentClient, 1);
        await manager.JoinChannelAsync(Channel);

        // The decorator of the boot client is reached through the weak table the manager keeps; the
        // hook it was given must book the attempt on the manager's tracker.
        var wire = WireObserverOf(manager.CurrentClient);
        wire.Hook!.Invoke(OutgoingChannelCommand.Join, Channel);

        Assert.True(manager.Tracker.IsAttemptOpen(Channel));
    }

    [Theory]
    [InlineData("JOIN #olaf_olaf_son", "Join", "olaf_olaf_son")]
    [InlineData("JOIN #OLAF_olaf_Son", "Join", "olaf_olaf_son")]
    [InlineData("PART #olaf_olaf_son", "Part", "olaf_olaf_son")]
    [InlineData("JOIN #a,#b", "Join", "a")]
    [InlineData("JOIN #a\r\n", "Join", "a")]
    public void ParseChannelCommand_RecognisesJoinAndPart(string line, string expected, string channel)
    {
        Assert.True(WireObservingClient.TryParseChannelCommand(line, out var command, out var parsed));
        Assert.Equal(expected, command.ToString());
        Assert.Equal(channel, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("JOIN ")]
    [InlineData("JOIN #")]
    [InlineData("JOIN olaf")]
    [InlineData("PRIVMSG #olaf :JOIN #other")]
    [InlineData(" JOIN #olaf")]
    [InlineData("join #olaf")]
    [InlineData("CAP REQ :twitch.tv/tags")]
    [InlineData("PONG :tmi.twitch.tv")]
    public void ParseChannelCommand_IgnoresEverythingElse(string? line)
    {
        Assert.False(WireObservingClient.TryParseChannelCommand(line, out _, out _));
    }

    [Fact]
    public async Task Decorator_ReportsBeforeForwarding_AndForwardsEveryLineUnchanged()
    {
        var order = new List<string>();
        var inner = Substitute.For<IClient>();
        inner.SendAsync(Arg.Any<string>()).Returns(call =>
        {
            order.Add("send:" + call.Arg<string>());
            return Task.FromResult(true);
        });
        var wire = new WireObservingClient(inner)
        {
            Hook = (command, channel) => order.Add($"hook:{command}:{channel}")
        };

        await wire.SendAsync("JOIN #Olaf");
        await wire.SendAsync("PRIVMSG #olaf :hi");

        Assert.Equal(["hook:Join:olaf", "send:JOIN #Olaf", "send:PRIVMSG #olaf :hi"], order);
    }

    [Fact]
    public async Task Decorator_ForwardsEvenWhenTheHookThrows_AndWithoutAHook()
    {
        var inner = Substitute.For<IClient>();
        inner.SendAsync(Arg.Any<string>()).Returns(true);
        var wire = new WireObservingClient(inner)
        {
            Hook = (_, _) => throw new InvalidOperationException("boom")
        };

        Assert.True(await wire.SendAsync("PART #olaf"));
        wire.Hook = null;
        Assert.True(await wire.SendAsync("JOIN #olaf"));

        await inner.Received(1).SendAsync("PART #olaf");
        await inner.Received(1).SendAsync("JOIN #olaf");
    }

    private static void SendJoin(TwitchChatManager manager, string channel) =>
        manager.OnOutgoingChannelCommand(manager.CurrentClient, 1, OutgoingChannelCommand.Join, channel);

    private static int Seconds(TwitchChatManager manager) =>
        Assert.Single(manager.GetRoster()).UnconfirmedSeconds!.Value;

    private async Task<TwitchChatManager> ConnectedManagerWantingAsync(string channel)
    {
        var manager = CreateManager();
        await manager.OnConnected(manager.CurrentClient, 1);
        await manager.JoinChannelAsync(channel);
        return manager;
    }

    private static WireObservingClient WireObserverOf(TwitchClient client)
    {
        var field = typeof(TwitchChatManager).GetField(
            "WireObservers",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var table = (System.Runtime.CompilerServices.ConditionalWeakTable<TwitchClient, WireObservingClient>)field.GetValue(null)!;
        Assert.True(table.TryGetValue(client, out var wire));
        return wire;
    }

    private TwitchChatManager CreateManager() => new(
        NullLogger<TwitchChatManager>.Instance,
        NullLoggerFactory.Instance,
        Substitute.For<IEmoteMatchCache>(),
        Substitute.For<IEmoteUsageCounter>(),
        Substitute.For<IBotChatterDetector>(),
        Substitute.For<IExcludedChatterFilter>(),
        new WorkerStats(),
        new WishOptions(),
        Substitute.For<IRewardObservationSink>(),
        _clock);
}
