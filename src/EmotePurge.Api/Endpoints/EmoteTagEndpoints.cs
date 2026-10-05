using System.Diagnostics;
using EmotePurge.Api.Auth;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// Channel-owned emote tags (#201). Three groups on the same prefix: reads behind the usage-stats
/// access check (anyone who may look at the channel's stats may see its tags), maintenance behind the
/// channel-management check, and the registration and reports of tag runs (T-C) behind the usage-stats
/// check plus the 7TV ownership ladder in the handler. Handlers only translate; the rules live in
/// <see cref="IEmoteTagService"/>.
/// </summary>
public static class EmoteTagEndpoints
{
    public static void MapEmoteTagEndpoints(this WebApplication app)
    {
        const string prefix = "/api/channels/{channelName}/tags";

        var read = app.MapGroup(prefix)
            .RequireAuthorization()
            // Ahead of the authorization filter on purpose — see ChannelNameValidationFilter.
            .AddEndpointFilter<ChannelNameValidationFilter>()
            .AddEndpointFilter<UsageStatsAccessAuthorizationFilter>()
            .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        // emoteSetId is optional on both reads; absent means the channel's active set. The filter runs
        // per route, after the group's authorization, like the tracked-set preview.
        read.MapGet("", async (
            string channelName,
            string? emoteSetId,
            IEmoteTagService tagService,
            CancellationToken ct) =>
        {
            var result = await tagService.ListAsync(channelName, emoteSetId, ct);
            return result.Status switch
            {
                EmoteTagListStatus.Ok => Results.Ok(new EmoteTagListResponse(result.EmoteSetId, result.IsActiveSet, result.Tags)),
                EmoteTagListStatus.ChannelNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
                _ => throw new UnreachableException($"Unexpected {nameof(EmoteTagListStatus)} value: {result.Status}.")
            };
        })
        .AddEndpointFilter<EmoteSetIdValidationFilter>();

        read.MapGet("/{tagId:long}/entries", async (
            string channelName,
            long tagId,
            string? emoteSetId,
            IEmoteTagService tagService,
            CancellationToken ct) =>
        {
            var result = await tagService.ListEntriesAsync(channelName, tagId, emoteSetId, ct);
            return result.Status switch
            {
                EmoteTagEntriesStatus.Ok => Results.Ok(new EmoteTagEntriesResponse(result.EmoteSetId, result.IsActiveSet, result.Entries, result.ActivationOperationId)),
                EmoteTagEntriesStatus.ChannelNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
                EmoteTagEntriesStatus.TagNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.TagNotFound }),
                _ => throw new UnreachableException($"Unexpected {nameof(EmoteTagEntriesStatus)} value: {result.Status}.")
            };
        })
        .AddEndpointFilter<EmoteSetIdValidationFilter>();

        var manage = app.MapGroup(prefix)
            .RequireAuthorization()
            .AddEndpointFilter<ChannelNameValidationFilter>()
            .AddEndpointFilter<ChannelManagementAuthorizationFilter>()
            .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        // A missing body or a missing "name" is the same 400 as an unfit name: no code of its own.
        manage.MapPost("", async (
            string channelName,
            CreateTagRequest? request,
            HttpContext httpContext,
            IEmoteTagService tagService,
            CancellationToken ct) =>
        {
            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var result = await tagService.CreateAsync(channelName, request?.Name, actor, ct);
            return result.Status == EmoteTagMutationStatus.Ok
                ? Results.Json(result.Tag, statusCode: StatusCodes.Status201Created)
                : MapMutationFailure(result.Status);
        });

        manage.MapPatch("/{tagId:long}", async (
            string channelName,
            long tagId,
            RenameTagRequest? request,
            HttpContext httpContext,
            IEmoteTagService tagService,
            CancellationToken ct) =>
        {
            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var result = await tagService.RenameAsync(channelName, tagId, request?.Name, actor, ct);
            return result.Status == EmoteTagMutationStatus.Ok
                ? Results.Ok(result.Tag)
                : MapMutationFailure(result.Status);
        });

        manage.MapDelete("/{tagId:long}", async (
            string channelName,
            long tagId,
            HttpContext httpContext,
            IEmoteTagService tagService,
            CancellationToken ct) =>
        {
            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var status = await tagService.DeleteAsync(channelName, tagId, actor, ct);
            return status == EmoteTagMutationStatus.Ok ? Results.NoContent() : MapMutationFailure(status);
        });

        manage.MapPost("/{tagId:long}/entries", async (
            string channelName,
            long tagId,
            TagEntryIdsRequest? request,
            IEmoteTagService tagService,
            CancellationToken ct) =>
        {
            var result = await tagService.AddEntriesAsync(channelName, tagId, request?.SevenTvEmoteIds, ct);
            return result.Status switch
            {
                EmoteTagAddEntriesStatus.Ok => Results.Ok(
                    new { addedCount = result.AddedCount, alreadyTaggedCount = result.AlreadyTaggedCount, skippedNotInSetIds = result.SkippedNotInSetIds }),
                EmoteTagAddEntriesStatus.ChannelNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
                EmoteTagAddEntriesStatus.TagNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.TagNotFound }),
                EmoteTagAddEntriesStatus.EmoteIdsEmpty => Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdsEmpty }),
                EmoteTagAddEntriesStatus.EmoteIdsInvalid => Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdsInvalid }),
                EmoteTagAddEntriesStatus.EntryLimitReached => Results.Conflict(new { errorCode = ApiErrorCodes.TagEntryLimitReached }),
                _ => throw new UnreachableException($"Unexpected {nameof(EmoteTagAddEntriesStatus)} value: {result.Status}.")
            };
        });

        manage.MapPost("/{tagId:long}/entries/remove", async (
            string channelName,
            long tagId,
            TagEntryIdsRequest? request,
            IEmoteTagService tagService,
            CancellationToken ct) =>
        {
            var result = await tagService.RemoveEntriesAsync(channelName, tagId, request?.SevenTvEmoteIds, ct);
            return result.Status switch
            {
                EmoteTagRemoveEntriesStatus.Ok => Results.Ok(new { removedCount = result.RemovedCount }),
                EmoteTagRemoveEntriesStatus.ChannelNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
                EmoteTagRemoveEntriesStatus.TagNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.TagNotFound }),
                EmoteTagRemoveEntriesStatus.EmoteIdsEmpty => Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdsEmpty }),
                EmoteTagRemoveEntriesStatus.EmoteIdsInvalid => Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdsInvalid }),
                _ => throw new UnreachableException($"Unexpected {nameof(EmoteTagRemoveEntriesStatus)} value: {result.Status}.")
            };
        });

        MapReportEndpoints(app);
    }

    // The report group (#201 T-C). Registration and reports are bookkeeping about a run the browser has
    // already been allowed to start, so the group is open to everyone who may see the channel's usage
    // (E9 rev. 3): the real gate is the 7TV ownership ladder, which needs the body's set id and
    // therefore lives in each handler rather than in a filter. Every handler runs, in this order:
    //   1. the form step — operation id (UUID), kind (registration only), set id, every id list;
    //      nothing is asked of 7TV for a malformed body, and a malformed body outranks the ladder;
    //   2. the actor (401);
    //   3. IImportTargetOwnershipService via EmoteSetOwnershipRejection (404 / bare 403 / 503) —
    //      the same ladder, and the same translation, as the set-centric sync-* reports;
    //   4. the service, whose status maps to the response.
    // The channel filters stay in front of all of it, so a caller without access to the channel gets
    // no 403 from the ladder that would reveal whether a set exists.
    private static void MapReportEndpoints(WebApplication app)
    {
        var report = app.MapGroup("/api/channels/{channelName}/tags/{tagId:long}")
            .RequireAuthorization()
            .AddEndpointFilter<ChannelNameValidationFilter>()
            .AddEndpointFilter<UsageStatsAccessAuthorizationFilter>()
            .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        // Registered before the run's first 7TV write (E27 rev. 3). Writes no audit entry: a
        // registration is intent, not an event, so the service takes no actor either.
        report.MapPost("/operations", async (
            string channelName,
            long tagId,
            RegisterTagOperationBody? request,
            HttpContext httpContext,
            IImportTargetOwnershipService ownershipService,
            IEmoteTagService tagService,
            CancellationToken ct) =>
        {
            var formError = ValidateOperationForm(request?.OperationId, request?.EmoteSetId, out var operationId);
            if (formError is null && !EmoteTagOperationKind.IsKnown(request!.Kind))
            {
                formError = ApiErrorCodes.TagOperationKindInvalid;
            }

            if (formError is not null)
            {
                return Results.BadRequest(new { errorCode = formError });
            }

            var (rejection, actor) = await PassOwnershipLadderAsync(request!.EmoteSetId!, request.TargetOwnerTwitchId, httpContext, ownershipService, ct);
            if (rejection is not null)
            {
                return rejection;
            }

            var result = await tagService.RegisterOperationAsync(
                ChannelName.Normalize(channelName),
                tagId,
                new RegisterTagOperationRequest(operationId, request.Kind!, request.EmoteSetId!),
                ct);
            return result.Status switch
            {
                TagOperationRegistrationStatus.Ok => Results.Json(
                    new { registeredAtUtc = RequireRegisteredAt(result) }, statusCode: StatusCodes.Status201Created),
                TagOperationRegistrationStatus.Replayed => Results.Ok(new { registeredAtUtc = RequireRegisteredAt(result) }),
                TagOperationRegistrationStatus.ChannelNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
                TagOperationRegistrationStatus.TagNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.TagNotFound }),
                TagOperationRegistrationStatus.Conflict => Results.Conflict(new { errorCode = ApiErrorCodes.TagOperationConflict }),
                _ => throw new UnreachableException($"Unexpected {nameof(TagOperationRegistrationStatus)} value: {result.Status}.")
            };
        });

        report.MapPost("/placements", async (
            string channelName,
            long tagId,
            TagPlacementsRequest? request,
            HttpContext httpContext,
            IImportTargetOwnershipService ownershipService,
            IEmoteTagService tagService,
            CancellationToken ct) =>
        {
            var formError = ValidateOperationForm(request?.OperationId, request?.EmoteSetId, out var operationId)
                ?? ValidateIdList(request?.SevenTvEmoteIds);
            if (formError is not null)
            {
                return Results.BadRequest(new { errorCode = formError });
            }

            var (rejection, actor) = await PassOwnershipLadderAsync(request!.EmoteSetId!, request.TargetOwnerTwitchId, httpContext, ownershipService, ct);
            if (rejection is not null)
            {
                return rejection;
            }

            var result = await tagService.ReportPlacementsAsync(
                ChannelName.Normalize(channelName),
                tagId,
                new TagPlacementReport(operationId, request.EmoteSetId!, request.SevenTvEmoteIds!),
                actor!,
                ct);
            return MapReportFailure(result.Status) ?? Results.Ok(new
            {
                replayed = result.Replayed,
                recordedCount = result.RecordedCount,
                alreadyRecordedCount = result.AlreadyRecordedCount,
                notTaggedIds = result.NotTaggedIds,
                discardedStaleIds = result.DiscardedStaleIds
            });
        });

        report.MapPost("/placements/removed", async (
            string channelName,
            long tagId,
            TagPlacementsRemovedRequest? request,
            HttpContext httpContext,
            IImportTargetOwnershipService ownershipService,
            IEmoteTagService tagService,
            CancellationToken ct) =>
        {
            var formError = ValidateOperationForm(request?.OperationId, request?.EmoteSetId, out var operationId);
            Guid? activationOperationId = null;
            if (formError is null && request!.ActivationOperationId is { } rawActivationOperationId)
            {
                // null is legal (the preview read no activation, so nothing may deactivate); a value
                // that is present but not a UUID is a malformed body.
                if (TryParseOperationId(rawActivationOperationId, out var parsedActivation))
                {
                    activationOperationId = parsedActivation;
                }
                else
                {
                    formError = ApiErrorCodes.TagOperationIdInvalid;
                }
            }

            List<TagPlacementSnapshotEntry>? snapshot = null;
            if (formError is null)
            {
                formError = ValidateSnapshot(request!.Snapshot, out snapshot)
                    ?? ValidateIdList(request.RemovedIds)
                    ?? ValidateIdList(request.KeptIds);
            }

            if (formError is not null)
            {
                return Results.BadRequest(new { errorCode = formError });
            }

            var (rejection, actor) = await PassOwnershipLadderAsync(request!.EmoteSetId!, request.TargetOwnerTwitchId, httpContext, ownershipService, ct);
            if (rejection is not null)
            {
                return rejection;
            }

            var result = await tagService.ReportRemovalAsync(
                ChannelName.Normalize(channelName),
                tagId,
                new TagRemovalReport(operationId, request.EmoteSetId!, activationOperationId, snapshot!, request.RemovedIds!, request.KeptIds!),
                actor!,
                ct);
            var failure = MapReportFailure(result.Status);
            if (failure is not null)
            {
                return failure;
            }

            // A replay is answered with the documented all-zero body, built here rather than copied
            // from the service result: on a replay the counters and Deactivated describe no outcome
            // (the first application's figures are not reconstructed), so a client must never read
            // them as one. The flag is what tells it to re-read the tag instead.
            return result.Replayed
                ? Results.Ok(new { replayed = true, deletedCount = 0, transferredCount = 0, droppedCount = 0, sweptCount = 0, deactivated = false })
                : Results.Ok(new
                {
                    replayed = false,
                    deletedCount = result.DeletedCount,
                    transferredCount = result.TransferredCount,
                    droppedCount = result.DroppedCount,
                    sweptCount = result.SweptCount,
                    deactivated = result.Deactivated
                });
        });
    }

    // The form step shared by all three routes: operation id, then set id. A missing body is a missing
    // operation id. EmoteSetIdValidation.IsValid is the same rule the route filters apply.
    private static string? ValidateOperationForm(string? operationId, string? emoteSetId, out Guid parsedOperationId)
    {
        if (!TryParseOperationId(operationId, out parsedOperationId))
        {
            return ApiErrorCodes.TagOperationIdInvalid;
        }

        return emoteSetId is not null && EmoteSetIdValidation.IsValid(emoteSetId) ? null : ApiErrorCodes.InvalidEmoteSetId;
    }

    private static bool TryParseOperationId(string? value, out Guid parsed)
    {
        parsed = Guid.Empty;
        return value is not null && Guid.TryParseExact(value, "D", out parsed) && parsed != Guid.Empty;
    }

    // A report's id list may be empty (a run that added or removed nothing is a legal report), but a
    // missing or JSON-null list is a malformed body: EmoteTagIdList.Check maps null to Empty, which
    // would let it through, so null is refused here before the shared rule runs.
    private static string? ValidateIdList(IReadOnlyList<string>? ids) =>
        ids is not null && EmoteTagIdList.Check(ids) != EmoteTagIdListStatus.Invalid ? null : ApiErrorCodes.EmoteIdsInvalid;

    // The snapshot is an id list with a revision per id: null list, null element, a malformed id and
    // an oversize list are emote_ids_invalid (the same rule as every id list); a revision that is not a
    // UUID is tag_operation_id_invalid, checked after all ids.
    private static string? ValidateSnapshot(
        IReadOnlyList<TagPlacementSnapshotEntryRequest?>? snapshot, out List<TagPlacementSnapshotEntry>? entries)
    {
        entries = null;
        if (snapshot is null || snapshot.Any(entry => entry is null))
        {
            return ApiErrorCodes.EmoteIdsInvalid;
        }

        var idError = ValidateIdList([.. snapshot.Select(entry => entry!.SevenTvEmoteId!)]);
        if (idError is not null)
        {
            return idError;
        }

        var parsed = new List<TagPlacementSnapshotEntry>(snapshot.Count);
        foreach (var entry in snapshot)
        {
            if (!TryParseOperationId(entry!.PlacementOperationId, out var revision))
            {
                return ApiErrorCodes.TagOperationIdInvalid;
            }

            parsed.Add(new TagPlacementSnapshotEntry(entry.SevenTvEmoteId!, revision));
        }

        entries = parsed;
        return null;
    }

    // Stage 3 of the handler order: the actor, then the shared ownership ladder. Null means "the
    // actor owns the set (or edits its owner's account)"; anything else ends the request. The actor
    // it built comes back with the verdict, so the handler never rebuilds it.
    private static async Task<(IResult? Rejection, AuditActor? Actor)> PassOwnershipLadderAsync(
        string emoteSetId,
        string? targetOwnerTwitchId,
        HttpContext httpContext,
        IImportTargetOwnershipService ownershipService,
        CancellationToken ct)
    {
        var actor = httpContext.User.TryBuildAuditActor();
        if (actor is null)
        {
            return (Results.Unauthorized(), null);
        }

        var ownership = await ownershipService.CheckAsync(
            actor.TwitchUserId, actor.Login, emoteSetId, ct, EmoteSetOwnershipRejection.BuildOwnerHint(targetOwnerTwitchId));
        return (EmoteSetOwnershipRejection.For(ownership.Status), actor);
    }

    private static DateTime RequireRegisteredAt(TagOperationRegistrationResult result) =>
        result.RegisteredAtUtc ?? throw new UnreachableException("A registered operation answered without its registration time.");

    // The failures both reports share; null means Ok.
    private static IResult? MapReportFailure(TagReportStatus status) => status switch
    {
        TagReportStatus.Ok => null,
        TagReportStatus.ChannelNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
        TagReportStatus.TagNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.TagNotFound }),
        TagReportStatus.OperationUnknown => Results.NotFound(new { errorCode = ApiErrorCodes.TagOperationUnknown }),
        TagReportStatus.OperationConflict => Results.Conflict(new { errorCode = ApiErrorCodes.TagOperationConflict }),
        _ => throw new UnreachableException($"Unexpected {nameof(TagReportStatus)} value: {status}.")
    };

    private static IResult MapMutationFailure(EmoteTagMutationStatus status) => status switch
    {
        EmoteTagMutationStatus.ChannelNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
        EmoteTagMutationStatus.TagNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.TagNotFound }),
        EmoteTagMutationStatus.NameInvalid => Results.BadRequest(new { errorCode = ApiErrorCodes.TagNameInvalid }),
        EmoteTagMutationStatus.NameTaken => Results.Conflict(new { errorCode = ApiErrorCodes.TagNameTaken }),
        EmoteTagMutationStatus.LimitReached => Results.Conflict(new { errorCode = ApiErrorCodes.TagLimitReached }),
        _ => throw new UnreachableException($"Unexpected {nameof(EmoteTagMutationStatus)} value: {status}.")
    };
}

