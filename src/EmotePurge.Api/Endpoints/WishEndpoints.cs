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
            WishOptions options,
            IWishService wishService,
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
            var result = await wishService.ListAsync(channelName, parsedView, effectivePage, effectivePageSize, ct);
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
                : MapResult(await wishService.UpdateSettingsAsync(channelName, request.ToRequest(), actor, ct), StatusCodes.Status200OK);
        })
        .AddEndpointFilter<WishSettingsAuthorizationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapGet("/{id:long}", async (
            string channelName,
            long id,
            WishOptions options,
            IWishService wishService,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            var detail = await wishService.GetAsync(channelName, id, ct);
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
                : MapResult(await wishService.AdoptAsync(channelName, request, actor, ct), StatusCodes.Status201Created);
        })
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapPatch("/{id:long}", async (
            string channelName,
            long id,
            UpdateWishRequest? request,
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
                : MapResult(await wishService.UpdateAsync(channelName, id, request, actor, ct), StatusCodes.Status200OK);
        })
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapPost("/{id:long}/re-add", async (
            string channelName,
            long id,
            ReAddWishRequest? request,
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
                : MapResult(await wishService.ReAddAsync(channelName, id, request, actor, ct), StatusCodes.Status200OK);
        })
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        MapTransition(group, "/{id:long}/extend", (s, c, id, a, ct) => s.ExtendAsync(c, id, a, ct));
        MapTransition(group, "/{id:long}/keep", (s, c, id, a, ct) => s.KeepAsync(c, id, a, ct));
        MapTransition(group, "/{id:long}/close", (s, c, id, a, ct) => s.CloseAsync(c, id, a, ct));
        MapTransition(group, "/{id:long}/retry", (s, c, id, a, ct) => s.RetryAsync(c, id, a, ct));
        // Plan #485 2.1a: the report names slot, generation and set, so it has a body and a handler of its own.
        group.MapPost("/{id:long}/complete-removal", async (
            string channelName,
            long id,
            CompleteWishRemovalRequest? request,
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
                : MapResult(await wishService.CompleteRemovalAsync(channelName, id, request, actor, ct), StatusCodes.Status200OK);
        })
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        // K1 registers the route; the command refuses every state until K4 gives it its one transition.
        MapTransition(group, "/{id:long}/recheck", (s, c, id, a, ct) => s.RecheckAsync(c, id, a, ct));

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
    /// The three conflicts that concern another wish name it as <c>wishId</c> (plan #485 2.1a; null when
    /// <c>wish_emote_already_in_set</c> has no running wish).
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
            default:
                throw new UnreachableException($"Unexpected {nameof(WishOutcome)} value: {result.Outcome}.");
        }
    }

    private static void MapTransition(
        RouteGroupBuilder group,
        string pattern,
        Func<IWishService, string, long, AuditActor, CancellationToken, Task<WishCommandResult>> command)
    {
        group.MapPost(pattern, async (
            string channelName,
            long id,
            HttpContext httpContext,
            WishOptions options,
            IWishService wishService,
            CancellationToken ct) =>
        {
            if (!options.Enabled)
            {
                return WishesDisabled();
            }

            var actor = httpContext.User.TryBuildAuditActor();
            return actor is null
                ? Results.Unauthorized()
                : MapResult(await command(wishService, channelName, id, actor, ct), StatusCodes.Status200OK);
        })
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);
    }

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
/// </summary>
internal sealed record WishSettingsReadDto(
    int DefaultDurationDays,
    int ProbationLeadDays,
    decimal? ProtectionThresholdPerDay,
    bool BotModeEnabled,
    string? BotSuspendedReason,
    bool CanEdit)
{
    public WishSettingsReadDto(WishSettingsDto settings, bool canEdit)
        : this(
            settings.DefaultDurationDays,
            settings.ProbationLeadDays,
            settings.ProtectionThresholdPerDay,
            settings.BotModeEnabled,
            settings.BotSuspendedReason,
            canEdit)
    {
    }
}

/// <summary>
/// The wire body of <c>PUT …/wishes/settings</c>. A threshold that is absent from the JSON keeps the stored one, an
/// explicit <c>null</c> switches protection off, a value sets it (plan #485 2.1a, Sol-P2-4). System.Text.Json calls
/// the <c>init</c> accessor only for a property present in the body, which is what tells "absent" from <c>null</c>.
/// </summary>
internal sealed record UpdateWishSettingsBody(int DefaultDurationDays, int ProbationLeadDays)
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
        new(DefaultDurationDays, ProbationLeadDays, ProtectionThresholdPerDay, KeepProtectionThreshold: !ThresholdSent);
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
