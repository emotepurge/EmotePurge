namespace EmotePurge.Core.Services;

/// <summary>
/// The four outcomes of the set-centric import's owner check (spec 6.7, E22) — never a plain bool:
/// the endpoint needs to tell "no such set" (404) apart from "not allowed" (403) and "7TV would not
/// say" (503, no audit entry), and on a match it needs the matched owner's identity for the audit
/// row (<c>targetOwnerSevenTvUserId</c>/<c>targetOwnerTwitchLogin</c>), not just a yes.
/// </summary>
public enum SevenTvEmoteSetOwnershipStatus
{
    Owner,
    SetNotFound,
    Forbidden,
    Unavailable
}

/// <summary>
/// Which account probably owns the set a caller asks about — the Twitch id and/or login the client
/// already had in hand (the picker's account, a run's frozen owner, a protocol file's channel).
/// </summary>
/// <remarks>
/// <para>
/// <b>An order, never a permission.</b> The owner check only uses a hint to decide which list it
/// reads next to the actor's own; whether the set is admissible is still decided by
/// <see cref="EmoteSetEditability.IsEditable"/> over the lists of verified accounts alone. A hint
/// that names neither the actor nor one of the actor's <c>editor_of</c> grants is dropped before any
/// request is made, and a hint that names a grant counts only once the actor's own list has not
/// answered <see cref="EmoteSetListStatus.NoSevenTvAccount"/>.
/// </para>
/// <para>
/// <see cref="TwitchUserId"/> wins; <see cref="TwitchLogin"/> is only consulted without it, and only
/// ever resolves to the matching account's Twitch id. A blank value counts as absent, and a hint
/// with neither value is no hint.
/// </para>
/// </remarks>
/// <param name="TwitchUserId">The probable owner's immutable Twitch user id.</param>
/// <param name="TwitchLogin">
/// The probable owner's Twitch login, compared after <c>ChannelName.Normalize</c> on both sides —
/// the fallback for callers that know nothing but a channel name.
/// </param>
public sealed record EmoteSetOwnerHint(string? TwitchUserId = null, string? TwitchLogin = null);

/// <summary>
/// The result of <see cref="IImportTargetOwnershipService.CheckAsync"/> and
/// <see cref="IImportTargetOwnershipService.ResolveEditableAsync"/>. The three identity properties
/// are non-null if and only if <see cref="Status"/> is <see cref="SevenTvEmoteSetOwnershipStatus.Owner"/>,
/// built like the other result families in this namespace (see
/// <see cref="SevenTvEditorGrantsLookupResult"/>).
/// </summary>
public sealed class SevenTvEmoteSetOwnershipCheckResult
{
    private SevenTvEmoteSetOwnershipCheckResult(
        SevenTvEmoteSetOwnershipStatus status,
        string? ownerSevenTvUserId,
        string? ownerTwitchLogin,
        string? ownerTwitchUserId,
        EmoteSetSummary? emoteSet = null,
        string? sevenTvActiveEmoteSetId = null)
    {
        Status = status;
        OwnerSevenTvUserId = ownerSevenTvUserId;
        OwnerTwitchLogin = ownerTwitchLogin;
        OwnerTwitchUserId = ownerTwitchUserId;
        EmoteSet = emoteSet;
        SevenTvActiveEmoteSetId = sevenTvActiveEmoteSetId;
    }

    public SevenTvEmoteSetOwnershipStatus Status { get; }

    /// <summary>Non-null exactly when <see cref="Status"/> is <see cref="SevenTvEmoteSetOwnershipStatus.Owner"/>.</summary>
    public string? OwnerSevenTvUserId { get; }

    /// <summary>
    /// Non-null exactly when <see cref="Status"/> is <see cref="SevenTvEmoteSetOwnershipStatus.Owner"/> —
    /// the actor's own Twitch login when the actor owns the set directly, or the matching grant's
    /// <see cref="SevenTvEditorGrantEntry.ChannelLogin"/> when ownership came through an editor grant
    /// (spec 6.7: "der Login des passenden Grants bzw. des Akteurs" — a Twitch login for the paper
    /// trail, deliberately never a 7TV display name).
    /// </summary>
    public string? OwnerTwitchLogin { get; }

    /// <summary>
    /// Non-null exactly when <see cref="Status"/> is <see cref="SevenTvEmoteSetOwnershipStatus.Owner"/> —
    /// the Twitch id of the same account as <see cref="OwnerTwitchLogin"/>: the actor's own id, or
    /// the matching grant's <see cref="SevenTvEditorGrantEntry.TwitchChannelId"/>. The set-centric
    /// delete/restore report resolves the owner's tracked channel from it (restore-per-set spec,
    /// addendum N3) — by id, not login, because logins move and the id does not.
    /// </summary>
    public string? OwnerTwitchUserId { get; }

    /// <summary>
    /// The set as a list named it — non-null exactly when <see cref="Status"/> is
    /// <see cref="SevenTvEmoteSetOwnershipStatus.Owner"/> <b>and</b> the match came from a set list;
    /// always <c>null</c> after the report's owner-lookup fallback, which learns nothing but an owner
    /// id. Taken from the owner account's own list when that list carries the set, otherwise from the
    /// list that did.
    /// </summary>
    public EmoteSetSummary? EmoteSet { get; }

    /// <summary>
    /// <see cref="EmoteSetList.SevenTvActiveEmoteSetId"/> of the <b>owner</b> account's list — the
    /// account behind <see cref="OwnerTwitchUserId"/>, never merely the account that listed the set.
    /// <c>null</c> whenever <see cref="EmoteSet"/> is, when the owner's own list did not carry the set
    /// (it was only listed under another checked account), or when 7TV reported no active set.
    /// </summary>
    public string? SevenTvActiveEmoteSetId { get; }

