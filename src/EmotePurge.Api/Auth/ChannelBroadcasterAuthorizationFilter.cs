using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

/// <summary>
/// Admits only the channel's own broadcaster (#245): 400 for a malformed name, 401 without a usable
/// principal, 404 when no row exists, 403 when the row stores a Twitch id that is not the caller's.
/// No admin override and no moderator branch — the purge is the owner's alone. A row without a stored
/// id passes; the purge service then proves ownership live against Helix.
/// <para>
/// Only for a route whose service performs that live proof. A route without one (the data summary)
/// uses <see cref="ChannelBroadcasterSummaryAuthorizationFilter"/>, which decides id-less rows itself.
/// </para>
/// </summary>
public class ChannelBroadcasterAuthorizationFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        AuthorizeAsync(context, next, BroadcasterOwnership.MayAttempt);

    /// <summary>
    /// The steps both broadcaster filters share (400 → 401 → 404), followed by <paramref name="admits"/>
    /// as the 403 decision, so the two filters differ in exactly that one predicate.
    /// </summary>
    internal static async ValueTask<object?> AuthorizeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        Func<Channel, TwitchPrincipalInfo, bool> admits)
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

        return admits(channel, principal) ? await next(context) : Results.Forbid();
    }
}
