using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

public sealed class WorkerSystemLoadWorkloadSource(
    IChannelService channelService,
    ITwitchLiveStatusReader liveStatusReader,
    WorkerStats workerStats,
    IEmoteUsageCounter usageCounter) : ISystemLoadWorkloadSource
{
    private long _lastChatLines;
    private long _lastEmoteMatches;

    public async Task<SystemLoadWorkloadSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var activeChannels = await channelService.ListActiveChannelNamesAsync(cancellationToken);
        var liveStatus = await liveStatusReader.ReadAsync(cancellationToken);
        int? liveChannels = liveStatus is null ? null : liveStatus.LiveChannelLogins.Count;
        var chatLines = Difference(workerStats.TotalChatLines, ref _lastChatLines);
        var emoteMatches = Difference(usageCounter.TotalMatchedEmotes, ref _lastEmoteMatches);

        return new SystemLoadWorkloadSnapshot(activeChannels.Count, liveChannels, chatLines, emoteMatches);
    }

    private static long Difference(long current, ref long previous)
    {
        var prior = Interlocked.Exchange(ref previous, current);
        return current >= prior ? current - prior : current;
    }
}
