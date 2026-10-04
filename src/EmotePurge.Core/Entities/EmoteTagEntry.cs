namespace EmotePurge.Core.Entities;

/// <summary>
/// One emote carrying one <see cref="EmoteTag"/>. Keyed by the 7TV emote id, deliberately without a
/// foreign key to <see cref="Emote"/>: <c>Emote.Id</c> is an internal guid and the emote row is
/// archived or recreated as the set changes (rule 8), while a tag must outlive that — an emote that
/// leaves the set and comes back keeps its tags.
/// </summary>
public class EmoteTagEntry
{
    public long TagId { get; set; }

    /// <summary>
    /// The 7TV emote id (a 26-character ULID in practice; the column allows 32). Not a foreign key to
    /// <see cref="Emote"/> — see the type comment.
    /// </summary>
    public string SevenTvEmoteId { get; set; } = string.Empty;

    /// <summary>The emote's name when it was tagged, kept so the tag page can show emotes that are not in the set (any more).</summary>
    public string Alias { get; set; } = string.Empty;

    public string ImageUrl { get; set; } = string.Empty;
    public DateTime AddedAtUtc { get; set; }

    public EmoteTag Tag { get; set; } = null!;
}
