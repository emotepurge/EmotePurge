using System.Diagnostics;
using EmotePurge.Api.Auth;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// Wish emotes (#481, K1): the manager's routes per channel and the cross-channel overview. Handlers only
/// translate: the switch is checked first (404 <c>wishes_disabled</c>, after the authorization filters, so a
/// caller who may not manage the channel never learns whether the feature exists), then the body, then the
/// service decides and <see cref="MapResult"/> turns its outcome into a status and an error code.
/// <para>
/// K6 (plan #486 2.9): <c>approve</c>, <c>reject</c> and <c>unlink</c> need only <c>Wishes:Enabled</c>; the reward
/// routes under <c>…/wishes/rewards</c> carry <see cref="WishCaptureAccessFilter"/>, which also answers 404
/// <c>wish_capture_disabled</c> while the Api's capture switch is off. Every handler whose answer carries a wish asks
/// <see cref="IChannelAccessService.ResolveWishCaptureAccessAsync"/> and passes the answer on, so redeemer data leaves
/// only for a caller confirmed on the channel's Twitch id (2.4a).
/// </para>
/// </summary>
public static class WishEndpoints
{
    public static void MapWishEndpoints(this WebApplication app)
    {
        // Filter order is the error contract: 400 (name) before 401/403 (management) before the handler's 404.
        var group = app.MapGroup("/api/channels/{channelName}/wishes")
            .RequireAuthorization()
            .AddEndpointFilter<ChannelNameValidationFilter>()
            .AddEndpointFilter<ChannelManagementAuthorizationFilter>();

        group.MapGet("", async (
            string channelName,
            string? view,
            string? page,
            string? pageSize,
            HttpContext httpContext,
            WishOptions options,
            IWishService wishService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            if (!TryParseView(view, out var parsedView))
            {
                return InvalidBody();
            }

            var (effectivePage, effectivePageSize) = PagingQuery.Clamp(page, pageSize);
            var access = await ResolveAccessAsync(httpContext, channelName, channelAccess, ct);
            var result = await wishService.ListAsync(channelName, parsedView, effectivePage, effectivePageSize, access, ct);
            return result is null
                ? Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound })
                : Results.Ok(result);
        })
        .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        // Literal routes (settings, adopt) sit next to {id:long}; the constraint keeps a literal from ever
        // being read as an id and a non-numeric id from reaching a handler.
        group.MapGet("/settings", async (
            string channelName,
            HttpContext httpContext,
            WishOptions options,
            IWishService wishService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            var principal = httpContext.User.TryBuildTwitchPrincipal();
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var settings = await wishService.GetSettingsAsync(channelName, ct);
            if (settings is null)
            {
                return Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound });
            }

            // canEdit is the decision WishSettingsAuthorizationFilter takes for the PUT, asked the same way.
            var canEdit = await wishService.CanChangeSettingsAsync(
                channelName, principal.TwitchUserId, channelAccess.IsGlobalAdmin(principal), ct);
            return Results.Ok(new WishSettingsReadDto(settings, canEdit));
        })
        .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        // The entries "take over from the set" may pick (plan #485 2.1a), with the conflict adopt would decide.
        group.MapGet("/slots", async (
            string channelName,
            string? sevenTvEmoteId,
            WishOptions options,
            IWishService wishService,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            var slots = await wishService.ListAdoptableSlotsAsync(channelName, sevenTvEmoteId, ct);
            return slots is null
                ? Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound })
                : Results.Ok(slots);
        })
        .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        group.MapPut("/settings", async (
            string channelName,
            UpdateWishSettingsBody? request,
            HttpContext httpContext,
            WishOptions options,
            IWishService wishService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            if (request is null)
            {
                return InvalidBody();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            return actor is null
                ? Results.Unauthorized()
                : MapResult(
                    await wishService.UpdateSettingsAsync(
                        channelName, request.ToRequest(), actor, IsGlobalAdmin(httpContext, channelAccess), ct),
                    StatusCodes.Status200OK);
        })
        .AddEndpointFilter<WishSettingsAuthorizationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapGet("/{id:long}", async (
            string channelName,
            long id,
            HttpContext httpContext,
            WishOptions options,
            IWishService wishService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            var access = await ResolveAccessAsync(httpContext, channelName, channelAccess, ct);
            var detail = await wishService.GetAsync(channelName, id, access, ct);
            return detail is null
                ? Results.NotFound(new { errorCode = ApiErrorCodes.WishNotFound })
                : Results.Ok(detail);
        })
        .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        group.MapPost("", async (
            string channelName,
            CreateWishRequest? request,
            HttpContext httpContext,
            WishOptions options,
            IWishService wishService,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            if (request is null)
            {
                return InvalidBody();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            return actor is null
                ? Results.Unauthorized()
                : MapResult(await wishService.CreateAsync(channelName, request, actor, ct), StatusCodes.Status201Created);
        })
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapPost("/adopt", async (
            string channelName,
            AdoptWishRequest? request,
            HttpContext httpContext,
            WishOptions options,
            IWishService wishService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            if (request is null)
            {
                return InvalidBody();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var access = await ResolveAccessAsync(httpContext, channelName, channelAccess, ct);
            return MapResult(await wishService.AdoptAsync(channelName, request, actor, access, ct), StatusCodes.Status201Created);
        })
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapPatch("/{id:long}", async (
            string channelName,
            long id,
            UpdateWishRequest? request,
            HttpContext httpContext,
            WishOptions options,
            IWishService wishService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            if (request is null)
            {
                return InvalidBody();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var access = await ResolveAccessAsync(httpContext, channelName, channelAccess, ct);
            return MapResult(await wishService.UpdateAsync(channelName, id, request, actor, access, ct), StatusCodes.Status200OK);
        })
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapPost("/{id:long}/re-add", async (
            string channelName,
            long id,
            ReAddWishRequest? request,
            HttpContext httpContext,
            WishOptions options,
            IWishService wishService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            if (request is null)
            {
                return InvalidBody();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var access = await ResolveAccessAsync(httpContext, channelName, channelAccess, ct);
            return MapResult(await wishService.ReAddAsync(channelName, id, request, actor, access, ct), StatusCodes.Status200OK);
        })
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        // K7a (spec E34, E38, E39, E41, E44): deletes an ended wish. `endedAtUtc` is the episode the confirmation showed,
        // copied unchanged from the wish's DTO; a missing or unreadable value is a 400, a differing one a 409 from the
        // service. WishIdAccessFilter answers the switch (404) and the id-bound access (404/409/403) first, so the
        // query is read only for a confirmed caller. Success is a 204 without a body, which MapResult cannot express
        // (a success without wish or settings is unreachable there).
        group.MapDelete("/{id:long}", async (
            string channelName,
            long id,
            string? endedAtUtc,
            HttpContext httpContext,
            IWishService wishService,
            CancellationToken ct) =>
        {
            if (!DateTime.TryParse(
                    endedAtUtc,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var expectedEndedAtUtc))
            {
                return InvalidBody();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var result = await wishService.DeleteAsync(
                channelName, id, expectedEndedAtUtc, actor, WishIdAccessFilter.GetAccess(httpContext), ct);
            return result.Outcome == WishOutcome.Success
                ? Results.NoContent()
                : MapResult(result, StatusCodes.Status200OK);
        })
        .AddEndpointFilter<WishIdAccessFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        MapTransition(group, "/{id:long}/extend", (s, c, id, a, access, ct) => s.ExtendAsync(c, id, a, access, ct));
        MapTransition(group, "/{id:long}/keep", (s, c, id, a, access, ct) => s.KeepAsync(c, id, a, access, ct));
        MapTransition(group, "/{id:long}/close", (s, c, id, a, access, ct) => s.CloseAsync(c, id, a, access, ct));
        MapTransition(group, "/{id:long}/retry", (s, c, id, a, access, ct) => s.RetryAsync(c, id, a, access, ct));
        // Plan #485 2.1a: the report names slot, generation and set, so it has a body and a handler of its own.
        group.MapPost("/{id:long}/complete-removal", async (
            string channelName,
            long id,
            CompleteWishRemovalRequest? request,
            HttpContext httpContext,
            WishOptions options,
            IWishService wishService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            if (request is null)
            {
                return InvalidBody();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var access = await ResolveAccessAsync(httpContext, channelName, channelAccess, ct);
            return MapResult(await wishService.CompleteRemovalAsync(channelName, id, request, actor, access, ct), StatusCodes.Status200OK);
        })
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        // K1 registers the route; the command refuses every state until K4 gives it its one transition. It answers no
        // wish, so it asks no capture access.
        MapTransition(group, "/{id:long}/recheck", (s, c, id, a, _, ct) => s.RecheckAsync(c, id, a, ct), resolveAccess: false);

        MapCaptureCommands(group);
        MapRewardEndpoints(group);

        // The cross-channel side (E18). Neither read goes through IMyChannelsService.
        var overview = app.MapGroup("/api/wishes")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        overview.MapGet("/mine", async (
            HttpContext httpContext,
            WishOptions options,
            IWishOverviewService overviewService,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            var principal = httpContext.User.TryBuildTwitchPrincipal();
            return principal is null
                ? Results.Unauthorized()
                : Results.Ok(await overviewService.ListMineAsync(principal, ct));
        });

        overview.MapGet("/summary", async (
            HttpContext httpContext,
            WishOptions options,
            IWishOverviewService overviewService,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            var principal = httpContext.User.TryBuildTwitchPrincipal();
            return principal is null
                ? Results.Unauthorized()
                : Results.Ok(new { actionCount = await overviewService.CountActionAsync(principal, ct) });
        });
    }

    /// <summary>
    /// The one place an outcome becomes a status and an error code. Public so the mapping test can run every
    /// <see cref="WishOutcome"/> through it. Outcomes without a code of their own reuse existing ones:
    /// <see cref="WishOutcome.EmoteIdInvalid"/> is <c>emote_ids_invalid</c>, and <see cref="WishOutcome.StartsAtInvalid"/>,
    /// <see cref="WishOutcome.ModeInvalid"/> and <see cref="WishOutcome.RequestInvalid"/> are <c>invalid_request_body</c>.
    /// The conflicts that concern another wish name it as <c>wishId</c> (plan #485 2.1a, and K6's
    /// <c>wish_running_not_extendable</c>; null when
    /// <c>wish_emote_already_in_set</c> has no running wish). <see cref="WishOutcome.AccessDenied"/> (K7a, spec E41) is a
    /// 403 without a body in the form of the manager filter, like <see cref="WishRewardOutcome.AccessDenied"/>: the
    /// service's second wall when the channel row's Twitch id changed after the access filter decided.
    /// </summary>
    internal static IResult MapResult(WishCommandResult result, int successStatus)
    {
        switch (result.Outcome)
        {
            case WishOutcome.Success:
                if (result.Settings is { } settings)
                {
                    return Results.Ok(settings);
                }

                if (result.Wish is not { } wish)
                {
                    throw new UnreachableException("A successful wish command carries a wish or settings.");
                }

                // The manual entry carries the capacity hint next to the wish (spec F2); every other
                // command answers the plain wish.
                return successStatus == StatusCodes.Status201Created && result.SetFull
                    ? Results.Json(new CreatedWishDto(wish, SetFull: true), statusCode: successStatus)
                    : Results.Json(wish, statusCode: successStatus);
            case WishOutcome.ChannelNotFound:
                return Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound });
            case WishOutcome.WishNotFound:
                return Results.NotFound(new { errorCode = ApiErrorCodes.WishNotFound });
            case WishOutcome.EmoteIdInvalid:
                return Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdsInvalid });
            case WishOutcome.DurationOutOfRange:
                return Results.BadRequest(new { errorCode = ApiErrorCodes.WishDurationOutOfRange });
            case WishOutcome.AliasInvalid:
                return Results.BadRequest(new { errorCode = ApiErrorCodes.WishAliasInvalid });
            case WishOutcome.RequesterInvalid:
                return Results.BadRequest(new { errorCode = ApiErrorCodes.WishRequesterInvalid });
            case WishOutcome.SettingsOutOfRange:
                return Results.BadRequest(new { errorCode = ApiErrorCodes.WishSettingsOutOfRange });
            case WishOutcome.StartsAtInvalid:
            case WishOutcome.ModeInvalid:
            case WishOutcome.RequestInvalid:
                return InvalidBody();
            case WishOutcome.EmoteAlreadyInSet:
                return Results.Conflict(new
                {
                    errorCode = ApiErrorCodes.WishEmoteAlreadyInSet,
                    slotIds = result.SlotIds ?? [],
                    wishId = result.ConflictingWishId
                });
            case WishOutcome.AlreadyRunning:
                return Results.Conflict(new { errorCode = ApiErrorCodes.WishAlreadyRunning, wishId = result.ConflictingWishId });
            case WishOutcome.SlotNotFound:
                return Results.NotFound(new { errorCode = ApiErrorCodes.WishSlotNotFound });
            case WishOutcome.SlotAlreadyLinked:
                return Results.Conflict(new { errorCode = ApiErrorCodes.WishSlotAlreadyLinked, wishId = result.ConflictingWishId });
            case WishOutcome.StateConflict:
                return Results.Conflict(new { errorCode = ApiErrorCodes.WishStateConflict });
            case WishOutcome.SettingsRestricted:
                return Results.Json(new { errorCode = ApiErrorCodes.WishSettingsRestricted }, statusCode: StatusCodes.Status403Forbidden);
            case WishOutcome.RepairCheckOpen:
                return Results.Conflict(new { errorCode = ApiErrorCodes.WishRepairCheckOpen });
            case WishOutcome.EmoteRequired:
                return Results.BadRequest(new { errorCode = ApiErrorCodes.WishEmoteRequired });
            case WishOutcome.RunningNotExtendable:
                return Results.Conflict(new { errorCode = ApiErrorCodes.WishRunningNotExtendable, wishId = result.ConflictingWishId });
            case WishOutcome.AccessDenied:
                return Results.Forbid();
            default:
                throw new UnreachableException($"Unexpected {nameof(WishOutcome)} value: {result.Outcome}.");
        }
    }

    /// <summary>
    /// The one place a reward outcome becomes a status and an error code (plan #486 2.4, 2.9). Public for the same
    /// reason as <see cref="MapResult"/>. <see cref="WishRewardOutcome.AccessDenied"/> is a 403 in the form of the
    /// manager filter (no body): the service's second wall behind <see cref="WishCaptureAccessFilter"/>.
    /// </summary>
    internal static IResult MapRewardResult(WishRewardResult result, Func<WishRewardResult, IResult> success) =>
        result.Outcome switch
        {
            WishRewardOutcome.Success => success(result),
            WishRewardOutcome.ChannelNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
            WishRewardOutcome.ChannelUnbound => Results.Conflict(new { errorCode = ApiErrorCodes.WishChannelUnbound }),
            WishRewardOutcome.AccessDenied => Results.Forbid(),
            WishRewardOutcome.LearningActive => Results.Conflict(new { errorCode = ApiErrorCodes.WishLearningActive }),
            WishRewardOutcome.LearningStale => Results.Conflict(new { errorCode = ApiErrorCodes.WishLearningStale }),
            WishRewardOutcome.NoCandidate => Results.Conflict(new { errorCode = ApiErrorCodes.WishRewardNoCandidate }),
            WishRewardOutcome.AlreadyKnown => Results.Conflict(new { errorCode = ApiErrorCodes.WishRewardAlreadyKnown }),
            WishRewardOutcome.RewardLimit => Results.Conflict(new { errorCode = ApiErrorCodes.WishRewardLimit }),
            WishRewardOutcome.RewardNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.WishRewardNotFound }),
            WishRewardOutcome.RequestInvalid => InvalidBody(),
            WishRewardOutcome.DurationOutOfRange => Results.BadRequest(new { errorCode = ApiErrorCodes.WishDurationOutOfRange }),
            WishRewardOutcome.SettingsRestricted =>
                Results.Json(new { errorCode = ApiErrorCodes.WishSettingsRestricted }, statusCode: StatusCodes.Status403Forbidden),
            _ => throw new UnreachableException($"Unexpected {nameof(WishRewardOutcome)} value: {result.Outcome}."),
        };

    private static void MapTransition(
        RouteGroupBuilder group,
        string pattern,
        Func<IWishService, string, long, AuditActor, WishCaptureAccess?, CancellationToken, Task<WishCommandResult>> command,
        bool resolveAccess = true)
    {
        group.MapPost(pattern, async (
            string channelName,
            long id,
            HttpContext httpContext,
            WishOptions options,
            IWishService wishService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var access = resolveAccess ? await ResolveAccessAsync(httpContext, channelName, channelAccess, ct) : null;
            return MapResult(await command(wishService, channelName, id, actor, access, ct), StatusCodes.Status200OK);
        })
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);
    }

    // Plan #486 W7: deciding an open wish needs Wishes:Enabled only, not the capture switch. Wishes captured before the
    // capture was switched off (or captured by a worker whose switch is on while the Api's is off) stay decidable.
    // Access to the commands is the K1 manager check (2.4a); the answers carry redeemer data per the access.
    private static void MapCaptureCommands(RouteGroupBuilder group)
    {
        group.MapPost("/{id:long}/approve", async (
            string channelName,
            long id,
            ApproveWishRequest? request,
            HttpContext httpContext,
            WishOptions options,
            IWishService wishService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            if (request is null)
            {
                return InvalidBody();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var access = await ResolveAccessAsync(httpContext, channelName, channelAccess, ct);
            var result = await wishService.ApproveAsync(channelName, id, request, actor, access, ct);
            // The answer names the outcome the manager has to be told about: merged into the running wish (the wish
            // carries mergedIntoWishId) and that wish's new expiry; null when the wish was approved on its own.
            return result is { IsSuccess: true, Wish: { } wish }
                ? Results.Ok(new ApprovedWishDto(wish, result.ExtendedExpiresAtUtc))
                : MapResult(result, StatusCodes.Status200OK);
        })
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        MapTransition(group, "/{id:long}/reject", (s, c, id, a, access, ct) => s.RejectAsync(c, id, a, access, ct));
        MapTransition(group, "/{id:long}/unlink", (s, c, id, a, access, ct) => s.UnlinkAsync(c, id, a, access, ct));
    }

    // The reward routes (plan #486 2.9). Every one carries WishCaptureAccessFilter right after the group's manager
    // filter; it answers the two switches, the binding and the id-confirmed access, and hands the access on. The
    // writes add the settings filter behind it (bot mode: broadcaster, activator, admin). {rewardId} is the row id.
    private static void MapRewardEndpoints(RouteGroupBuilder group)
    {
        var rewards = group.MapGroup("/rewards")
            .AddEndpointFilter<WishCaptureAccessFilter>();

        rewards.MapGet("", async (
            HttpContext httpContext,
            IWishRewardService rewardService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            var principal = httpContext.User.TryBuildTwitchPrincipal();
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var result = await rewardService.GetAsync(
                WishCaptureAccessFilter.GetAccess(httpContext), principal.TwitchUserId, channelAccess.IsGlobalAdmin(principal), ct);
            return MapRewardResult(result, r => Results.Ok(r.Rewards));
        })
        .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        rewards.MapPost("/learning", async (
            HttpContext httpContext,
            IWishRewardService rewardService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            var actor = httpContext.User.TryBuildAuditActor();
            return actor is null
                ? Results.Unauthorized()
                : MapRewardResult(
                    await rewardService.StartLearningAsync(
                        WishCaptureAccessFilter.GetAccess(httpContext), actor, IsGlobalAdmin(httpContext, channelAccess), ct),
                    r => Results.Json(r.Learning, statusCode: StatusCodes.Status201Created));
        })
        .AddEndpointFilter<WishSettingsAuthorizationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        rewards.MapPost("/learning/confirm", async (
            ConfirmWishRewardLearningRequest? request,
            HttpContext httpContext,
            IWishRewardService rewardService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (request is null)
            {
                return InvalidBody();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            return actor is null
                ? Results.Unauthorized()
                : MapRewardResult(
                    await rewardService.ConfirmLearningAsync(
                        WishCaptureAccessFilter.GetAccess(httpContext), request, actor, IsGlobalAdmin(httpContext, channelAccess), ct),
                    r => Results.Json(r.Reward, statusCode: StatusCodes.Status201Created));
        })
        .AddEndpointFilter<WishSettingsAuthorizationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        // The session travels in the query (a DELETE has no body). Bound as a string so a malformed id is the same
        // translatable 400 as a missing one, not the framework's bare binding failure.
        rewards.MapDelete("/learning", async (
            string? sessionId,
            HttpContext httpContext,
            IWishRewardService rewardService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var parsedSessionId) || parsedSessionId == Guid.Empty)
            {
                return InvalidBody();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            return actor is null
                ? Results.Unauthorized()
                : MapRewardResult(
                    await rewardService.CancelLearningAsync(
                        WishCaptureAccessFilter.GetAccess(httpContext), parsedSessionId, actor, IsGlobalAdmin(httpContext, channelAccess), ct),
                    _ => Results.NoContent());
        })
        .AddEndpointFilter<WishSettingsAuthorizationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        rewards.MapPatch("/{rewardId:long}", async (
            long rewardId,
            UpdateWishRewardRequest? request,
            HttpContext httpContext,
            IWishRewardService rewardService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            if (request is null)
            {
                return InvalidBody();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            return actor is null
                ? Results.Unauthorized()
                : MapRewardResult(
                    await rewardService.UpdateRewardAsync(
                        WishCaptureAccessFilter.GetAccess(httpContext), rewardId, request, actor, IsGlobalAdmin(httpContext, channelAccess), ct),
                    r => Results.Ok(r.Reward));
        })
        .AddEndpointFilter<WishSettingsAuthorizationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        rewards.MapDelete("/{rewardId:long}", async (
            long rewardId,
            HttpContext httpContext,
            IWishRewardService rewardService,
            IChannelAccessService channelAccess,
            CancellationToken ct) =>
        {
            var actor = httpContext.User.TryBuildAuditActor();
            return actor is null
                ? Results.Unauthorized()
                : MapRewardResult(
                    await rewardService.DeleteRewardAsync(
                        WishCaptureAccessFilter.GetAccess(httpContext), rewardId, actor, IsGlobalAdmin(httpContext, channelAccess), ct),
                    _ => Results.NoContent());
        })
        .AddEndpointFilter<WishSettingsAuthorizationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);
    }

    // Plan #486 2.4a: every answer that carries a WishDto asks the id-bound question next to the manager filter's and
    // hands the answer to the service, which gives out redeemer data only when it is confirmed. Access to the route
    // itself stays the manager filter's decision; an unconfirmed answer only empties those fields.
    private static async Task<WishCaptureAccess?> ResolveAccessAsync(
        HttpContext httpContext, string channelName, IChannelAccessService channelAccess, CancellationToken ct)
    {
        var principal = httpContext.User.TryBuildTwitchPrincipal();
        return principal is null ? null : await channelAccess.ResolveWishCaptureAccessAsync(principal, channelName, ct);
    }

    // The admin flag the settings rule (D7) needs behind the channel lock (#487 plan 2.9): the same question the settings
    // filter asks. No principal is no admin; the handler answers 401 before it matters.
    private static bool IsGlobalAdmin(HttpContext httpContext, IChannelAccessService channelAccess) =>
        httpContext.User.TryBuildTwitchPrincipal() is { } principal && channelAccess.IsGlobalAdmin(principal);

    private static bool TryParseView(string? view, out WishListView parsed)
    {
        switch (view?.ToLowerInvariant())
        {
            case null:
            case "":
            case "open":
                parsed = WishListView.Open;
                return true;
            case "running":
                parsed = WishListView.Running;
                return true;
            case "ended":
                parsed = WishListView.Ended;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static IResult WishesDisabled() =>
        Results.NotFound(new { errorCode = ApiErrorCodes.WishesDisabled });

    private static IResult InvalidBody() =>
        Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidRequestBody });
}

