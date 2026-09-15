namespace EmotePurge.Core.Services;

/// <summary>One of the emotes involved in a name collision.</summary>
public record DuplicateEmoteDto(string EmoteId, string SevenTvEmoteId, string ImageUrl);

/// <summary>
/// An active emote name carried by more than one emote in the channel's current 7TV set.
/// Chat matching is exact (case-sensitive, like Twitch chat), so only exact-name collisions
/// appear here — casing variants are distinct chat tokens and match independently. While a
/// collision exists, all chat usage of the name is counted onto a single one of the emotes.
/// </summary>
public record DuplicateEmoteNameDto(string Name, IReadOnlyList<DuplicateEmoteDto> Emotes);
