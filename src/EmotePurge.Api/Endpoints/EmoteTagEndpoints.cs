using System.Diagnostics;
using EmotePurge.Api.Auth;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// Channel-owned emote tags (#201). Two groups on the same prefix: reads behind the usage-stats
/// access check (anyone who may look at the channel's stats may see its tags), maintenance behind the
/// channel-management check. Handlers only translate; the rules live in <see cref="IEmoteTagService"/>.
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
                EmoteTagEntriesStatus.Ok => Results.Ok(new EmoteTagEntriesResponse(result.EmoteSetId, result.IsActiveSet, result.Entries)),
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
    }

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

public sealed record EmoteTagListResponse(string? EmoteSetId, bool IsActiveSet, IReadOnlyList<EmoteTagSummaryDto> Tags);

public sealed record EmoteTagEntriesResponse(string? EmoteSetId, bool IsActiveSet, IReadOnlyList<EmoteTagEntryDto> Entries);