/// <summary>
/// The settings read: the settings plus <see cref="CanEdit"/>, whether this caller may write them (plan #485,
/// F1 = A). The write answers the plain settings: whoever got through its filter may edit.
/// <see cref="PendingExpiryDays"/> (K6, plan #486 2.8) is the channel's expiry of open wishes, carried whatever the
/// capture switch says, like the other values.
/// </summary>
internal sealed record WishSettingsReadDto(
    int DefaultDurationDays,
    int ProbationLeadDays,
    decimal? ProtectionThresholdPerDay,
    bool BotModeEnabled,
    string? BotSuspendedReason,
    int PendingExpiryDays,
    bool CanEdit)
{
    public WishSettingsReadDto(WishSettingsDto settings, bool canEdit)
        : this(
            settings.DefaultDurationDays,
            settings.ProbationLeadDays,
            settings.ProtectionThresholdPerDay,
            settings.BotModeEnabled,
            settings.BotSuspendedReason,
            settings.PendingExpiryDays,
            canEdit)
    {
    }
}

/// <summary>
/// The wire body of <c>PUT …/wishes/settings</c>. A threshold that is absent from the JSON keeps the stored one, an
/// explicit <c>null</c> switches protection off, a value sets it (plan #485 2.1a, Sol-P2-4). System.Text.Json calls
/// the <c>init</c> accessor only for a property present in the body, which is what tells "absent" from <c>null</c>.
/// <see cref="PendingExpiryDays"/> (K6, plan #486 2.8) is optional the plain way: absent or <c>null</c> keeps the
/// stored value, as there is no "switch off" for it.
/// </summary>
internal sealed record UpdateWishSettingsBody(int DefaultDurationDays, int ProbationLeadDays, int? PendingExpiryDays = null)
{
    private readonly decimal? _protectionThresholdPerDay;

