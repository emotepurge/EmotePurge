namespace EmotePurge.Api.Validation;

/// <summary>
/// Validates the optional <c>emoteSetId</c> query parameter (spec 2026-09-20, E14) on every route
/// that accepts one — the set-vorschau's <c>?emoteSetId=</c> (6.4), the three <c>/usage-stats/*</c>
/// routes (6.5) and <c>/emotes/set-warning</c> (6.8). Register it on the specific route, not the
/// group: unlike <see cref="ChannelNameValidationFilter"/>'s <c>channelName</c>, which every route in
/// a channel-scoped group carries, <c>emoteSetId</c> is only ever a handful of routes within a group
/// that otherwise has nothing to do with it.
/// </summary>
/// <remarks>
/// The parameter is optional: an absent key passes through untouched, exactly like
/// <see cref="ChannelNameValidationFilter"/> passes through a route with no <c>channelName</c> route
/// value. A key that <i>is</i> present but empty (<c>?emoteSetId=</c>) is rejected rather than treated
/// as "no filter" — "nicht leer" is part of the format contract itself (E14), not a separate rule, so
/// there is exactly one shape that counts as "no parameter": the key missing outright.
/// </remarks>
public class EmoteSetIdValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
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
