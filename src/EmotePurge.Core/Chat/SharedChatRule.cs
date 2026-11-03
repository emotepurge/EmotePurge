namespace EmotePurge.Core.Chat;

/// <summary>
/// The one room-origin rule shared by the live chat path (<c>TwitchChatManager.OnMessageReceived</c>,
/// via <see cref="FromTags"/>) and the chat-log backfill (<c>ChatLogBackfillBlockCounter.Count</c>,
/// via <see cref="Classify"/>) — extracted rather than rewritten (DECISIONS.md, "Shared Chat",
/// 2026-09-06). Both sides decide with the same
/// three-way <see cref="MessageOrigin"/> instead of duplicating the comparison.
/// <para>
/// Deliberately TwitchLib-free: it works on plain strings and an optional tag dictionary, not on
/// <c>ChatMessage</c>. What differs between the two callers is only how they *extract* the tags —
/// the Justlog archive parser reads them from an already-decoded dictionary, the live path reads
/// them from TwitchLib's <c>UndocumentedTags</c>, which TwitchLib 4.0.1 only populates when a
/// message carries at least one tag it does not model as a typed property. <c>source-room-id</c>,
/// <c>source-id</c>, <c>source-badges</c> and <c>source-badge-info</c> are all undocumented in
/// that version — a fact this class relies on but cannot see for itself, so
/// <c>SharedChatRuleTwitchLibTests</c> in the Worker test project proves it, marker by marker,
/// against the installed library rather than trusting a changelog. A future TwitchLib update that types one of these
/// tags would make it disappear from <c>UndocumentedTags</c> and silently blind the live side —
/// that is what those tests are for, and why a TwitchLib bump arrives as its own Dependabot PR.
/// </para>
/// </summary>
public static class SharedChatRule
{
    public const string SourceRoomIdTag = "source-room-id";
    public const string SourceIdTag = "source-id";
    public const string SourceBadgesTag = "source-badges";
    public const string SourceBadgeInfoTag = "source-badge-info";

    /// <summary>
    /// The rule itself, verbatim from the design doc's table: a non-empty <paramref name="sourceRoomId"/>
    /// ordinally unequal to <paramref name="roomId"/> is <see cref="MessageOrigin.Foreign"/> — even
    /// when <paramref name="roomId"/> is <c>null</c>, because a comparison against nothing is never
    /// "equal"; non-empty and equal is <see cref="MessageOrigin.Own"/>; empty/<c>null</c> without
    /// <paramref name="hasOtherSourceMarkers"/> is <see cref="MessageOrigin.Own"/>; empty/<c>null</c>
    /// with it is <see cref="MessageOrigin.Indeterminate"/>. Empty string and <c>null</c> are
    /// equivalent in both string arguments — a blank tag value means the same as an absent tag,
    /// never a value in its own right (Twitch ids are digit strings; whitespace there would be a
    /// parser bug, not data), so trimming does not belong in this rule.
    /// </summary>
    public static MessageOrigin Classify(string? roomId, string? sourceRoomId, bool hasOtherSourceMarkers)
    {
        if (!string.IsNullOrEmpty(sourceRoomId))
        {
            return string.Equals(sourceRoomId, roomId, StringComparison.Ordinal)
                ? MessageOrigin.Own
                : MessageOrigin.Foreign;
        }

        return hasOtherSourceMarkers ? MessageOrigin.Indeterminate : MessageOrigin.Own;
    }

    /// <summary>
    /// Whether any of the three Shared Chat markers other than <see cref="SourceRoomIdTag"/> is
    /// present in <paramref name="tags"/> — presence only, never the value: an empty value still
    /// counts as present. A <c>null</c> dictionary returns <c>false</c>.
    /// </summary>
    public static bool HasOtherSourceMarkers(IReadOnlyDictionary<string, string>? tags) =>
        tags is not null &&
        (tags.ContainsKey(SourceIdTag) || tags.ContainsKey(SourceBadgesTag) || tags.ContainsKey(SourceBadgeInfoTag));

    /// <summary>
    /// The null-safe live extraction: a <c>null</c> <paramref name="tags"/> dictionary is the
    /// ordinary case for a message that carries not a single tag TwitchLib leaves undocumented —
    /// not an error — so this returns <see cref="MessageOrigin.Own"/> without a lookup and without
    /// ever throwing. A <c>TryGetValue</c> straight on the <c>UndocumentedTags</c> property would
    /// be a <see cref="NullReferenceException"/> on the hot path for exactly the messages that
    /// matter most, since most messages carry no undocumented tag at all. Allocation-free, no
    /// logging.
    /// </summary>
    public static MessageOrigin FromTags(string? roomId, IReadOnlyDictionary<string, string>? tags)
    {
        if (tags is null)
        {
            return MessageOrigin.Own;
        }

        tags.TryGetValue(SourceRoomIdTag, out var sourceRoomId);
        return Classify(roomId, sourceRoomId, HasOtherSourceMarkers(tags));
    }
}
