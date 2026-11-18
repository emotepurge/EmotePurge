using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

/// <summary>
/// Guards <c>PUT /api/channels/{channelName}/wishes/settings</c> on top of
/// <see cref="ChannelManagementAuthorizationFilter"/> (#481): in manual mode every manager may write, in bot
/// mode only the broadcaster, the manager who activated the bot and a global admin (403
/// <c>wish_settings_restricted</c>). The decision is <see cref="IWishService.CanChangeSettingsAsync"/>'s.
/// With <c>Wishes:Enabled</c> off the filter lets the request through, so the handler answers 404
/// <c>wishes_disabled</c> and a disabled feature never shows a settings-specific refusal.
/// </summary>
public class WishSettingsAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        if (!services.GetRequiredService<WishOptions>().Enabled)
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

        var isGlobalAdmin = services.GetRequiredService<IChannelAccessService>().IsGlobalAdmin(principal);
        var allowed = await services.GetRequiredService<IWishService>().CanChangeSettingsAsync(
            channelName, principal.TwitchUserId, isGlobalAdmin, context.HttpContext.RequestAborted);

        return allowed
            ? await next(context)
            : Results.Json(new { errorCode = ApiErrorCodes.WishSettingsRestricted }, statusCode: StatusCodes.Status403Forbidden);
    }
}
