using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

/// <summary>
/// The ordering decision of boot recovery, separated from Redis, Postgres and TwitchLib: channels
/// that were live when the previous process went down are joined first, because chat messages are
/// only lost in rooms where somebody is chatting.
/// <para>
/// Pure and stable: live channels keep their relative order, and so do the others. Without a
/// snapshot the input order is returned unchanged, so a cold Redis, an expired key or an outage
/// degrades to the previous behaviour instead of blocking or failing the boot.
/// </para>
/// </summary>
public static class BootRecoveryOrderPolicy
{
    /// <param name="activeChannels">The active roster, in the order the roster source returns it.</param>
    /// <param name="liveStatus">
    /// The live-status snapshot, or <c>null</c> when it is absent or unreadable. Its age is not
    /// checked here: the key's TTL (twice the poll interval, <c>TwitchLiveStatusKeys.TimeToLiveFor</c>)
    /// already turns a snapshot from a worker that stayed down into <c>null</c>, and a snapshot a few
    /// minutes old is still a better hint than the alphabetical order.
    /// </param>
    public static IReadOnlyList<string> LiveFirst(
        IReadOnlyList<string> activeChannels,
        TwitchLiveStatusSnapshot? liveStatus)
    {
        if (liveStatus is null || liveStatus.LiveChannelLogins.Count == 0 || activeChannels.Count == 0)
        {
            return activeChannels;
        }

        var live = liveStatus.LiveChannelLogins
            .Where(login => !string.IsNullOrWhiteSpace(login))
            .Select(ChannelName.Normalize)
            .ToHashSet(StringComparer.Ordinal);

        var ordered = new List<string>(activeChannels.Count);
        ordered.AddRange(activeChannels.Where(name => live.Contains(ChannelName.Normalize(name))));
        ordered.AddRange(activeChannels.Where(name => !live.Contains(ChannelName.Normalize(name))));
        return ordered;
    }

    /// <summary>
    /// The order of the sync phase: channels whose match cache is still empty after the join phase
    /// first (they count nothing until their sync), everything else behind them. Stable within both
    /// groups, so the join-phase order carries over.
    /// </summary>
    public static IReadOnlyList<string> ColdFirst(IReadOnlyList<string> channels, IReadOnlySet<string> coldChannels)
    {
        if (coldChannels.Count == 0 || channels.Count == 0)
        {
            return channels;
        }

        var ordered = new List<string>(channels.Count);
        ordered.AddRange(channels.Where(coldChannels.Contains));
        ordered.AddRange(channels.Where(name => !coldChannels.Contains(name)));
        return ordered;
    }
}
