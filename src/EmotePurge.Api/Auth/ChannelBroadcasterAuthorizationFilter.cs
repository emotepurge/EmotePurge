using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

/// <summary>
/// Admits only the channel's own broadcaster (#245): 400 for a malformed name, 401 without a usable
/// principal, 404 when no row exists, 403 when the row stores a Twitch id that is not the caller's.
/// No admin override and no moderator branch — the purge and its summary are the owner's alone. A row
/// without a stored id passes; the service then proves ownership live against Helix.
/// </summary>
public class ChannelBroadcasterAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
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

        var channelService = context.HttpContext.RequestServices.GetRequiredService<IChannelService>();
        var channel = await channelService.GetByNameAsync(channelName, context.HttpContext.RequestAborted);
        if (channel is null)
        {
            return Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound });
        }

        return BroadcasterOwnership.MayAttempt(channel, principal) ? await next(context) : Results.Forbid();
    }
}
