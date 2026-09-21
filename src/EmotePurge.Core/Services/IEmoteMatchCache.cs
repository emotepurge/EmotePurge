namespace EmotePurge.Core.Services;

/// <summary>
/// One channel's chat-matching dictionary together with the 7TV set it was built for and when.
/// <see cref="EmoteSetId"/> is <c>""</c> only for the empty snapshot of a channel that has never
/// been populated — the chat path treats that exactly like <c>Count == 0</c> today and bails out
/// (spec 2026-09-20, section 5). A real, populated snapshot always carries a non-empty set id.
/// Reading name-to-id map, set id and generation as one object (rather than through two separate
/// calls) is deliberate: two calls could observe a cache swap in between and pair one channel's
/// dictionary with another swap's set id.
/// </summary>
public readonly record struct EmoteMatchSnapshot(
    IReadOnlyDictionary<string, string> NameToEmoteId,
    string EmoteSetId,
    DateTimeOffset GeneratedAtUtc);

public interface IEmoteMatchCache
{
    void ReplaceChannel(string channelName, string emoteSetId, IReadOnlyDictionary<string, string> nameToEmoteId);

    void RemoveChannel(string channelName);

    EmoteMatchSnapshot GetChannelSnapshot(string channelName);
}
