using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Services;

/// <summary>
/// The bot account's editor check (#487 plan 2.5, 2.5b, Revision 5a and 6): account-wide, one unauthenticated read
/// answers every channel at once and is reused for one minute. The booking against <c>ChannelWishSettings</c>, the
/// suspension and the activation stay with the wishes; this only reads. A read is bound to the bot id it read for and
/// the <c>IdentityVersion</c> the caller passes (plan 2.3): a booking from it is conditional on both, and a read cached
/// under another identity is never handed out.
/// </summary>
public interface IBotAccountEditorCheck
{
    /// <summary>
    /// The editor state of <paramref name="botSevenTvUserId"/> in every channel. A read of the same bot id and identity that
    /// began less than a minute ago is reused, unless <paramref name="startedAfterUtc"/> asks for one that began strictly
    /// after that instant (the visibility window of a young request, plan 2.5b S6-3). An unusable answer is
    /// <see cref="BotAccountEditorRead.IsAvailable"/> false and is never reused.
    /// </summary>
    Task<BotAccountEditorRead> ReadAsync(
        string botSevenTvUserId,
        int identityVersion,
        DateTime? startedAfterUtc = null,
        CancellationToken cancellationToken = default);
}

/// <summary>What the bot account is in one channel according to the editor read (plan 2.5b).</summary>
public enum BotAccountEditorStatus
{
    /// <summary>No entry, another state, or the "manage emote sets" permission missing: the bot cannot write there.</summary>
    NotEditor,

    /// <summary><c>PENDING</c> with "manage emote sets": an invitation the bot account could accept.</summary>
    Invited,

    /// <summary><c>ACCEPTED</c> with "manage emote sets": the only positive result.</summary>
    Editor,
}

/// <summary>
/// The editor read's verdict for one channel (plan 2.5b, Revision 6d K6d-2): the status, whether 7TV's raw state is
/// <c>ACCEPTED</c> (with or without "manage emote sets"; it counts only for resolving an acceptance attempt), and the
/// owner's 7TV id (the target of an acceptance; null for a channel without an entry).
/// </summary>
public readonly record struct BotAccountEditorEntry(BotAccountEditorStatus Status, bool Accepted, string? OwnerSevenTvUserId)
{
    /// <summary>A channel the read has no entry for.</summary>
    public static BotAccountEditorEntry Absent { get; } = new(BotAccountEditorStatus.NotEditor, false, null);

    /// <summary>
    /// The one mapping of a raw entry (plan 2.5b): <c>ACCEPTED</c> and manage → editor; <c>PENDING</c> and manage →
    /// invited; everything else, an unknown or missing state included, → not_editor. <c>ACCEPTED</c> without manage is
    /// not_editor but keeps <see cref="Accepted"/>.
    /// </summary>
    public static BotAccountEditorEntry Of(string? state, bool managesEmoteSets, string? ownerSevenTvUserId)
    {
        var accepted = string.Equals(state, SevenTvEditorForEntry.Accepted, StringComparison.Ordinal);
        var status = !managesEmoteSets
            ? BotAccountEditorStatus.NotEditor
            : accepted
                ? BotAccountEditorStatus.Editor
                : string.Equals(state, SevenTvEditorForEntry.Pending, StringComparison.Ordinal)
                    ? BotAccountEditorStatus.Invited
                    : BotAccountEditorStatus.NotEditor;
        return new BotAccountEditorEntry(status, accepted, ownerSevenTvUserId);
    }
}

/// <summary>
/// One editor read of <see cref="IBotAccountEditorCheck"/>: for which bot id and identity it was made, when it began,
/// and, when it is available, the verdict per channel by the channel's Twitch id.
/// </summary>
public sealed class BotAccountEditorRead
{
    private readonly IReadOnlyDictionary<string, BotAccountEditorEntry> _byTwitchUserId;

    private BotAccountEditorRead(
        bool isAvailable,
        string botSevenTvUserId,
        int identityVersion,
        DateTime startedAtUtc,
        IReadOnlyDictionary<string, BotAccountEditorEntry> byTwitchUserId)
    {
        IsAvailable = isAvailable;
        BotSevenTvUserId = botSevenTvUserId;
        IdentityVersion = identityVersion;
        StartedAtUtc = startedAtUtc;
        _byTwitchUserId = byTwitchUserId;
    }

    /// <summary>False: 7TV gave no usable answer. Nothing may be suspended or resumed from it (plan 2.5).</summary>
    public bool IsAvailable { get; }

    /// <summary>The bot account's 7TV id the read was made for.</summary>
    public string BotSevenTvUserId { get; }

    /// <summary>The <c>IdentityVersion</c> the read is bound to; a booking from it requires the row to still show it.</summary>
    public int IdentityVersion { get; }

    /// <summary>When the read began (before the request was sent), on the check's clock.</summary>
    public DateTime StartedAtUtc { get; }

    public static BotAccountEditorRead Available(
        string botSevenTvUserId, int identityVersion, DateTime startedAtUtc, IReadOnlyDictionary<string, BotAccountEditorEntry> byTwitchUserId)
    {
        ArgumentNullException.ThrowIfNull(botSevenTvUserId);
        ArgumentNullException.ThrowIfNull(byTwitchUserId);
        return new BotAccountEditorRead(true, botSevenTvUserId, identityVersion, startedAtUtc, byTwitchUserId);
    }

    public static BotAccountEditorRead Unavailable(string botSevenTvUserId, int identityVersion, DateTime startedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(botSevenTvUserId);
        return new BotAccountEditorRead(
            false, botSevenTvUserId, identityVersion, startedAtUtc, new Dictionary<string, BotAccountEditorEntry>(StringComparer.Ordinal));
    }

    /// <summary>
    /// The verdict for the channel with <paramref name="twitchChannelId"/>: <see cref="BotAccountEditorEntry.Absent"/> when
    /// the read has no entry for it (or the channel has no Twitch id), null when the read is not available.
    /// </summary>
    public BotAccountEditorEntry? For(string? twitchChannelId)
    {
        if (!IsAvailable)
        {
            return null;
        }

        return twitchChannelId is not null && _byTwitchUserId.TryGetValue(twitchChannelId, out var entry)
            ? entry
            : BotAccountEditorEntry.Absent;
    }
}
