using System.Collections.Concurrent;
using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Remembers, per channel and for the life of the process, how many active emotes the flag-on match
/// map last left out because they still have an unsettled seed of the active set (plan #373, E7; spec
/// 6.1 point 1). The map is rebuilt on every resync tick, so its information line is written only when
/// that number changes — the same pattern and the same lifetime as <see cref="DuplicateEmoteNameTracker"/>.
/// </summary>
public sealed class MatchMapExclusionTracker
{
    private readonly ConcurrentDictionary<string, int> _byChannel = new();

    /// <summary>
    /// Records <paramref name="excludedEmotes"/> for the channel and returns whether it differs from the
    /// last recorded value. A first value of 0 is no change (a channel with nothing left out says nothing),
    /// and 0 is stored as absence so channels that settled do not linger.
    /// </summary>
    public bool Update(string channelName, int excludedEmotes)
    {
        var key = ChannelName.Normalize(channelName);
        if (excludedEmotes == 0)
        {
            return _byChannel.TryRemove(key, out _);
        }

        // Get-then-set instead of an atomic upsert: syncs of the same channel are serialized by
        // ChannelSyncGate, so no two updates for the same key race each other.
        if (_byChannel.TryGetValue(key, out var previous) && previous == excludedEmotes)
        {
            return false;
        }

        _byChannel[key] = excludedEmotes;
        return true;
    }
}
