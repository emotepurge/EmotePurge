using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class ChannelAccessService(
    IModeratorCheckService moderatorCheckService,
    ISevenTvEditorService sevenTvEditorService,
    IChannelService channelService,
    IGlobalAdminAllowlist globalAdminAllowlist,
    ILogger<ChannelAccessService> logger) : IChannelAccessService
{
    public async Task<bool> CanManageChannelAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default)
    {
        var normalizedChannel = ChannelName.Normalize(channelName);

        if (IsGlobalAdmin(principal))
        {
            return true;
        }

        var channel = await channelService.GetByNameAsync(normalizedChannel, cancellationToken);
        if (IsBroadcaster(principal, normalizedChannel, channel?.TwitchChannelId))
        {
            return true;
        }

        return await moderatorCheckService.IsModeratorAsync(principal, normalizedChannel, cancellationToken);
    }

    public async Task<bool> CanViewUsageStatsAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default)
    {
        if (await CanManageChannelAsync(principal, channelName, cancellationToken))
        {
            return true;
        }

        var normalizedChannel = ChannelName.Normalize(channelName);

        // Resolution and caching of the grants live in ISevenTvEditorService — this used to be the
        // most expensive authorization path in the app (two sequential 7TV calls per request, on
        // endpoints a viewer can poll freely) and one of two independent copies of the same chain.
        var grantsResult = await sevenTvEditorService.GetEditorGrantsAsync(principal.TwitchUserId, cancellationToken);
        // Fail-closed on every non-Ok status, NoSevenTvAccount included: this is an authorization
        // check, not a status display, so "this user simply has no 7TV account" must deny access
        // exactly like "7TV is unreachable" does. Do not loosen this to only Unavailable — that
        // distinction belongs to MyChannelsService's overview banner, not to this gate.
        if (grantsResult.Status != SevenTvLookupStatus.Ok)
        {
            return false;
        }

        var grants = grantsResult.Grants!;

        // Matched on the immutable Twitch id where we have one, for the same reason as IsBroadcaster.
        var channel = await channelService.GetByNameAsync(normalizedChannel, cancellationToken);
        return channel?.TwitchChannelId is { } channelTwitchId
            ? grants.TwitchChannelIds.Contains(channelTwitchId)
            : grants.ChannelLogins.Contains(normalizedChannel);
    }

    public bool IsGlobalAdmin(TwitchPrincipalInfo principal) => globalAdminAllowlist.IsAdmin(principal);

    // Twitch permits renames and releases the old name again after a grace period. Deciding "is
    // broadcaster" on the login alone therefore handed the channel to whoever registered the freed-up
    // name next: a pure string comparison, without consulting Twitch, 7TV or the database, granting
    // full usage statistics, vote-session control and the channel's entire history. The immutable
    // numeric id is authoritative wherever we have one; the login comparison survives only as a
    // fallback for rows whose TwitchChannelId has never been resolved (nullable until the first sync).
    private bool IsBroadcaster(TwitchPrincipalInfo principal, string normalizedChannel, string? channelTwitchId)
    {
        if (channelTwitchId is null)
        {
            return string.Equals(principal.TwitchLogin, normalizedChannel, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(channelTwitchId, principal.TwitchUserId, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(principal.TwitchLogin, normalizedChannel, StringComparison.OrdinalIgnoreCase))
        {
            // Either the channel was renamed and this is its new owner, or the stored id is wrong.
            // Denied either way, but logged: otherwise this is a rejection nobody could explain.
            logger.LogWarning(
                "Broadcaster-Zugriff auf {Channel} abgelehnt: Login stimmt überein, aber die hinterlegte Twitch-ID {ChannelTwitchId} weicht von der des Nutzers ({UserTwitchId}) ab.",
                normalizedChannel, channelTwitchId, principal.TwitchUserId);
        }

        return false;
    }
}
