namespace EmotePurge.Core.SevenTv;

/// <summary>
/// One entry of 7TV's public v4 read <c>user(id).editorFor</c> (#487 plan 2.5b, Revision 6b K6b-13): a channel owner
/// the queried account is an editor (or invited editor) of, as 7TV reports it. Raw on purpose: what an entry means for
/// the bot (<c>editor</c>, <c>invited</c>, <c>not_editor</c>) is decided by <c>BotAccountEditorEntry.Of</c> in one place.
/// </summary>
/// <param name="OwnerSevenTvUserId">The owner's 7TV user id (<c>userId</c>), the target of an invitation acceptance.</param>
/// <param name="OwnerTwitchUserId">The <c>platformId</c> of the owner's <c>TWITCH</c> connection, which maps the entry to a channel.</param>
/// <param name="State">7TV's <c>UserEditorState</c> as sent (<see cref="Accepted"/>, <see cref="Pending"/>, <see cref="Rejected"/>, or anything newer); null when absent.</param>
/// <param name="ManagesEmoteSets"><c>permissions.emoteSet.manage</c>; false when absent.</param>
/// <param name="AddedAtUtc">7TV's <c>addedAt</c> of the entry (when the invitation was made), for the log only; null when absent or unreadable.</param>
public sealed record SevenTvEditorForEntry(
    string OwnerSevenTvUserId,
    string OwnerTwitchUserId,
    string? State,
    bool ManagesEmoteSets,
    DateTime? AddedAtUtc)
{
    /// <summary>The invitation was accepted by the editor account; only now may it write (probe T0, A5).</summary>
    public const string Accepted = "ACCEPTED";

    /// <summary>Invited, not accepted yet.</summary>
    public const string Pending = "PENDING";

    /// <summary>The invitation was declined.</summary>
    public const string Rejected = "REJECTED";
}

/// <summary>
/// The answer of <see cref="ISevenTvApiClient.GetEditorForAsync"/>. <see cref="SevenTvLookupStatus.Ok"/> carries every
/// entry with a Twitch connection (an empty list is a real answer: an editor of nobody); every unusable answer, a 429
/// included, is <see cref="SevenTvLookupStatus.Unavailable"/> and never an empty list (fail-closed: an empty list would
/// read as "editor nowhere" and suspend every channel).
/// </summary>
public sealed class SevenTvEditorForResult
{
    private SevenTvEditorForResult(SevenTvLookupStatus status, IReadOnlyList<SevenTvEditorForEntry>? entries)
    {
        Status = status;
        Entries = entries;
    }

    public SevenTvLookupStatus Status { get; }

    /// <summary>Non-null if and only if <see cref="Status"/> is <see cref="SevenTvLookupStatus.Ok"/>.</summary>
    public IReadOnlyList<SevenTvEditorForEntry>? Entries { get; }

    public static SevenTvEditorForResult Ok(IReadOnlyList<SevenTvEditorForEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new SevenTvEditorForResult(SevenTvLookupStatus.Ok, entries);
    }

    public static SevenTvEditorForResult Failed(SevenTvLookupStatus status)
    {
        SevenTvLookupStatusGuard.ThrowIfNotAFailure(status, nameof(SevenTvEditorForResult), "Ok(entries)");
        return new SevenTvEditorForResult(status, null);
    }
}
