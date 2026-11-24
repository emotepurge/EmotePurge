using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

/// <summary>
/// Guards the route that deletes an ended wish (wish emotes K7a, spec E39, E41) on top of
/// <see cref="ChannelManagementAuthorizationFilter"/>. The manager filter decides on the login for moderators, but a
/// hard, irreversible delete works on the stored channel row, so the caller must be confirmed on that row's Twitch id
/// (<see cref="IChannelAccessService.ResolveWishCaptureAccessAsync"/>). A moderator of a channel that took over a
/// released login never reaches the old row's wishes.
/// <para>
/// Like <see cref="WishCaptureAccessFilter"/>, but without the capture switch and without the read exemption. In this
/// order: <c>Wishes:Enabled</c> off → 404 <c>wishes_disabled</c>; no channel row → 404 <c>channel_not_found</c>; a row
/// without a <c>TwitchChannelId</c> → 409 <c>wish_channel_unbound</c>; not confirmed on the id → 403 in the form of the
/// manager filter. Otherwise the answer goes into <see cref="HttpContext.Items"/> (<see cref="GetAccess"/>) and the
/// handler passes it on unchanged; the service checks the binding again behind its lock. No role filter follows: every
/// manager may delete, in bot mode too (E39).
/// </para>
/// </summary>
public sealed class WishIdAccessFilter : IEndpointFilter
{
    private static readonly object AccessItemKey = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var services = httpContext.RequestServices;
        if (!services.GetRequiredService<WishOptions>().Enabled)
        {
            return Results.NotFound(new { errorCode = ApiErrorCodes.WishesDisabled });
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
            return Results.Conflict(new { errorCode = ApiErrorCodes.WishChannelUnbound });
        }

        if (!access.IdConfirmed)
        {
            return Results.Forbid();
        }

        httpContext.Items[AccessItemKey] = access;
        return await next(context);
    }

    /// <summary>The access this filter resolved for the request; throws when the route was mapped without the filter.</summary>
    public static WishCaptureAccess GetAccess(HttpContext httpContext) =>
        httpContext.Items[AccessItemKey] as WishCaptureAccess
        ?? throw new InvalidOperationException($"The route was mapped without {nameof(WishIdAccessFilter)}.");
}
