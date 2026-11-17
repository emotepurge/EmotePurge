namespace EmotePurge.Core.Services;

/// <summary>
/// The cross-channel side of wish emotes (#481, spec E18): what needs a manager's attention across every channel
/// the caller may manage. Both reads start at the channels that have such wishes at all (one index query) and
/// ask <see cref="IChannelAccessService.CanManageChannelAsync"/> only for those; neither goes through
/// <c>IMyChannelsService</c>. Whether a role check costs a Helix call is the moderated-channel cache's business
/// (<c>Auth:ModCheckCacheTtlMinutes</c>); this service adds no cache of its own.
/// </summary>
public interface IWishOverviewService
{
    /// <summary>
    /// The wishes that need action or are in probation (E2) in every channel the caller manages, sorted by
    /// expiry (none last), then creation. A caller who is only a 7TV editor of a channel sees nothing of it.
    /// </summary>
    Task<IReadOnlyList<WishOverviewItemDto>> ListMineAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// The number of wishes that need action in the channels the caller manages (the header counter). Zero without
    /// any role check when no channel has such a wish.
    /// </summary>
    Task<int> CountActionAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default);
}

/// <summary>One row of the overview: the wish and the channel it belongs to.</summary>
public sealed record WishOverviewItemDto(string ChannelName, WishDto Wish);
