using System.Security.Claims;
using EmotePurge.Api.Auth;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

public static class VoteSessionEndpoints
{
    public static void MapVoteSessionEndpoints(this WebApplication app)
    {
        // Ahead of every per-endpoint authorization filter — see ChannelNameValidationFilter. This also
        // closes the five endpoints that carried no check at all (end, delete, results, cast vote,
        // retract vote).
        var group = app.MapGroup("/api/channels/{channelName}/vote-sessions")
            .RequireAuthorization()
            .AddEndpointFilter<ChannelNameValidationFilter>();

        group.MapPost("", async (
            string channelName,
            CreateVoteSessionRequest request,
            HttpContext httpContext,
            IVoteSessionService voteSessionService,
            CancellationToken ct) =>
        {
            // Format check ahead of everything else (spec 6.9, E14): emoteSetId is a body field, not a
            // query/route parameter, so EmoteSetIdValidationFilter never sees it — same idiom as
            // SyncImportedRequest.TargetEmoteSetId (EmoteEndpoints.cs, AK 29), inline instead of a
            // shared filter.
            if (request.EmoteSetId is not null && !EmoteSetIdValidation.IsValid(request.EmoteSetId))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidEmoteSetId });
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            // Pure translation — the rules themselves live in VoteSessionService, which is the tested
            // layer and the one every non-HTTP caller goes through.
            var (result, session) = await voteSessionService.CreateAsync(
                new VoteSessionCreateRequest(
                    channelName, request.Title, request.AllowedVoterRoles, request.StartedAt, request.EmoteIds,
                    request.HideResultsUntilEnd, request.EmoteSetId, request.SevenTvEmoteIds),
                actor, ct);

            return result switch
            {
                CreateVoteSessionResult.Success => Results.Ok(ToSummaryDto(session!)),
                CreateVoteSessionResult.ChannelNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotJoined }),
                CreateVoteSessionResult.TitleEmpty => Results.BadRequest(new { errorCode = ApiErrorCodes.VoteSessionTitleEmpty }),
                CreateVoteSessionResult.RolesEmpty => Results.BadRequest(new { errorCode = ApiErrorCodes.VoteSessionRolesEmpty }),
                CreateVoteSessionResult.VipsNotSupported => Results.BadRequest(new { errorCode = ApiErrorCodes.VipsNotSupported }),
                CreateVoteSessionResult.StartedAtInFuture => Results.BadRequest(new { errorCode = ApiErrorCodes.StartedAtInFuture }),
                CreateVoteSessionResult.StartedAtTooFarBack => Results.BadRequest(
                    new { errorCode = ApiErrorCodes.RangeTooLarge, maxRangeDays = VoteSessionLimits.MaxBackdateDays }),
                CreateVoteSessionResult.EmoteIdsEmpty => Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdsEmpty }),
                CreateVoteSessionResult.EmoteIdsInvalid => Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdsInvalid }),
                CreateVoteSessionResult.SetBallotInvalid => Results.BadRequest(new { errorCode = ApiErrorCodes.VoteSessionSetBallotInvalid }),
                CreateVoteSessionResult.SevenTvUnavailable => Results.Json(
                    new { errorCode = ApiErrorCodes.ForeignChannelSevenTvUnavailable },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Problem()
            };
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>()
        // The three management mutations here (create, end, delete) had no policy at all until now.
        // Bookkeeping: writes against our own database with no provider cost, and 120 a minute is far
        // past anything a mod team does by hand.
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapPost("/{sessionId:long}/end", async (
            string channelName,
            long sessionId,
            HttpContext httpContext,
            IVoteSessionService voteSessionService,
            IRedisPublisher redisPublisher,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var session = await voteSessionService.EndAsync(channelName, sessionId, actor, ct);
            if (session is null)
            {
                return Results.NotFound();
            }

            // Same event a cast vote raises, and for a stronger reason: ending a session revokes
            // everyone's ability to vote, and on a secret ballot it unseals the tallies. Voters
            // sitting on the results page would otherwise keep looking at a session they believe
            // is still open until they reload by hand.
            await PublishVoteChangedAsync(redisPublisher, logger, channelName, sessionId);

            return Results.Ok(ToSummaryDto(session));
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapDelete("/{sessionId:long}", async (
            string channelName,
            long sessionId,
            HttpContext httpContext,
            IVoteSessionService voteSessionService,
            CancellationToken ct) =>
        {
            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var deleted = await voteSessionService.DeleteAsync(channelName, sessionId, actor, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapGet("", async (
            string channelName,
            int page,
            int pageSize,
            HttpContext httpContext,
            IVoteSessionQueryService voteSessionQueryService,
            IChannelAccessService channelAccessService,
            IVoteEligibilityService voteEligibilityService,
            CancellationToken ct) =>
        {
            var (effectivePage, effectivePageSize) = PagingQuery.Clamp(page, pageSize);

            var principal = httpContext.User.TryBuildTwitchPrincipal();
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            // Managers (admin/broadcaster/live-moderator) see every session unfiltered — same standing
            // as the "Neue Abstimmung erstellen" section — and get true DB-level pagination since no
            // per-session filter has to run first.
            if (await channelAccessService.CanManageChannelAsync(principal, channelName, ct))
            {
                var paged = await voteSessionQueryService.ListSessionsPagedAsync(channelName, effectivePage, effectivePageSize, ct);
                return Results.Ok(paged);
            }

            // Everyone else only sees sessions they're personally part of the audience for
            // (VoteEligibilityService.EvaluateAudienceAsync, same check used to gate the results page) —
            // a session a viewer has no rights to shouldn't even reveal its existence to them. Filter-
            // then-paginate (in memory, over the full per-channel list) rather than paginate-then-filter:
            // DB-side Skip/Take before filtering could return a page that shrinks to fewer than
            // pageSize (or zero) items after eligibility is applied even though more eligible sessions
            // exist further down — breaking the pagination contract and making TotalCount/TotalPages
            // meaningless. A channel's vote-session history is small (one broadcaster's own history,
            // not a global table), so the in-memory pass stays cheap.
            var allSessions = await voteSessionQueryService.ListSessionsAsync(channelName, ct);
            var visibleSessions = new List<VoteSessionSummaryDto>();
            foreach (var session in allSessions)
            {
                var eligibility = await voteEligibilityService.EvaluateAudienceAsync(principal, channelName, session.Id, ct);
                if (eligibility == VoteEligibilityResult.Allowed)
                {
                    visibleSessions.Add(session);
                }
            }

            var pageItems = visibleSessions.Skip((effectivePage - 1) * effectivePageSize).Take(effectivePageSize).ToList();
            return Results.Ok(new PagedResult<VoteSessionSummaryDto>(pageItems, effectivePage, effectivePageSize, visibleSessions.Count));
        })
        // A list a voter opens by navigating, so it belongs with the rest of navigation. The
        // non-manager branch above runs EvaluateAudienceAsync per session and every session with the
        // Subs flag triggers its own uncached Helix call — but that is a fan-out inside one request,
        // which a per-request budget cannot bound anyway; the caches in front of Helix can.
        .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        group.MapGet("/{sessionId:long}/results", async (
            string channelName,
            long sessionId,
            HttpContext httpContext,
            IVoteSessionQueryService voteSessionQueryService,
            IChannelAccessService channelAccessService,
            CancellationToken ct) =>
        {
            // VoteAudienceFilter already required an authenticated, in-audience principal to reach this
            // handler — anonymous viewing was removed (see decision log). MyVote is filled in from the
            // now-guaranteed-present principal.
            var principal = httpContext.User.TryBuildTwitchPrincipal()!;

            // One flag, two withholdings, both decided inside the read model (see GetResultsAsync):
            // raw per-emote chat usage is management data, and a session created as a secret ballot
            // shows no tallies to anyone else while it runs. CanManageChannelAsync is the same
            // boundary that gates creating and ending a session, so the manager who set the flag
            // keeps seeing through it.
            var viewerIsManager = await channelAccessService.CanManageChannelAsync(principal, channelName, ct);
            var results = await voteSessionQueryService.GetResultsAsync(channelName, sessionId, principal.TwitchUserId, viewerIsManager, ct);
            return results is null ? Results.NotFound() : Results.Ok(results);
        })
        .AddEndpointFilter<VoteAudienceFilter>()
        // The page a voter sits on, and the one every vote-changed live event makes them refetch.
        .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        group.MapPost("/{sessionId:long}/votes", async (
            string channelName,
            long sessionId,
            CastVoteRequest request,
            IVoteSessionService voteSessionService,
            IRedisPublisher redisPublisher,
            ILogger<Program> logger,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(request.EmoteId))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdEmpty });
            }

            if (request.Type is not (VoteType.Keep or VoteType.Delete))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidVoteType });
            }

            // VoteEligibilityFilter already required an authenticated principal to reach this handler.
            var twitchUserId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var (result, vote) = await voteSessionService.CastVoteAsync(channelName, sessionId, request.EmoteId, twitchUserId, request.Type, ct);

            if (result == VoteCastResult.Success)
            {
                await PublishVoteChangedAsync(redisPublisher, logger, channelName, sessionId);
            }

            return result switch
            {
                VoteCastResult.Success => Results.Ok(new { voteId = vote!.Id, emoteId = vote.EmoteId, type = vote.Type, updatedAt = vote.UpdatedAt }),
                VoteCastResult.ChannelNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
                VoteCastResult.SessionNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.VoteSessionNotFound }),
                VoteCastResult.SessionEnded => Results.Conflict(new { errorCode = ApiErrorCodes.VoteSessionEnded }),
                VoteCastResult.EmoteNotEligible => Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteNotEligible }),
                _ => Results.Problem()
            };
        })
        .AddEndpointFilter<VoteEligibilityFilter>()
        // Voting, whose partition is user *and* session: clicking through a large ballot is the one
        // burst in this app that is unquestionably legitimate, and it must not spend the budget that
        // navigation or a second session runs on.
        .RequireRateLimiting(RateLimitPolicyNames.Voting);

        group.MapDelete("/{sessionId:long}/votes/{emoteId}", async (
            string channelName,
            long sessionId,
            string emoteId,
            IVoteSessionService voteSessionService,
            IRedisPublisher redisPublisher,
            ILogger<Program> logger,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // VoteEligibilityFilter already required an authenticated principal to reach this handler.
            var twitchUserId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var result = await voteSessionService.RetractVoteAsync(channelName, sessionId, emoteId, twitchUserId, ct);

            if (result == VoteCastResult.Success)
            {
                await PublishVoteChangedAsync(redisPublisher, logger, channelName, sessionId);
            }

            return result switch
            {
                VoteCastResult.Success => Results.NoContent(),
                VoteCastResult.ChannelNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
                VoteCastResult.SessionNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.VoteSessionNotFound }),
                VoteCastResult.SessionEnded => Results.Conflict(new { errorCode = ApiErrorCodes.VoteSessionEnded }),
                _ => Results.Problem()
            };
        })
        .AddEndpointFilter<VoteEligibilityFilter>()
        // Voting rather than a read budget, deliberately: retracting is half of changing one's mind,
        // and a ballot the voter is reworking produces casts and retracts in the same burst. Splitting
        // the pair across two policies would let the cheaper-looking half be the one that fails.
        .RequireRateLimiting(RateLimitPolicyNames.Voting);

        // Deliberately NOT nested under /api/channels/{channelName}/... — a user's voting history spans
        // every channel they've ever voted in, so there's no single channelName route value to key off.
        app.MapGet("/api/vote-sessions/mine", async (
            int page,
            int pageSize,
            HttpContext httpContext,
            IVoteSessionQueryService voteSessionQueryService,
            CancellationToken ct) =>
        {
            var principal = httpContext.User.TryBuildTwitchPrincipal();
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var (effectivePage, effectivePageSize) = PagingQuery.Clamp(page, pageSize);

            var result = await voteSessionQueryService.ListMyVoteSessionsAsync(principal.TwitchUserId, effectivePage, effectivePageSize, ct);
            return Results.Ok(result);
        })
        .RequireAuthorization()
        // Registered outside the group above, so it also missed the group's policy — a paged read like
        // every other vote list, and now on the same one.
        .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);
    }

    // Maps a tracked VoteSession to its summary. Relies on the SessionEmotes collection being
    // populated (relationship fixup after create, explicit load in EndAsync) — 0 rows means a
    // dynamic "all emotes" session, reported as null.
    private static VoteSessionSummaryDto ToSummaryDto(VoteSession session) => new(
        session.Id, session.Title, session.AllowedVoterRoles, session.IsActive, session.StartedAt, session.EndedAt,
        session.SessionEmotes.Count == 0 ? null : session.SessionEmotes.Count, session.HideResultsUntilEnd, session.EmoteSetId);

    /// <summary>
    /// Announces "the tally of this session changed" to everyone watching it. Deliberately without
    /// any voter identity and without the new tally: who voted how is exactly what the vote UI must
    /// not leak, and the results read model is viewer- and role-specific (MyVote, includeRawUsage),
    /// so every subscriber refetches it through the existing endpoint under their own rights.
    /// <para>
    /// Published here in the endpoint rather than in VoteSessionService: the service is also the
    /// non-HTTP path, and the notification belongs to the request that produced it. Failure is
    /// logged and swallowed — the vote is already committed and the response must not change.
    /// </para>
    /// </summary>
    private static async Task PublishVoteChangedAsync(
        IRedisPublisher redisPublisher,
        ILogger logger,
        string channelName,
        long sessionId)
    {
        try
        {
            // No request token on purpose: the vote is committed, so a client that hung up right
            // after voting must not cost every *other* viewer their update.
            await redisPublisher.PublishAsync(
                LiveEvents.Channel,
                new LiveEvent(LiveEvents.VoteChanged, ChannelName.Normalize(channelName), sessionId).Serialize());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live-Event '{Type}' für Session {SessionId} konnte nicht veröffentlicht werden.", LiveEvents.VoteChanged, sessionId);
        }
    }
}

// EmoteIds null = the session covers all non-archived channel emotes dynamically; a non-null list
// becomes the session's fixed ballot (local emote guids, validated in VoteSessionService). Always
// null for a set-session (E4).
// HideResultsUntilEnd defaults to false, so an existing client that never sends it keeps creating
// open sessions.
// EmoteSetId/SevenTvEmoteIds (spec 6.9, additive): a set-session ballot by 7TV identity instead of
// local Emote guid. The exclusion rule between this pair and EmoteIds is VoteSessionService's, not
// this record's — see VoteSessionCreateRequest.
internal sealed record CreateVoteSessionRequest(
    string Title, AllowedRoles AllowedVoterRoles, DateTime? StartedAt = null, IReadOnlyList<string>? EmoteIds = null,
    bool HideResultsUntilEnd = false, string? EmoteSetId = null, IReadOnlyList<string>? SevenTvEmoteIds = null);
internal sealed record CastVoteRequest(string EmoteId, VoteType Type);
