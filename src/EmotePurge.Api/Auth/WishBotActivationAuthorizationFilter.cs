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
/// The only authorization filter that answers 503. A pass after (3) to (5) leaves the checked channel's Twitch id for the
/// handler (<see cref="CheckedTwitchChannelIdOf"/>), which binds the write to it under the channel lock.
/// </summary>
public class WishBotActivationAuthorizationFilter : IEndpointFilter
{
    private static readonly object CheckedTwitchChannelIdKey = new();

    /// <summary>
    /// The Twitch id the filter's steps (3) to (5) were passed for, an empty string when no channel had the name; null
    /// when the filter checked no channel (a switch off, a global admin by id).
    /// </summary>
    public static string? CheckedTwitchChannelIdOf(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return httpContext.Items.TryGetValue(CheckedTwitchChannelIdKey, out var value) ? value as string : null;
    }

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

        var check = await services.GetRequiredService<IWishBotService>().CheckActivationAccessAsync(
            channelName, principal.TwitchUserId, context.HttpContext.RequestAborted);
        if (check.Access == WishBotActivationAccess.Allowed)
        {
            context.HttpContext.Items[CheckedTwitchChannelIdKey] = check.TwitchChannelId ?? string.Empty;
            return await next(context);
        }

        return check.Access switch
        {
            WishBotActivationAccess.NotListed =>
                Results.Json(new { errorCode = ApiErrorCodes.WishBotChannelNotListed }, statusCode: StatusCodes.Status403Forbidden),
            WishBotActivationAccess.EditorUnavailable =>
                Results.Json(new { errorCode = ApiErrorCodes.ForeignChannelSevenTvUnavailable }, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Json(new { errorCode = ApiErrorCodes.WishBotActivationForbidden }, statusCode: StatusCodes.Status403Forbidden),
        };
    }
}
