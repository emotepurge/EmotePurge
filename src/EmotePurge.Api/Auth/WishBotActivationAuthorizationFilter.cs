using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

/// <summary>
/// Guards <c>POST /api/channels/{channelName}/wishes/bot/activate</c> on top of
/// <see cref="ChannelManagementAuthorizationFilter"/> (#487 plan 2.9, E8, E24, [W12]). In this order:
/// (1) <c>Wishes:Enabled</c> or <c>BotAccount:Enabled</c> off → through, so the handler answers 404
/// <c>wishes_disabled</c> or 409 <c>wish_bot_unavailable</c> without any 7TV call; (2) a global admin by id → through;
/// then <see cref="IWishBotService.CheckActivationAccessAsync"/>: (3) the channel not on the pilot list → 403
/// <c>wish_bot_channel_not_listed</c>; (4) the broadcaster by Twitch id → through; (5) a moderator with a confirmed 7TV
/// editor grant for the channel's Twitch id → through, no grant, no 7TV account or no Twitch id on the row → 403
/// <c>wish_bot_activation_forbidden</c>, 7TV unavailable → 503 <c>foreign_channel_seventv_unavailable</c>, never a pass.
/// The only authorization filter that answers 503.
/// </summary>
public class WishBotActivationAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        if (!services.GetRequiredService<WishOptions>().Enabled || !services.GetRequiredService<BotAccountOptions>().Enabled)
        {
            return await next(context);
        }

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

        if (services.GetRequiredService<IChannelAccessService>().IsGlobalAdminById(principal))
        {
            return await next(context);
        }

        var access = await services.GetRequiredService<IWishBotService>().CheckActivationAccessAsync(
            channelName, principal.TwitchUserId, context.HttpContext.RequestAborted);
        return access switch
        {
            WishBotActivationAccess.Allowed => await next(context),
            WishBotActivationAccess.NotListed =>
                Results.Json(new { errorCode = ApiErrorCodes.WishBotChannelNotListed }, statusCode: StatusCodes.Status403Forbidden),
            WishBotActivationAccess.EditorUnavailable =>
                Results.Json(new { errorCode = ApiErrorCodes.ForeignChannelSevenTvUnavailable }, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Json(new { errorCode = ApiErrorCodes.WishBotActivationForbidden }, statusCode: StatusCodes.Status403Forbidden),
        };
    }
}
