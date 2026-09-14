using EmotePurge.Core.SevenTv;

namespace EmotePurge.Api.Validation;

/// <summary>
/// Validates the <c>sortBy</c> query parameter on <c>GET /api/seventv/leaderboard</c> (7TV-leaderboard
/// spec 2026-09-13, section 4) before the handler runs — same pattern as
/// <see cref="ChannelNameValidationFilter"/>: the 400 contract belongs to a filter, pinned once here,
/// rather than to a hand-written check inside a handler that would already have resolved
/// <c>ISevenTvLeaderboardService</c> and everything behind it.
/// </summary>
/// <remarks>
/// <see cref="SevenTvLeaderboardSortWireCode.TryParse"/> is strict-ordinal: a missing, lowercased
/// (<c>trending_daily</c>) or unrecognized (<c>TRENDING_WEEKLY</c>) value all answer the same 400
/// here, and none of them ever reach the service — the whole point of running this as an endpoint
/// filter rather than a check inside the handler.
/// </remarks>
public class LeaderboardSortValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var sortBy = context.HttpContext.Request.Query["sortBy"].ToString();
        if (!SevenTvLeaderboardSortWireCode.TryParse(sortBy, out _))
        {
            return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidLeaderboardSort });
        }

        return await next(context);
    }
}
