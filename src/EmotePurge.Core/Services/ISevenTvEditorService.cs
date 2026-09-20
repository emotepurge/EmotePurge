using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Services;

/// <summary>
/// One 7TV editor grant, exactly as 7TV paired the two identifiers when the grant was resolved.
/// </summary>
/// <param name="ChannelLogin">Normalized via <see cref="Entities.ChannelName.Normalize"/> — this is
/// 7TV's own copy of the login, which can be stale after a Twitch rename.</param>
/// <param name="TwitchChannelId">The numeric Twitch id, opaque and compared with
/// <see cref="StringComparison.Ordinal"/> — never normalized.</param>
/// <param name="SevenTvUserId">
/// The 7TV account id behind this Twitch channel (E22, spec 2026-09-20) — additive and nullable.
/// <c>null</c> covers two different situations a reader must not conflate: a grant resolved live
/// before this field existed in the 7TV query always carries it now, but a grant read back from
/// <c>ModRoleCache</c> that was <em>written</em> before the field existed (F10 — the cache holds a
/// JSON payload with a ten-minute TTL) still deserializes with it missing. The set-centric import's
/// owner check (6.7, AK 31) must resolve that case live via
/// <see cref="ISevenTvApiClient.ResolveSevenTvIdentityAsync"/> rather than reading the absence as
/// "not an editor".
/// </param>
public record SevenTvEditorGrantEntry(string ChannelLogin, string TwitchChannelId, string? SevenTvUserId = null);

/// <summary>
/// A user's 7TV editor grants, normalized once. Carries both identifiers because the two callers ask
/// different questions: an authorization check matches on the immutable <see cref="TwitchChannelIds"/>
/// wherever the channel row has one (Twitch releases freed-up logins for re-registration — see the
/// broadcaster check in ChannelAccessService for the same reasoning), while the overview has nothing
/// but logins to work with.
/// </summary>
/// <param name="ChannelLogins">Lower-cased, trimmed Twitch logins of every channel the user may edit.</param>
/// <param name="TwitchChannelIds">The numeric Twitch ids of the same channels.</param>
/// <param name="Entries">
/// The same grants as login↔id pairs. <see cref="ChannelLogins"/> and <see cref="TwitchChannelIds"/>
/// exist for authorization (<c>ChannelAccessService</c>), which only ever asks "is this login/id in
/// the grant set" — deliberately kept independent of a second foreign system, since the auth path
/// must not depend on Helix being reachable. <see cref="Entries"/> exists for the overview
/// (<c>MyChannelsService</c>), which has to decide per grant whether 7TV's reported login is still
/// current or needs resolving against Helix. Omitting this parameter yields an empty list, which is
/// also what a cache entry written before this field existed deserializes to — see
/// <c>ModRoleCache</c> for how that legacy shape is produced and <c>MyChannelsService</c> for how it
/// is detected (empty <see cref="Entries"/> alongside a non-empty <see cref="ChannelLogins"/>) and
/// handled.
/// </param>
public record SevenTvEditorGrants(IReadOnlySet<string> ChannelLogins, IReadOnlySet<string> TwitchChannelIds, IReadOnlyList<SevenTvEditorGrantEntry> Entries)
{
    public SevenTvEditorGrants(IReadOnlySet<string> channelLogins, IReadOnlySet<string> twitchChannelIds)
        : this(channelLogins, twitchChannelIds, [])
    {
    }
}

/// <summary>
/// The result of a grant lookup: <see cref="Grants"/> is populated if and only if <see cref="Status"/>
/// is <see cref="SevenTvLookupStatus.Ok"/>. Ok always carries a (possibly empty) grant set — "answered:
/// this user edits nothing" is Ok, not a failure status. The two factories are the only way to build
/// one at all; built like <see cref="SevenTvChannelStateResult"/>, whose remarks carry the reasoning
/// for the whole family.
/// </summary>
public sealed class SevenTvEditorGrantsLookupResult
{
    private SevenTvEditorGrantsLookupResult(SevenTvLookupStatus status, SevenTvEditorGrants? grants)
    {
        Status = status;
        Grants = grants;
    }

    public SevenTvLookupStatus Status { get; }

