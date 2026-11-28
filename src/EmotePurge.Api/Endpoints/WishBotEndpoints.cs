using EmotePurge.Api.Auth;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// The channel routes of the wish bot mode (#487 plan 2.9) under <c>/api/channels/{channelName}/wishes/bot</c>, mapped
/// onto the wish group, so the group's ladder (400 name, 401/403 manager) runs first. Handlers only translate; the
/// service decides. <c>GET</c> reads, <c>activate</c> sits behind <see cref="WishBotActivationAuthorizationFilter"/>
/// (E24) and records a request the worker confirms (202), <c>DELETE</c> sits behind
/// <see cref="WishSettingsAuthorizationFilter"/> (D7) and switches the bot off (204), also with <c>BotAccount:Enabled</c>
/// off. The literal <c>bot</c> never collides with <c>{id:long}</c>.
/// </summary>
internal static class WishBotEndpoints
{
    public static void MapWishBotEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/bot", async (
            string channelName,
            HttpContext httpContext,
            WishOptions options,
            IWishBotService wishBot,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            var principal = httpContext.User.TryBuildTwitchPrincipal();
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var caller = new WishBotCaller(
                principal.TwitchUserId, channelAccess.IsGlobalAdmin(principal), channelAccess.IsGlobalAdminById(principal));
            var state = await wishBot.GetChannelAsync(channelName, caller, ct);
            return state is null
                ? Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound })
                : Results.Ok(state);
        })
        .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        group.MapPost("/bot/activate", async (
            string channelName,
            HttpContext httpContext,
            WishOptions options,
            IWishBotService wishBot,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var result = await wishBot.ActivateAsync(
                channelName, actor, WishBotActivationAuthorizationFilter.CheckedTwitchChannelIdOf(httpContext), ct);
            return result.IsSuccess
                ? Results.Json(new { activationPending = true }, statusCode: StatusCodes.Status202Accepted)
                : WishEndpoints.MapResult(result, StatusCodes.Status202Accepted);
        })
        .AddEndpointFilter<WishBotActivationAuthorizationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapDelete("/bot", async (
            string channelName,
            HttpContext httpContext,
            WishOptions options,
            IWishBotService wishBot,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            var principal = httpContext.User.TryBuildTwitchPrincipal();
            var actor = httpContext.User.TryBuildAuditActor();
            if (principal is null || actor is null)
            {
                return Results.Unauthorized();
            }

            var result = await wishBot.DeactivateAsync(channelName, actor, channelAccess.IsGlobalAdmin(principal), ct);
            return result.IsSuccess ? Results.NoContent() : WishEndpoints.MapResult(result, StatusCodes.Status204NoContent);
        })
        .AddEndpointFilter<WishSettingsAuthorizationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);
    }

    private static IResult WishesDisabled() =>
        Results.NotFound(new { errorCode = ApiErrorCodes.WishesDisabled });
}