    public static SevenTvEmoteSetOwnershipCheckResult Owner(string ownerSevenTvUserId, string ownerTwitchLogin, string ownerTwitchUserId) =>
        new(SevenTvEmoteSetOwnershipStatus.Owner, ownerSevenTvUserId, ownerTwitchLogin, ownerTwitchUserId);

    /// <summary>An owner found in a set list, with what that list said about the set.</summary>
    public static SevenTvEmoteSetOwnershipCheckResult Owner(
        string ownerSevenTvUserId,
        string ownerTwitchLogin,
        string ownerTwitchUserId,
        EmoteSetSummary emoteSet,
        string? sevenTvActiveEmoteSetId)
    {
        ArgumentNullException.ThrowIfNull(emoteSet);
        return new(
            SevenTvEmoteSetOwnershipStatus.Owner, ownerSevenTvUserId, ownerTwitchLogin, ownerTwitchUserId, emoteSet, sevenTvActiveEmoteSetId);
    }

    public static SevenTvEmoteSetOwnershipCheckResult SetNotFound() =>
        new(SevenTvEmoteSetOwnershipStatus.SetNotFound, null, null, null);

    public static SevenTvEmoteSetOwnershipCheckResult Forbidden() =>
        new(SevenTvEmoteSetOwnershipStatus.Forbidden, null, null, null);

    public static SevenTvEmoteSetOwnershipCheckResult Unavailable() =>
        new(SevenTvEmoteSetOwnershipStatus.Unavailable, null, null, null);
}

/// <summary>
/// The owner check of the set-centric reports — <c>POST /api/seventv/emote-sets/{emoteSetId}/sync-imported</c>
/// (spec 6.7, ladder step 4, as amended by section 32) and the set-centric
/// <c>sync-deleted</c>/<c>sync-restored</c> — and of the editable pre-check: is the set's owner the
/// actor, or an account the actor holds a 7TV <c>editor_of</c> grant on?
/// </summary>
/// <remarks>
/// <para>
/// <b>What it protects.</b> A report runs <i>after</i> its 7TV mutation — which already happened
/// with the user's own token. The check is therefore not access control there; it keeps the audit
/// log honest, so nobody writes entries about a set they do not edit. That is why it must not cost
/// unguarded 7TV requests: the reports sit under the <c>Bookkeeping</c> policy (120/min per user),
/// and a check that asked 7TV directly on every call could drain the shared provider bucket.
/// </para>
/// <para>
/// <b>How it answers.</b> From the set lists of <see cref="ISevenTvEmoteSetListService"/> — the
/// same cached, coalesced, budgeted lists the target picker is built from — for the actor and every
/// <c>editor_of</c> account, read in that order and serially. With a valid
/// <see cref="EmoteSetOwnerHint"/> on a grant, that grant's list is read in parallel with the actor's
/// own, and the rest only if neither settles it. Only the report asks 7TV about a set that is in no
/// list (or listed without an owner): one budgeted owner lookup. The pre-check never does.
/// </para>
/// <para>
/// <b>What it costs.</b> Nothing while the lists are cached (60 s). Cold: one list request when the
/// actor owns the set (hinted or not); two, in a single round trip, with a valid hint on a grant; up
/// to <c>1 + k</c> serial ones for <c>k</c> grants without a valid hint, plus the report's one owner
/// lookup for a set in no list. Reading the grants adds two more requests when their ten-minute cache
/// is cold — also for a hint that names another account and is dropped: it makes no list request of
/// its own, but resolving it needs the grants first (a hint on the actor does not). Every request is budgeted, behind the breaker and the coalescer.
/// </para>
/// </remarks>
public interface IImportTargetOwnershipService
{
    /// <summary>The reports' check. Never null.</summary>
    /// <param name="actorTwitchLogin">
    /// Only feeds <see cref="SevenTvEmoteSetOwnershipCheckResult.OwnerTwitchLogin"/> when the actor
    /// turns out to own the set themselves.
    /// </param>
    /// <param name="ownerHint">
    /// Optional, after the token so positional callers keep compiling; <c>null</c> walks the lists
    /// exactly as without a hint.
    /// </param>
    Task<SevenTvEmoteSetOwnershipCheckResult> CheckAsync(
        string actorTwitchUserId,
        string actorTwitchLogin,
        string emoteSetId,
        CancellationToken cancellationToken = default,
        EmoteSetOwnerHint? ownerHint = null);

    /// <summary>
    /// The editable pre-check's variant of <see cref="CheckAsync"/>: the same list walk, but never the
    /// owner lookup — a set in no readable list is <see cref="SevenTvEmoteSetOwnershipStatus.SetNotFound"/>,
    /// a set listed only under foreign owners or without an owner id is
    /// <see cref="SevenTvEmoteSetOwnershipStatus.Forbidden"/> (strictly never looser than the report,
    /// F16), and any unreadable source without an admissible find is
    /// <see cref="SevenTvEmoteSetOwnershipStatus.Unavailable"/>. An <c>Owner</c> result always carries
    /// <see cref="SevenTvEmoteSetOwnershipCheckResult.EmoteSet"/>. Never null.
    /// </summary>
    Task<SevenTvEmoteSetOwnershipCheckResult> ResolveEditableAsync(
        string actorTwitchUserId,
        string actorTwitchLogin,
        string emoteSetId,
        CancellationToken cancellationToken = default,
        EmoteSetOwnerHint? ownerHint = null);
}
