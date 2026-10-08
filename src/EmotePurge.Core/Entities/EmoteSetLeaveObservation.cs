namespace EmotePurge.Core.Entities;

/// <summary>
/// "Last time we credibly observed that emote X left set S of this channel." A channel-level fact,
/// not a tag table: the sync cannot know which tag report is still on its way, so it writes the
/// observation for every credible leave whether or not the channel has tags, and the row falls with
/// the channel's purge rather than with a tag. One row per <c>(channel, emote, set)</c>, overwritten by
/// each upsert, so the table is bounded by emotes times visited sets.
/// <para>
/// Deliberately no foreign key to <see cref="Emote"/>: the observation outlives the grid row.
/// </para>
/// </summary>
public class EmoteSetLeaveObservation
{
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>The 7TV emote id (a 26-character ULID in practice; the column allows 32).</summary>
    public string SevenTvEmoteId { get; set; } = string.Empty;

    /// <summary>The set the leave was observed in (a 26-character ULID in practice; the column allows 32).</summary>
    public string SevenTvEmoteSetId { get; set; } = string.Empty;

    public DateTime LastObservedAtUtc { get; set; }

    public Channel Channel { get; set; } = null!;
}
