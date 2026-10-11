using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

public sealed class WorkerSystemLoadWorkloadSource(
    IChannelService channelService,
    ITwitchLiveStatusReader liveStatusReader,
    WorkerStats workerStats,
    IEmoteUsageCounter usageCounter,
    WorkerSystemLoadCounterBaselines counterBaselines) : ISystemLoadWorkloadSource
{
    public async Task<SystemLoadWorkloadSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var activeChannels = await channelService.ListActiveChannelNamesAsync(cancellationToken);
        var liveStatus = await liveStatusReader.ReadAsync(cancellationToken);
        int? liveChannels = liveStatus is null ? null : liveStatus.LiveChannelLogins.Count;
        var chatLines = counterBaselines.ReadChatLineDelta(workerStats.TotalChatLines);
        var emoteMatches = counterBaselines.ReadEmoteMatchDelta(usageCounter.TotalMatchedEmotes);

        return new SystemLoadWorkloadSnapshot(activeChannels.Count, liveChannels, chatLines, emoteMatches);
    }
}

/// <summary>
/// Holds only the process-wide previous values needed to calculate workload intervals. The source
/// remains scoped because it also depends on scoped channel services backed by AppDbContext.
/// </summary>
public sealed class WorkerSystemLoadCounterBaselines
{
    private long _lastChatLines;
    private long _lastEmoteMatches;

    public long ReadChatLineDelta(long current) => Difference(current, ref _lastChatLines);

    public long ReadEmoteMatchDelta(long current) => Difference(current, ref _lastEmoteMatches);

    private static long Difference(long current, ref long previous)
    {
        var prior = Interlocked.Exchange(ref previous, current);
        return current >= prior ? current - prior : current;
    }
}
