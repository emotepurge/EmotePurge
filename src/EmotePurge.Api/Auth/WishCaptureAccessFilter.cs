using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

/// <summary>
/// Guards the reward routes of the chat capture (<c>…/wishes/rewards…</c>, wish emotes K6, plan #486 2.4a, 2.9) on top
/// of <see cref="ChannelManagementAuthorizationFilter"/>, and in front of <see cref="WishSettingsAuthorizationFilter"/>
/// on the writes. The manager filter decides on the login for moderators; K6 data is decided on the channel row's
/// Twitch id only (<see cref="IChannelAccessService.ResolveWishCaptureAccessAsync"/>), so a moderator of a channel
/// that took over a released login never reaches the old row's rewards, candidate or gaps.
/// <para>
/// In this order: <c>Wishes:Enabled</c> off → 404 <c>wishes_disabled</c>; the Api's capture switch off
/// (<see cref="WishOptions.CaptureActive"/>) → 404 <c>wish_capture_disabled</c>. The filter answers both itself,
/// because it is the first route filter after the manager filter: the settings filter behind it would otherwise
/// show a settings-specific refusal for a switched-off feature. Then no channel row → 404 <c>channel_not_found</c>;
/// a row without a <c>TwitchChannelId</c> → 409 <c>wish_channel_unbound</c>, except on a read, which goes on and
/// answers <c>channelBound: false</c> without any K6 data (plan 2.4); a caller not confirmed on the id → 403 in the
/// form of the manager filter. Otherwise the answer goes into <see cref="HttpContext.Items"/>, where the handler
/// takes it (<see cref="GetAccess"/>) and passes it on to the service unchanged; the service checks the binding
/// again behind its lock.
/// </para>
/// </summary>
public sealed class WishCaptureAccessFilter : IEndpointFilter
{
    private static readonly object AccessItemKey = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var services = httpContext.RequestServices;
        var options = services.GetRequiredService<WishOptions>();
        if (!options.Enabled)
        {
            return Results.NotFound(new { errorCode = ApiErrorCodes.WishesDisabled });
        }

        if (!options.CaptureActive)
        {
            return Results.NotFound(new { errorCode = ApiErrorCodes.WishCaptureDisabled });
        }

        var channelName = httpContext.Request.RouteValues["channelName"] as string;
        if (channelName is null || !ChannelNameValidation.IsValid(channelName))
        {
            return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
        }

        var principal = httpContext.User.TryBuildTwitchPrincipal();
        if (principal is null)
        {
            return Results.Unauthorized();
        }

        var access = await services.GetRequiredService<IChannelAccessService>()
            .ResolveWishCaptureAccessAsync(principal, channelName, httpContext.RequestAborted);
        if (access is null)
        {
            return Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound });
        }

        if (!access.IsBound)
        {
            if (!HttpMethods.IsGet(httpContext.Request.Method))
            {
                return Results.Conflict(new { errorCode = ApiErrorCodes.WishChannelUnbound });
            }
        }
        else if (!access.IdConfirmed)
        {
            return Results.Forbid();
        }

        httpContext.Items[AccessItemKey] = access;
        return await next(context);
    }

    /// <summary>The access this filter resolved for the request; throws when the route was mapped without the filter.</summary>
    public static WishCaptureAccess GetAccess(HttpContext httpContext) =>
        httpContext.Items[AccessItemKey] as WishCaptureAccess
        ?? throw new InvalidOperationException($"The route was mapped without {nameof(WishCaptureAccessFilter)}.");
}
