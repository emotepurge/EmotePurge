namespace EmotePurge.Core.Entities;

/// <summary>
/// A channel-owned label a moderator attaches to emotes (#201). The tag lives with the channel, not
/// with an emote set: membership is keyed by 7TV emote id (<see cref="EmoteTagEntry"/>), so it
/// survives an emote leaving and re-entering the set. No inverse collection on
/// <see cref="Channel"/> — nothing navigates from a channel to its tags, every reader queries the
/// table by <see cref="ChannelId"/> (same as <see cref="ChannelEmoteSetObservation"/>).
/// </summary>
public class EmoteTag
{
    public long Id { get; set; }
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>The name as the moderator typed it (trimmed), for display.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="EmoteTagName.Normalize"/> of <see cref="Name"/>; unique per channel, so "Funny" and
    /// "funny" are the same tag.
    /// </summary>
    public string NormalizedName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public Channel Channel { get; set; } = null!;
}

/// <summary>
/// Owns the tag-name rules, in the spirit of <see cref="ChannelName"/>: everything that writes or
/// compares a tag name goes through here.
/// </summary>
public static class EmoteTagName
{
    public const int MaxLength = 40;

    public static string Normalize(string value) => value.Trim().ToLowerInvariant();

    /// <summary>
    /// A name is valid when, trimmed, it is non-empty, at most <see cref="MaxLength"/> characters and
    /// free of control characters.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length is > 0 and <= MaxLength && !trimmed.Any(char.IsControl);
    }
}

/// <summary>
/// Product limits of the tag feature. Constants rather than configuration: they bound what the UI
/// can sensibly show, they are not operating parameters.
/// </summary>
public static class EmoteTagLimits
{
    public const int MaxTagsPerChannel = 50;
    public const int MaxEntriesPerTag = 1000;

    /// <summary>
    /// Raw ids (before de-duplication) one add/remove request may carry: twice the entry limit, so a
    /// request that could still fit a full tag is never refused for its size, while an unbounded body
    /// never reaches the database.
    /// </summary>
    public const int MaxIdsPerRequest = 2 * MaxEntriesPerTag;
}
