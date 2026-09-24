using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Worker.SevenTv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The prune step's decision is RosterPrunePolicy's and tested there; this covers what the hosted
// service does around it that the policy cannot see: the channels it leaves, and what it logs while
// doing so. Container-free, like WorkerBootSequenceTests — everything external is a substitute.
public class SevenTvPeriodicResyncWorkerTests
{
    // Fourth Codex review of the block list: since the active roster leaves out rows whose Twitch id
    // is on the excluded-channel list, the prune is also how a blocked channel whose LEAVE got lost is
    // parted — and its log line used to name that channel.
    [Fact]
    public async Task Prune_LeavesAChannelMissingFromTwoConsecutiveRosters_AndNamesItOnlyAtDebug()
    {
        var gate = new BootRecoveryGate();
        gate.MarkCompleted();
        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>()).Returns(new List<string>());
        var chatManager = Substitute.For<ITwitchChatManager>();
        chatManager.GetRoster().Returns([new TwitchRosterEntry("prunedchannel", true, null)]);
        var left = new TaskCompletionSource();
        chatManager.When(x => x.LeaveChannelAsync("prunedchannel")).Do(_ => left.TrySetResult());
        var emoteMatchCache = Substitute.For<IEmoteMatchCache>();
        var eventClient = Substitute.For<ISevenTvEventClient>();

        var services = new ServiceCollection();
        services.AddSingleton(channelService);
        services.AddSingleton(Substitute.For<ISevenTvSyncService>());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var provider = new CapturingLoggerProvider();
        using var loggerFactory = new LoggerFactory();
        loggerFactory.AddProvider(provider);

        var worker = new SevenTvPeriodicResyncWorker(
            new Logger<SevenTvPeriodicResyncWorker>(loggerFactory),
            chatManager,
            gate,
            eventClient,
            emoteMatchCache,
            Substitute.For<IRedisPublisher>(),
            // The shortest interval the setting allows: the prune needs two ticks.
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SevenTv:ResyncIntervalSeconds"] = "1"
            }).Build(),
            scopeFactory);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await left.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        emoteMatchCache.Received(1).RemoveChannel("prunedchannel");
        eventClient.Received(1).Unsubscribe("prunedchannel");
        Assert.Contains(provider.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Convergence net"));
        Assert.DoesNotContain(
            provider.Entries, e => e.Level > LogLevel.Debug && e.Message.Contains("prunedchannel", StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Add(LogEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