public sealed record CreateTagRequest(string? Name);

public sealed record RenameTagRequest(string? Name);

public sealed record TagEntryIdsRequest(IReadOnlyList<string>? SevenTvEmoteIds);

// Bodies of the report group (#201 T-C, spec 6.4). Every field is nullable on purpose: the handler's
// form step answers a missing field with its own error code instead of the framework's bare 400.
// The operation id is a string until the form step has parsed it (the Core request records carry a
// Guid); TargetOwnerTwitchId is the optional owner hint of the ownership ladder.
public sealed record RegisterTagOperationBody(string? OperationId, string? Kind, string? EmoteSetId, string? TargetOwnerTwitchId);

public sealed record TagPlacementsRequest(
    string? OperationId, string? EmoteSetId, string? TargetOwnerTwitchId, IReadOnlyList<string>? SevenTvEmoteIds);

public sealed record TagPlacementSnapshotEntryRequest(string? SevenTvEmoteId, string? PlacementOperationId);

public sealed record TagPlacementsRemovedRequest(
    string? OperationId,
    string? EmoteSetId,
    string? TargetOwnerTwitchId,
    string? ActivationOperationId,
    IReadOnlyList<TagPlacementSnapshotEntryRequest?>? Snapshot,
    IReadOnlyList<string>? RemovedIds,
    IReadOnlyList<string>? KeptIds);

// The tag and entry DTOs carry the placement and activation fields (#201 T-C) themselves; the list
// response gains no top-level field of its own (spec 6.2).
public sealed record EmoteTagListResponse(string? EmoteSetId, bool IsActiveSet, IReadOnlyList<EmoteTagSummaryDto> Tags);

public sealed record EmoteTagEntriesResponse(
    string? EmoteSetId, bool IsActiveSet, IReadOnlyList<EmoteTagEntryDto> Entries, Guid? ActivationOperationId);
