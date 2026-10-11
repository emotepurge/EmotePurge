using EmotePurge.Core.Services;
using NSubstitute;
using Xunit;

namespace EmotePurge.Worker.Tests;

public class WorkerSystemLoadWorkloadSourceTests
{
    private static readonly DateTime PollAtUtc = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ReadsActiveAndLastPolledLiveChannelsWithSeparateIntervalCounters()
    {
        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["alpha", "beta"]));
        var liveReader = Substitute.For<ITwitchLiveStatusReader>();
        liveReader.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TwitchLiveStatusSnapshot?>(new TwitchLiveStatusSnapshot(PollAtUtc, ["alpha"])));
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
            new WorkerSystemLoadCounterBaselines());

        var first = await source.ReadAsync();
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
        liveReader.ReadAsync(Arg.Any<CancellationToken>())
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
}
