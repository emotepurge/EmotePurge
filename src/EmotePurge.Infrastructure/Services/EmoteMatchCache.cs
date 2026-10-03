using System.Collections.Concurrent;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

public class EmoteMatchCache : IEmoteMatchCache
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _byChannel = new();

    public void ReplaceChannel(string channelName, IReadOnlyDictionary<string, string> emoteNameToId)
        => _byChannel[ChannelName.Normalize(channelName)] = emoteNameToId;

    public void RemoveChannel(string channelName)
        => _byChannel.TryRemove(ChannelName.Normalize(channelName), out _);

    public IReadOnlyDictionary<string, string> GetChannelEmotes(string channelName)
        => _byChannel.TryGetValue(ChannelName.Normalize(channelName), out var emotes) ? emotes : Empty;

    public IReadOnlyCollection<string> GetCachedChannelNames() => [.. _byChannel.Keys];
}
