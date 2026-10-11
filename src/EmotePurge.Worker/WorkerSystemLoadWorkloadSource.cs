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
        var chatLinesTotal = workerStats.TotalChatLines;
        var emoteMatchesTotal = usageCounter.TotalMatchedEmotes;
        var chatLines = counterBaselines.GetChatLineDelta(chatLinesTotal);
        var emoteMatches = counterBaselines.GetEmoteMatchDelta(emoteMatchesTotal);

        return new SystemLoadWorkloadSnapshot(
            activeChannels.Count,
            liveChannels,
            chatLines,
            emoteMatches,
            chatLinesTotal,
            emoteMatchesTotal);
    }

    public void ConfirmRecorded(SystemLoadWorkloadSnapshot workload)
    {
        counterBaselines.Confirm(workload.ChatLinesTotal, workload.EmoteMatchesTotal);
    }
}

/// <summary>
/// Holds only process-wide totals from the last successfully recorded workload interval. The source
/// remains scoped because it also depends on scoped channel services backed by AppDbContext.
/// </summary>
public sealed class WorkerSystemLoadCounterBaselines
{
    private long _lastChatLines;
    private long _lastEmoteMatches;

    public long GetChatLineDelta(long current) => Difference(current, Interlocked.Read(ref _lastChatLines));

    public long GetEmoteMatchDelta(long current) => Difference(current, Interlocked.Read(ref _lastEmoteMatches));

    public void Confirm(long? chatLinesTotal, long? emoteMatchesTotal)
    {
        if (chatLinesTotal.HasValue)
        {
            Interlocked.Exchange(ref _lastChatLines, chatLinesTotal.Value);
        }

        if (emoteMatchesTotal.HasValue)
        {
            Interlocked.Exchange(ref _lastEmoteMatches, emoteMatchesTotal.Value);
        }
    }

    private static long Difference(long current, long previous) => current >= previous ? current - previous : current;
}
