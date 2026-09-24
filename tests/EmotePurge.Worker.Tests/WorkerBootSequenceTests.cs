using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Worker.SevenTv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using WorkerService = EmotePurge.Worker.Worker;

namespace EmotePurge.Worker.Tests;

// The boot ordering half of issue #54, asserted at the two real call sites rather than on the gate
// alone. The gate's own test only proves that two signals are independent; it cannot show that
// Worker arms the second one at the right moment, nor that the one worker which publishes
// commands actually waits for it — and that pair *is* the bug: Redis Pub/Sub silently discards a
// message published to a channel nobody is subscribed to.
//
// Container-free on purpose: everything that touches Redis or Postgres here is a transport, and
// what is under test is the order in which this process reaches them.
public class WorkerBootSequenceTests
{
    [Fact]
    public async Task Worker_ArmsTheCommandChannelSignalOnlyAfterBootRecoveryAndTheSubscribe()
    {
        var gate = new BootRecoveryGate();

        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<string> { "handofblood" });

        var chatManager = Substitute.For<ITwitchChatManager>();
        var joins = 0;
        chatManager.When(x => x.JoinChannelAsync(Arg.Any<string>())).Do(_ => joins++);

        // Everything the subscribe moment is supposed to look like, captured inside the subscribe
        // itself — after the fact these are indistinguishable from each other.
        var signalArmedDuringSubscribe = true;
        var bootRecoveryDoneDuringSubscribe = false;
        var joinsBeforeSubscribe = 0;
        var subscriber = Substitute.For<IRedisSubscriber>();
        subscriber.When(x => x.SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<string, string, Task>>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                signalArmedDuringSubscribe = gate.CommandChannelSubscribed.IsCompleted;
                bootRecoveryDoneDuringSubscribe = gate.Completed.IsCompleted;
                joinsBeforeSubscribe = joins;
            });

        var worker = new WorkerService(
            NullLogger<WorkerService>.Instance,
            chatManager,
            subscriber,
            Substitute.For<IRedisPublisher>(),
            Substitute.For<IEmoteMatchCache>(),
            gate,
            Substitute.For<ISevenTvEventClient>(),
            CreateScopeFactory(channelService),
            new ConfigurationBuilder().Build());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await gate.CommandChannelSubscribed.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.False(signalArmedDuringSubscribe);
        Assert.True(bootRecoveryDoneDuringSubscribe);
        Assert.Equal(1, joinsBeforeSubscribe);
        await subscriber.Received(1).SubscribeAsync(
            BotCommands.Channel, Arg.Any<Func<string, string, Task>>(), Arg.Any<CancellationToken>());
    }

    // Task 6 (Entscheidung 7.5): the cheap, allowed case for the debug trigger's gate — the
    // trigger itself is transport (Regel 11/16) and gets no test beyond this. This one only
    // proves the dispatcher honours Worker:Debug:AllowTwitchReconnectTrigger before ever reaching
    // TwitchChatManager.SimulateServerReconnectAsync; the injected path is verified live (Task 9,
    // S2), not here.
    [Fact]
    public async Task Worker_IgnoresTheDebugReconnectCommandWithoutTheConfigGate()
    {
        var gate = new BootRecoveryGate();
        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        var chatManager = Substitute.For<ITwitchChatManager>();
        Func<string, string, Task>? capturedHandler = null;
        var subscriber = Substitute.For<IRedisSubscriber>();
        subscriber.When(x => x.SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<string, string, Task>>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => capturedHandler = callInfo.Arg<Func<string, string, Task>>());

        // No Worker:Debug:AllowTwitchReconnectTrigger key at all — the same as the default in
        // production, where it is never set in docker-compose.yml or .env.example.
        var worker = new WorkerService(
            NullLogger<WorkerService>.Instance,
            chatManager,
            subscriber,
            Substitute.For<IRedisPublisher>(),
            Substitute.For<IEmoteMatchCache>(),
            gate,
            Substitute.For<ISevenTvEventClient>(),
            CreateScopeFactory(channelService),
            new ConfigurationBuilder().Build());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await gate.CommandChannelSubscribed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(capturedHandler);
            await capturedHandler!(BotCommands.Channel, "DEBUG:TWITCH-RECONNECT");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await chatManager.DidNotReceive().SimulateServerReconnectAsync();
    }

    // Fourth Codex review of the block list: JOIN and RESYNC commands used to be followed blindly, so
    // an Api still running with an older exclusion list — or an admin RESYNC of a row the identity
    // reconcile had not deactivated yet — made this worker join a channel its own roster source
    // (IChannelService.ListActiveChannelNamesAsync, which leaves out rows with an excluded stored id)
    // no longer lists. Only the listed channel may be entered, by either command.
    [Theory]
    [InlineData("JOIN:")]
    [InlineData("RESYNC:")]
    public async Task Worker_EntersOnlyAChannelOnTheActiveRoster_ForJoinAndResyncCommands(string prefix)
    {
        var gate = new BootRecoveryGate();
        var channelService = Substitute.For<IChannelService>();
        // Empty during boot recovery, so every JoinChannelAsync/EnsureJoinedAsync below comes from
        // the command under test.
        var roster = new List<string>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>()).Returns(_ => roster);
        var syncService = Substitute.For<ISevenTvSyncService>();

        var chatManager = Substitute.For<ITwitchChatManager>();
        Func<string, string, Task>? capturedHandler = null;
        var subscriber = Substitute.For<IRedisSubscriber>();
        subscriber.When(x => x.SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<string, string, Task>>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => capturedHandler = callInfo.Arg<Func<string, string, Task>>());

        var worker = new WorkerService(
            NullLogger<WorkerService>.Instance,
            chatManager,
            subscriber,
            Substitute.For<IRedisPublisher>(),
            Substitute.For<IEmoteMatchCache>(),
            gate,
            Substitute.For<ISevenTvEventClient>(),
            CreateScopeFactory(channelService, syncService),
            new ConfigurationBuilder().Build());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await gate.CommandChannelSubscribed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(capturedHandler);
            roster.Add("listedchannel");

            await capturedHandler!(BotCommands.Channel, prefix + "unlistedchannel");
            await capturedHandler(BotCommands.Channel, prefix + "listedchannel");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await chatManager.DidNotReceive().JoinChannelAsync("unlistedchannel");
        await chatManager.DidNotReceive().EnsureJoinedAsync("unlistedchannel");
        await syncService.DidNotReceive().SyncChannelAsync("unlistedchannel", Arg.Any<CancellationToken>());
        await syncService.Received(1).SyncChannelAsync("listedchannel", Arg.Any<CancellationToken>());
        if (prefix == "JOIN:")
        {
            await chatManager.Received(1).JoinChannelAsync("listedchannel");
        }
        else
        {
            await chatManager.Received(1).EnsureJoinedAsync("listedchannel");
        }
    }

    // Fourth Codex review of the block list: the identity reconcile publishes a LEAVE for a channel
    // it deactivates because its Twitch id is on the excluded-channel list, and the handler's line
    // used to name that channel right after the reconcile's own anonymous one.
    [Fact]
    public async Task Worker_NamesTheChannelOfALeaveCommandOnlyAtDebug()
    {
        var gate = new BootRecoveryGate();
        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>()).Returns(new List<string>());
        var chatManager = Substitute.For<ITwitchChatManager>();
        Func<string, string, Task>? capturedHandler = null;
        var subscriber = Substitute.For<IRedisSubscriber>();
        subscriber.When(x => x.SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<string, string, Task>>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => capturedHandler = callInfo.Arg<Func<string, string, Task>>());
        var logger = new RecordingLogger<WorkerService>();

        var worker = new WorkerService(
            logger,
            chatManager,
            subscriber,
            Substitute.For<IRedisPublisher>(),
            Substitute.For<IEmoteMatchCache>(),
            gate,
            Substitute.For<ISevenTvEventClient>(),
            CreateScopeFactory(channelService),
            new ConfigurationBuilder().Build());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await gate.CommandChannelSubscribed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(capturedHandler);
            await capturedHandler!(BotCommands.Channel, "LEAVE:leftchannel_test");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await chatManager.Received(1).LeaveChannelAsync("leftchannel_test");
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("leaving a channel"));
        Assert.DoesNotContain(
            logger.Entries, e => e.Level > LogLevel.Debug && e.Message.Contains("leftchannel_test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwitchIdentityReconcileWorker_DoesNotRunItsFirstPassOnBootRecoveryAlone()
    {
        var gate = new BootRecoveryGate();
        var reconciled = new TaskCompletionSource();
        var identityService = Substitute.For<IChannelIdentityService>();
        identityService.ReconcileActiveChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                reconciled.TrySetResult();
                // null = tick skipped; the worker's own behaviour after the pass is not under test.
                return (ChannelIdentityReconcileSummary?)null;
            });

        var worker = new TwitchIdentityReconcileWorker(
            NullLogger<TwitchIdentityReconcileWorker>.Instance,
            gate,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Twitch:ClientId"] = "client-id",
                ["Auth:Twitch:ClientSecret"] = "client-secret"
            }).Build(),
            CreateScopeFactory(identityService));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // This is the state the old code released on: boot recovery over, command channel still
            // deaf. A pass here would publish its LEAVE/JOIN handover into the void.
            gate.MarkCompleted();
            await Task.Delay(250);
            Assert.False(reconciled.Task.IsCompleted);

            gate.MarkCommandChannelSubscribed();
            await reconciled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static IServiceScopeFactory CreateScopeFactory(IChannelService channelService, ISevenTvSyncService? syncService = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(channelService);
        // Returns null for every channel, so SyncSevenTvAsync stops right after the call.
        services.AddSingleton(syncService ?? Substitute.For<ISevenTvSyncService>());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static IServiceScopeFactory CreateScopeFactory(IChannelIdentityService identityService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(identityService);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
