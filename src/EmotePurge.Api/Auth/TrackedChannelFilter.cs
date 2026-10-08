using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

/// <summary>
/// 404 <c>channel_not_found</c> for a channel without a row, for every principal (#245). Registered
/// before the authorization filter on the channel audit log: after a purge the old row is gone, and
/// the login-based management check would otherwise let a same-named account read the previous
/// generation's entries. The loaded row is parked in <c>HttpContext.Items</c> so the handler can
/// bound the log by its <c>CreatedAt</c> without a second lookup.
/// </summary>
public class TrackedChannelFilter : IEndpointFilter
{
    internal const string ItemKey = "EmotePurge.TrackedChannel";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var channelName = context.HttpContext.Request.RouteValues["channelName"] as string;
        if (channelName is null || !ChannelNameValidation.IsValid(channelName))
        {
            return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
        }

        // Authentication is the group middleware; this guards the half-built session (authenticated,
        // claims incomplete) so it answers 401 like every other filter instead of a misleading 404.
        if (context.HttpContext.User.TryBuildTwitchPrincipal() is null)
        {
            return Results.Unauthorized();
        }

        var channelService = context.HttpContext.RequestServices.GetRequiredService<IChannelService>();
        var channel = await channelService.GetByNameAsync(channelName, context.HttpContext.RequestAborted);
        if (channel is null)
        {
            return Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound });
        }

        context.HttpContext.Items[ItemKey] = channel;
        return await next(context);
    }
}
