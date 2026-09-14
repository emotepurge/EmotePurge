using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// The read side of "Fremde Kanäle als Import-Quelle" (spec 2026-09-09): any logged-in user can read
/// any Twitch channel's active 7TV emote set, no role in that channel required. That is the whole
/// point of the feature, and the reason this group carries no
/// <c>UsageStatsAccessAuthorizationFilter</c> — the nearest existing precedent for "logged in,
/// cross-channel" is <see cref="LiveEndpoints.MapLiveEndpoints"/>'s two routes, not the emotes group
/// in <see cref="EmoteEndpoints"/>.
/// </summary>
public static class SevenTvEndpoints
{
    public static void MapSevenTvEndpoints(this WebApplication app)
    {
        // No /api/seventv/... group existed before this spec. Filter order here is a tested contract
        // (spec section 4, AK 14): UseAuthentication/UseAuthorization and UseRateLimiter are
        // middleware and run before any endpoint filter regardless of registration order below — so
        // an invalid channel name that has already exhausted the rate-limit budget answers 429, not
        // 400. ChannelNameValidationFilter still runs first among the *endpoint* filters, matching
        // every other channel-scoped group.
        var group = app.MapGroup("/api/seventv/channels/{channelName}/emotes")
            .RequireAuthorization()
            .AddEndpointFilter<ChannelNameValidationFilter>()
            .RequireRateLimiting(RateLimitPolicyNames.ForeignEmoteLookup);

        group.MapGet("", async (
            string channelName,
            IForeignEmoteSetService foreignEmoteSetService,
            CancellationToken ct,
            bool refresh = false) => // query string; a C# default is what makes minimal API treat it
                                     // as optional instead of answering a plain request with 400
        {
            // refresh=true (T2, spec E3) bypasses the hardening decorator's 60 s cache but still
            // passes through the same rate-limit policy and the same circuit breaker — no separate
            // policy was ever needed for it.
            var result = await foreignEmoteSetService.GetForeignEmoteSetAsync(channelName, refresh, ct);

            // Mirrors the state table in spec section 5 one-to-one. SevenTvRateLimited and
            // SevenTvUnavailable deliberately share a branch and a code: the table has one row for
            // "7TV nicht erreichbar / 429", because the caller cannot act on the two any differently.
            return result.Status switch
            {
                ForeignEmoteSetLookupStatus.Ok => Results.Ok(result.EmoteSet),
                ForeignEmoteSetLookupStatus.ChannelNotOnTwitch =>
                    Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotOnTwitch }),
                ForeignEmoteSetLookupStatus.TwitchUnavailable => Results.Json(
                    new { errorCode = ApiErrorCodes.ForeignChannelTwitchUnavailable },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                ForeignEmoteSetLookupStatus.NoSevenTvAccount =>
                    Results.NotFound(new { errorCode = ApiErrorCodes.ForeignChannelNoSevenTvAccount }),
                ForeignEmoteSetLookupStatus.NoActiveEmoteSet =>
                    Results.NotFound(new { errorCode = ApiErrorCodes.ForeignChannelNoActiveEmoteSet }),
                ForeignEmoteSetLookupStatus.SevenTvUnavailable
                    or ForeignEmoteSetLookupStatus.SevenTvRateLimited
                    // Our own provider-wide budget refusing a permit is invisible to the caller by
                    // design: "try again shortly" is the same advice, and a code of its own would
                    // leak an internal throttle into the public vocabulary (Regel 7) for no gain.
                    or ForeignEmoteSetLookupStatus.ProviderBudgetExhausted => Results.Json(
                    new { errorCode = ApiErrorCodes.ForeignChannelSevenTvUnavailable },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(result), result.Status, "Unbekannter ForeignEmoteSetLookupStatus.")
            };
        });

        // GET /api/seventv/leaderboard (7TV-leaderboard-as-import-source spec 2026-09-13, section 4):
        // a network-wide ranking, not scoped to any channel — RequireAuthorization() only, no
        // ChannelNameValidationFilter (there is no channel name here at all) and no
        // UsageStatsAccessAuthorizationFilter (there is no role to check). The same middleware-before-
        // filter ordering as the group above applies: RequireRateLimiting still runs as middleware
        // before LeaderboardSortValidationFilter can, so a caller over budget gets 429 even with an
        // invalid sortBy.
        var leaderboardGroup = app.MapGroup("/api/seventv/leaderboard")
            .RequireAuthorization()
            .AddEndpointFilter<LeaderboardSortValidationFilter>()
            .RequireRateLimiting(RateLimitPolicyNames.SevenTvLeaderboard);

        leaderboardGroup.MapGet("", async (
            string? sortBy,
            ISevenTvLeaderboardService leaderboardService,
            CancellationToken ct) =>
        {
            if (!SevenTvLeaderboardSortWireCode.TryParse(sortBy, out var sort))
            {
                // LeaderboardSortValidationFilter has already rejected anything outside the allowlist
                // by the time this handler runs — reaching here with an unparsable sortBy would be a
                // bug in that filter, not something a caller can trigger.
                throw new InvalidOperationException(
                    "LeaderboardSortValidationFilter should have rejected this sortBy before the handler ran.");
            }

            var result = await leaderboardService.GetLeaderboardAsync(sort, ct);

            // Spec section 5: all three failure states share the existing ForeignChannelSevenTvUnavailable
            // code (E13) — the caller cannot act on "7TV unreachable", "7TV rate-limited" and "our own
            // window budget refused a permit" any differently.
            return result.Status switch
            {
                SevenTvLeaderboardStatus.Ok => Results.Ok(result.Response),
                SevenTvLeaderboardStatus.SevenTvUnavailable
                    or SevenTvLeaderboardStatus.SevenTvRateLimited
                    or SevenTvLeaderboardStatus.BudgetRefused => Results.Json(
                    new { errorCode = ApiErrorCodes.ForeignChannelSevenTvUnavailable },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(result), result.Status, "Unknown SevenTvLeaderboardStatus.")
            };
        });
    }
}
