using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Services;

/// <summary>
/// One 7TV editor grant, exactly as 7TV paired the two identifiers when the grant was resolved.
/// </summary>
/// <param name="ChannelLogin">Normalized via <see cref="Entities.ChannelName.Normalize"/> — this is
/// 7TV's own copy of the login, which can be stale after a Twitch rename.</param>
/// <param name="TwitchChannelId">The numeric Twitch id, opaque and compared with
/// <see cref="StringComparison.Ordinal"/> — never normalized.</param>
public record SevenTvEditorGrantEntry(string ChannelLogin, string TwitchChannelId);

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
}
