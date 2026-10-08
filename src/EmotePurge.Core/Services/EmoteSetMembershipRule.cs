namespace EmotePurge.Core.Services;

/// <summary>
/// The one rule behind "does this 7TV emote set belong to this tracked channel": the set is a
/// <c>NORMAL</c> set in the channel's own 7TV set list, or it is the channel's observed active set.
/// </summary>
/// <remarks>
/// Two callers share this rule so it cannot drift apart between them: the vote-session creation's
/// step 0 for set sessions (<c>VoteSessionService</c>) and the tracked-channel set preview's
/// membership proof (<c>TrackedEmoteSetMembershipService</c>). Only the <c>Ok</c> branch is shared —
/// each caller maps the non-<c>Ok</c> list states to its own outcome.
/// </remarks>
public static class EmoteSetMembershipRule
{
    /// <param name="list">The channel's 7TV set list, read successfully.</param>
    /// <param name="channelActiveEmoteSetId">
    /// <c>Channel.ActiveEmoteSetId</c>, our own observed state; a match on it counts even when the
    /// list does not carry the set.
    /// </param>
    /// <param name="emoteSetId">The set being asked about, compared ordinally.</param>
    public static bool BelongsToChannel(EmoteSetList list, string? channelActiveEmoteSetId, string? emoteSetId)
    {
        ArgumentNullException.ThrowIfNull(list);

        // Without this, an empty or missing id matches an equally empty active-set id.
        if (string.IsNullOrEmpty(emoteSetId))
        {
            return false;
        }

        return list.Sets.Any(set =>
            string.Equals(set.Id, emoteSetId, StringComparison.Ordinal)
            && string.Equals(set.Kind, "NORMAL", StringComparison.Ordinal))
            || string.Equals(emoteSetId, channelActiveEmoteSetId, StringComparison.Ordinal);
    }
}
