using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

// Weaker than ChannelManagementAuthorizationFilter — additionally admits a channel's 7TV editors.
// Applied as a group filter to five endpoints across two groups (UsageStatsEndpoints: usage-stats,
// usage-stats/totals; EmoteEndpoints: sync-deleted, set-warning, active-set) — not just the two
// usage-stats read endpoints the name suggests.
//
// This deliberately includes sync-deleted (and its sync-restored neighbor), the write paths in
// that list: a 7TV editor can already delete emotes directly via 7TV's own permission system.
// Restore-per-set spec 5.6/E4 retired the row-changing legacy overload these two routes used to
// call — MarkDeletedAsync/MarkRestoredAsync now only count and audit, never flip IsArchived — so
// the self-heal this comment used to describe (the periodic resync resetting a wrongly set flag)
// no longer applies to what this filter guards: there is no flag left for it to reset. What
// remains at risk is the audit entry and the resync this filter's caller can trigger — a 7TV
// editor whose only power here is one already backed by 7TV's own permission system. Anything with
// real management semantics (join/leave, vote sessions, channel config) belongs behind the
// stricter ChannelManagementAuthorizationFilter instead.
public class UsageStatsAccessAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Full format check (not just non-empty) before any Redis/external-system access — an
        // unvalidated route value would otherwise reach ModRoleCache.BuildKey as a Redis key part.
        var channelName = context.HttpContext.Request.RouteValues["channelName"] as string;
        if (channelName is null || !ChannelNameValidation.IsValid(channelName))
        {
            return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
        }

        var principal = context.HttpContext.User.TryBuildTwitchPrincipal();
        if (principal is null)
        {
            return Results.Unauthorized();
        }

        var accessService = context.HttpContext.RequestServices.GetRequiredService<IChannelAccessService>();
        var allowed = await accessService.CanViewUsageStatsAsync(principal, channelName, context.HttpContext.RequestAborted);

        return allowed ? await next(context) : Results.Forbid();
    }
}
