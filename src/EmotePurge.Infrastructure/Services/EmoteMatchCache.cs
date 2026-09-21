using System.Collections.Concurrent;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

public class EmoteMatchCache : IEmoteMatchCache
{
    // EmoteSetId == "" marks this as the empty snapshot (see the doc comment on
    // EmoteMatchSnapshot) — GeneratedAtUtc is otherwise meaningless for it, so it is pinned to
    // the epoch rather than "now" to make it obviously never a real generation timestamp.
    private static readonly EmoteMatchSnapshot Empty = new(new Dictionary<string, string>(), string.Empty, DateTimeOffset.UnixEpoch);

    // The whole snapshot — dictionary, set id and generation — is replaced as one entry so a
    // concurrent reader never observes one swap's dictionary paired with another swap's set id.
    private readonly ConcurrentDictionary<string, EmoteMatchSnapshot> _byChannel = new();

    public void ReplaceChannel(string channelName, string emoteSetId, IReadOnlyDictionary<string, string> nameToEmoteId)
        => _byChannel[ChannelName.Normalize(channelName)] = new EmoteMatchSnapshot(nameToEmoteId, emoteSetId, DateTimeOffset.UtcNow);

    public void RemoveChannel(string channelName)
        => _byChannel.TryRemove(ChannelName.Normalize(channelName), out _);

    public EmoteMatchSnapshot GetChannelSnapshot(string channelName)
        => _byChannel.TryGetValue(ChannelName.Normalize(channelName), out var snapshot) ? snapshot : Empty;
}
