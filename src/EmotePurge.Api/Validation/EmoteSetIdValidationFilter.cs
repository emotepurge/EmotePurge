namespace EmotePurge.Api.Validation;

/// <summary>
/// Validates the <c>emoteSetId</c> query parameter or route value (spec 2026-09-20, E14) on every
/// route that carries one — the set-vorschau's <c>?emoteSetId=</c> (6.4), the three
/// <c>/usage-stats/*</c> routes (6.5), <c>/emotes/set-warning</c> (6.8), and, as a route value, the
/// set-centric import's <c>/api/seventv/emote-sets/{emoteSetId}/sync-imported</c> (6.7). Register it
/// on the specific route, not the group: unlike <see cref="ChannelNameValidationFilter"/>'s
/// <c>channelName</c>, which every route in a channel-scoped group carries, <c>emoteSetId</c> is only
/// ever a handful of routes within a group that otherwise has nothing to do with it.
/// </summary>
/// <remarks>
/// The query parameter is optional: an absent key passes through untouched, exactly like
/// <see cref="ChannelNameValidationFilter"/> passes through a route with no <c>channelName</c> route
/// value. A key that <i>is</i> present but empty (<c>?emoteSetId=</c>) is rejected rather than treated
/// as "no filter" — "nicht leer" is part of the format contract itself (E14), not a separate rule, so
/// there is exactly one shape that counts as "no parameter": the key missing outright. A route value
/// named <c>emoteSetId</c>, in contrast, is never optional — the segment is always present by
/// construction — and is checked first: a route can carry the value in the path while a same-named
/// query key is absent, and checking the route first keeps a route-bound route's contract independent
/// of whatever the query string happens to hold.
/// </remarks>
public class EmoteSetIdValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.Request.RouteValues.TryGetValue("emoteSetId", out var routeValue))
        {
            if (routeValue is not string routeId || !EmoteSetIdValidation.IsValid(routeId))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidEmoteSetId });
            }

            return await next(context);
        }

        if (context.HttpContext.Request.Query.TryGetValue("emoteSetId", out var values))
        {
            var value = values.Count > 0 ? values[0] : null;
            if (value is null || !EmoteSetIdValidation.IsValid(value))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidEmoteSetId });
            }
        }

        return await next(context);
    }
}
