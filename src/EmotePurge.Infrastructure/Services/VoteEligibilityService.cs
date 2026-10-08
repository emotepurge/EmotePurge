using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class VoteEligibilityService(
    AppDbContext db,
    IChannelAccessService channelAccessService,
    IModRoleCache modRoleCache,
    ITwitchHelixClient helixClient,
    ITwitchUserTokenService userTokenService,
    IRateLimitTelemetry telemetry,
    ILogger<VoteEligibilityService> logger) : IVoteEligibilityService
{
    public async Task<VoteEligibilityResult> EvaluateAsync(
        TwitchPrincipalInfo principal, string channelName, long sessionId, CancellationToken cancellationToken = default)
    {
        var normalizedChannel = ChannelName.Normalize(channelName);
        var (channel, session) = await db.LoadChannelSessionAsync(normalizedChannel, sessionId, cancellationToken);
        if (channel is null || session is null)
        {
            return VoteEligibilityResult.SessionNotFound;
        }

        if (!session.IsActive)
        {
            return VoteEligibilityResult.SessionEnded;
        }

        return await EvaluateRoleAsync(principal, normalizedChannel, channel, session, cancellationToken);
    }

    public async Task<VoteEligibilityResult> EvaluateAudienceAsync(
        TwitchPrincipalInfo principal, string channelName, long sessionId, CancellationToken cancellationToken = default)
    {
        var normalizedChannel = ChannelName.Normalize(channelName);
        var (channel, session) = await db.LoadChannelSessionAsync(normalizedChannel, sessionId, cancellationToken);
        if (channel is null || session is null)
        {
            return VoteEligibilityResult.SessionNotFound;
        }

        return await EvaluateRoleAsync(principal, normalizedChannel, channel, session, cancellationToken);
    }

    private async Task<VoteEligibilityResult> EvaluateRoleAsync(
        TwitchPrincipalInfo principal, string normalizedChannel, Channel channel, VoteSession session, CancellationToken cancellationToken)
    {
        var roles = session.AllowedVoterRoles;

        if (roles.HasFlag(AllowedRoles.Everyone))
        {
            return VoteEligibilityResult.Allowed;
        }

        // Admin/broadcaster/mods can always vote in their own channel's sessions, regardless of which
        // roles a session otherwise targets — same "channel management" precedence as join/leave, not
        // gated behind the AllowedVoterRoles flags like Subs/Everyone. Delegated to
        // IChannelAccessService rather than re-implemented: the local copy had no admin branch, so a
        // global admin could list a session and then be bounced off its own results page, and "who
        // manages this channel?" answered differently in two places with nothing pointing that out.
        if (await channelAccessService.CanManageChannelAsync(principal, normalizedChannel, cancellationToken))
        {
            return VoteEligibilityResult.Allowed;
        }

        if (roles.HasFlag(AllowedRoles.Subs))
        {
            if (channel.TwitchChannelId is null)
            {
                logger.LogInformation(
                    "Sub-Check für {User}/{Channel} übersprungen: TwitchChannelId fehlt.",
                    principal.TwitchUserId, normalizedChannel);
            }
            else if (await IsSubscriberAsync(principal, channel.TwitchChannelId, cancellationToken))
            {
                return VoteEligibilityResult.Allowed;
            }
        }

        return VoteEligibilityResult.RoleNotEligible;
    }

    private async Task<bool> IsSubscriberAsync(TwitchPrincipalInfo principal, string broadcasterTwitchId, CancellationToken cancellationToken)
    {
        // Cached, because the session list evaluates eligibility per session: a channel with ten Subs
        // sessions used to fire ten identical Helix calls (10s timeout each) for a single page view.
        var cached = await modRoleCache.TryGetIsSubscriberAsync(principal.TwitchUserId, broadcasterTwitchId, cancellationToken);
        // Counted per lookup, not per page view: one channel with ten Subs sessions asks ten times,
        // and the nine hits are exactly the Helix calls this cache exists to remove.
        telemetry.ReportCacheLookup(RateLimitCacheNames.SubscriberCheck, cached is not null);
        if (cached is { } isSubscriberCached)
        {
            return isSubscriberCached;
        }

        // After the cache check on purpose — a cache hit must never cost a token refresh.
        var token = await userTokenService.GetValidAccessTokenAsync(principal, cancellationToken);
        if (token.AccessToken is null)
        {
            logger.LogInformation(
                "Sub-Check für {User}/{Broadcaster} übersprungen: kein gültiger Access Token.",
                principal.TwitchUserId, broadcasterTwitchId);
            return false;
        }

        var isSubscribed = await helixClient.GetUserSubscriptionStatusAsync(
            token.AccessToken, broadcasterTwitchId, principal.TwitchUserId, cancellationToken);

        // null is "Helix could not tell us" (rate limit, outage, expired token) — deliberately not
        // cached, otherwise a transient 429 would silently drop a subscriber's sessions off their list
        // for the full TTL with no error anywhere.
        if (isSubscribed is null)
        {
            logger.LogInformation(
                "Sub-Status für {User}/{Broadcaster} von Twitch nicht ermittelbar — wird nicht gecacht.",
                principal.TwitchUserId, broadcasterTwitchId);
            return false;
        }

        await modRoleCache.SetIsSubscriberAsync(principal.TwitchUserId, broadcasterTwitchId, isSubscribed.Value, cancellationToken);
        return isSubscribed.Value;
    }
}
