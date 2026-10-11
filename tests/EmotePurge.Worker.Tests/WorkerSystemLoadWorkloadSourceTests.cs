using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using NSubstitute;
using StackExchange.Redis;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EmotePurge.Worker.Tests;

public class WorkerSystemLoadWorkloadSourceTests
{
    [Fact]
    public async Task ReadsActiveAndLastPolledLiveChannelsWithSeparateIntervalCounters()
    {
        var counterBaselines = new WorkerSystemLoadCounterBaselines();
        var pollAtUtc = DateTime.UtcNow;
        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["alpha", "beta"]));
        var liveReader = Substitute.For<ITwitchLiveStatusReader>();
        liveReader.ReadQuietlyAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TwitchLiveStatusSnapshot?>(new TwitchLiveStatusSnapshot(pollAtUtc, ["alpha"])));
        var stats = new WorkerStats();
        stats.RecordChatLine();
        stats.RecordChatLine();
        var usageCounter = new EmoteUsageCounter();
        usageCounter.Increment("one", "set", UsageCategory.Human);
        usageCounter.Increment("two", "set", UsageCategory.Bot);
        var source = new WorkerSystemLoadWorkloadSource(
            channelService,
            liveReader,
            stats,
            usageCounter,
            counterBaselines);

        var first = await source.ReadAsync();
        source.ConfirmRecorded(first);
        usageCounter.DrainAndReset();
        stats.RecordChatLine();
        usageCounter.Increment("three", "set", UsageCategory.SharedChat);
        var second = await source.ReadAsync();

        Assert.Equal(2, first.ActiveChannels);
        Assert.Equal(1, first.LiveChannels);
        Assert.Equal(2, first.ChatLines);
        Assert.Equal(2, first.EmoteMatches);
        Assert.Equal(1, second.ChatLines);
        Assert.Equal(1, second.EmoteMatches);
    }

    [Fact]
    public async Task MissingLivePollStaysUnknownInsteadOfReportingZero()
    {
        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));
        var liveReader = Substitute.For<ITwitchLiveStatusReader>();
        liveReader.ReadQuietlyAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TwitchLiveStatusSnapshot?>(null));
        var source = new WorkerSystemLoadWorkloadSource(
            channelService,
            liveReader,
            new WorkerStats(),
            new EmoteUsageCounter(),
            new WorkerSystemLoadCounterBaselines());

        var result = await source.ReadAsync();

        Assert.Equal(0, result.ActiveChannels);
        Assert.Null(result.LiveChannels);
    }

    [Fact]
    public async Task SnapshotFromBeforeThisProcessStartedStaysUnknown()
    {
        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));
        var liveReader = Substitute.For<ITwitchLiveStatusReader>();
        liveReader.ReadQuietlyAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TwitchLiveStatusSnapshot?>(new TwitchLiveStatusSnapshot(
                new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), ["stale"])));
        var source = new WorkerSystemLoadWorkloadSource(
            channelService,
            liveReader,
            new WorkerStats(),
            new EmoteUsageCounter(),
            new WorkerSystemLoadCounterBaselines());

        var result = await source.ReadAsync();

        Assert.Null(result.LiveChannels);
    }

    [Fact]
    public async Task RedisFailureOnWorkloadPathPropagatesWithoutLogging()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>())
            .Returns(_ => throw new RedisException("Redis is unavailable."));
        var logger = new RecordingLogger<TwitchLiveStatusStore>();
        var reader = new TwitchLiveStatusStore(multiplexer, logger);
        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));
        var source = new WorkerSystemLoadWorkloadSource(
            channelService,
            reader,
            new WorkerStats(),
            new EmoteUsageCounter(),
            new WorkerSystemLoadCounterBaselines());

        await Assert.ThrowsAsync<RedisException>(() => source.ReadAsync());

        Assert.Empty(logger.Entries);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