    /// <summary>Non-null if and only if <see cref="Status"/> is <see cref="SevenTvLookupStatus.Ok"/>.</summary>
    public SevenTvEditorGrants? Grants { get; }

    public static SevenTvEditorGrantsLookupResult Ok(SevenTvEditorGrants grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        return new SevenTvEditorGrantsLookupResult(SevenTvLookupStatus.Ok, grants);
    }

    public static SevenTvEditorGrantsLookupResult Failed(SevenTvLookupStatus status)
    {
        SevenTvLookupStatusGuard.ThrowIfNotAFailure(status, nameof(SevenTvEditorGrantsLookupResult), "Ok(grants)");
        return new SevenTvEditorGrantsLookupResult(status, null);
    }
}

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
/// The result of <see cref="ISevenTvEditorService.CheckEmoteSetOwnershipAsync"/>. The two identity
/// properties are non-null if and only if <see cref="Status"/> is
/// <see cref="SevenTvEmoteSetOwnershipStatus.Owner"/>, built like the other result families in this
/// namespace (see <see cref="SevenTvEditorGrantsLookupResult"/>).
/// </summary>
public sealed class SevenTvEmoteSetOwnershipCheckResult
{
    private SevenTvEmoteSetOwnershipCheckResult(SevenTvEmoteSetOwnershipStatus status, string? ownerSevenTvUserId, string? ownerTwitchLogin)
    {
        Status = status;
        OwnerSevenTvUserId = ownerSevenTvUserId;
        OwnerTwitchLogin = ownerTwitchLogin;
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

    public static SevenTvEmoteSetOwnershipCheckResult Owner(string ownerSevenTvUserId, string ownerTwitchLogin) =>
        new(SevenTvEmoteSetOwnershipStatus.Owner, ownerSevenTvUserId, ownerTwitchLogin);

    public static SevenTvEmoteSetOwnershipCheckResult SetNotFound() =>
        new(SevenTvEmoteSetOwnershipStatus.SetNotFound, null, null);

    public static SevenTvEmoteSetOwnershipCheckResult Forbidden() =>
        new(SevenTvEmoteSetOwnershipStatus.Forbidden, null, null);

    public static SevenTvEmoteSetOwnershipCheckResult Unavailable() =>
        new(SevenTvEmoteSetOwnershipStatus.Unavailable, null, null);
}

/// <summary>
/// Answers "which channels is this user a 7TV editor of?" — the single implementation of a chain
/// (resolve 7TV identity, then look up editor grants) that used to exist twice, with two different
/// string-comparison strategies, in ChannelAccessService and MyChannelsService.
/// </summary>
public interface ISevenTvEditorService
{
    /// <summary>
    /// Never null. NoSevenTvAccount ("this Twitch user has no 7TV account at all") and Unavailable
    /// ("7TV could not answer") are deliberately distinct failure statuses (issue #37) — collapsing
    /// them used to make every account-less user look like a failed lookup. Neither is cached as a
    /// negative, and callers must not treat the two failure statuses the same: MyChannelsService
    /// reports only Unavailable as a degradation, while ChannelAccessService's authorization check
    /// fails closed on both.
    /// </summary>
    Task<SevenTvEditorGrantsLookupResult> GetEditorGrantsAsync(string twitchUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The set-centric import's owner check (spec 6.7, E22): does <paramref name="actorTwitchUserId"/>
    /// own <paramref name="emoteSetId"/> directly, or hold a 7TV editor grant on its owner? Checks the
    /// actor's own identity first, then every grant's <see cref="SevenTvEditorGrantEntry.SevenTvUserId"/>
    /// — resolving it live via <see cref="ISevenTvApiClient.ResolveSevenTvIdentityAsync"/> per grant
    /// when it is missing (F10/AK 31: a cache payload written before that field existed), never
    /// treating the absence as "not an editor". <paramref name="actorTwitchLogin"/> only feeds
    /// <see cref="SevenTvEmoteSetOwnershipCheckResult.OwnerTwitchLogin"/> for the case the actor turns
    /// out to be the owner themselves.
    /// </summary>
    Task<SevenTvEmoteSetOwnershipCheckResult> CheckEmoteSetOwnershipAsync(
        string actorTwitchUserId, string actorTwitchLogin, string emoteSetId, CancellationToken cancellationToken = default);
}
