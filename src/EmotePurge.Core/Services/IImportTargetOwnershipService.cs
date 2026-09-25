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
/// The result of <see cref="IImportTargetOwnershipService.CheckAsync"/>. The three identity
/// properties are non-null if and only if <see cref="Status"/> is
/// <see cref="SevenTvEmoteSetOwnershipStatus.Owner"/>, built like the other result families in this
/// namespace (see <see cref="SevenTvEditorGrantsLookupResult"/>).
/// </summary>
public sealed class SevenTvEmoteSetOwnershipCheckResult
{
    private SevenTvEmoteSetOwnershipCheckResult(
        SevenTvEmoteSetOwnershipStatus status, string? ownerSevenTvUserId, string? ownerTwitchLogin, string? ownerTwitchUserId)
    {
        Status = status;
        OwnerSevenTvUserId = ownerSevenTvUserId;
        OwnerTwitchLogin = ownerTwitchLogin;
        OwnerTwitchUserId = ownerTwitchUserId;
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

    public static SevenTvEmoteSetOwnershipCheckResult Owner(string ownerSevenTvUserId, string ownerTwitchLogin, string ownerTwitchUserId) =>
        new(SevenTvEmoteSetOwnershipStatus.Owner, ownerSevenTvUserId, ownerTwitchLogin, ownerTwitchUserId);

    public static SevenTvEmoteSetOwnershipCheckResult SetNotFound() =>
        new(SevenTvEmoteSetOwnershipStatus.SetNotFound, null, null, null);

    public static SevenTvEmoteSetOwnershipCheckResult Forbidden() =>
        new(SevenTvEmoteSetOwnershipStatus.Forbidden, null, null, null);

    public static SevenTvEmoteSetOwnershipCheckResult Unavailable() =>
        new(SevenTvEmoteSetOwnershipStatus.Unavailable, null, null, null);
}

/// <summary>
/// The owner check of <c>POST /api/seventv/emote-sets/{emoteSetId}/sync-imported</c> (spec 6.7,
/// ladder step 4, as amended by section 32): is the set's owner the actor, or an account the actor
/// holds a 7TV <c>editor_of</c> grant on?
/// </summary>
/// <remarks>
/// <para>
/// <b>What it protects.</b> The report runs <i>after</i> the import — the 7TV mutation already
/// happened with the user's own token. This check is therefore not access control; it keeps the
/// audit log honest, so nobody writes entries about a set they do not edit. That is why it must not
/// cost unguarded 7TV requests: the route sits under the <c>Bookkeeping</c> policy (120/min per
/// user), and a check that asked 7TV directly on every call could drain the shared provider bucket.
/// </para>
/// <para>
/// <b>How it answers.</b> From the set lists of <see cref="ISevenTvEmoteSetListService"/> — the
/// same cached, coalesced, budgeted lists the target picker was built from — for the actor and every
/// <c>editor_of</c> account. Only a set that is in none of them (or listed without an owner) costs
/// one budgeted owner lookup; that path is reached by a misrouted or forged request, not by the
/// picker.
/// </para>
/// </remarks>
public interface IImportTargetOwnershipService
{
    /// <summary>Never null.</summary>
    /// <param name="actorTwitchLogin">
    /// Only feeds <see cref="SevenTvEmoteSetOwnershipCheckResult.OwnerTwitchLogin"/> when the actor
    /// turns out to own the set themselves.
    /// </param>
    Task<SevenTvEmoteSetOwnershipCheckResult> CheckAsync(
        string actorTwitchUserId, string actorTwitchLogin, string emoteSetId, CancellationToken cancellationToken = default);
}