    public decimal? ProtectionThresholdPerDay
    {
        get => _protectionThresholdPerDay;
        init
        {
            _protectionThresholdPerDay = value;
            ThresholdSent = true;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool ThresholdSent { get; private init; }

    public UpdateWishSettingsRequest ToRequest() =>
        new(DefaultDurationDays, ProbationLeadDays, ProtectionThresholdPerDay, KeepProtectionThreshold: !ThresholdSent, PendingExpiryDays);
}

/// <summary>The manual entry's answer: the wish plus the hint that the set was at capacity (never a refusal).</summary>
internal sealed record CreatedWishDto : WishDto
{
    public CreatedWishDto(WishDto wish, bool SetFull)
        : base(wish)
    {
        this.SetFull = SetFull;
    }

    public bool SetFull { get; init; }
}

/// <summary>
/// The answer of <c>POST …/wishes/{id}/approve</c> (K6, plan #486 2.6): the wish plus <see cref="ExtendedExpiresAtUtc"/>,
/// the new expiry of the running wish it was merged into (the wish then carries <c>mergedIntoWishId</c>); null when
/// the wish was approved on its own. Always present on this route, so the client reads the outcome without guessing.
/// </summary>
internal sealed record ApprovedWishDto : WishDto
{
    public ApprovedWishDto(WishDto wish, DateTime? ExtendedExpiresAtUtc)
        : base(wish)
    {
        this.ExtendedExpiresAtUtc = ExtendedExpiresAtUtc;
    }

    public DateTime? ExtendedExpiresAtUtc { get; init; }
}
