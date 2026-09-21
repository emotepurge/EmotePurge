using System.Diagnostics;
using EmotePurge.Api.Auth;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;

namespace EmotePurge.Api.Endpoints;

public static class EmoteEndpoints
{
    public static void MapEmoteEndpoints(this WebApplication app)
    {
        // UsageStatsAccessAuthorizationFilter (not ChannelManagementAuthorizationFilter) applies to the
        // whole group: a channel's 7TV editors can legitimately delete/view via 7TV's own permission
        // system, so they must be admitted here too, not just channel managers.
        // The whole group is InteractiveRead: what a caller does here is read a channel's emotes while
        // navigating, and this policy is the app's ordinary-navigation budget. UsageStatsAccessAuthorization-
        // Filter can reach 7TV for a caller who is neither admin, broadcaster nor mod — but that is a
        // cache miss on an authorization answer, not a per-request cost a request budget could bound
        // (see the policy comments in Program.cs). The three sync-* bookkeeping endpoints override
        // the group below, and that override is the point: they must survive a spent read budget.
        var group = app.MapGroup("/api/channels/{channelName}/emotes")
            .RequireAuthorization()
            // Ahead of the authorization filter on purpose — see ChannelNameValidationFilter.
            .AddEndpointFilter<ChannelNameValidationFilter>()
            .AddEndpointFilter<UsageStatsAccessAuthorizationFilter>()
            .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        // The group root: a slim, unpaginated list of the channel's currently active emotes (7TV id
        // and name only — no usage numbers, no time range, no Emote.Id, which is channel-scoped and
        // meaningless across channels). The import dialog uses it to answer "already in the target
        // set?" and "name collision?" against a source channel or file; both questions are cheap
        // enough over the whole set (~900 emotes at most) that a page of results would only get in
        // the way. Stays on the group's InteractiveRead policy: this is an ordinary navigation read,
        // not a bookkeeping call like the two sync-* routes below it.
        group.MapGet("", async (
            string channelName,
            IEmoteListQueryService emoteListQueryService,
            CancellationToken ct) =>
        {
            var emotes = await emoteListQueryService.ListActiveAsync(channelName, ct);
            return emotes is null ? Results.NotFound() : Results.Ok(new { emotes });
        });

        // Spec 6.1: /api/channels/{channelName}/emote-sets — a route sibling of the /emotes group
        // above (not nested under it), but carrying the exact same filter chain and policy, in the
        // same file, rather than a second, independently-assembled combination (the "kein zweiter
        // Filtersatz" the spec calls for). Registered on `app`, not on `group`, because the group
        // object's prefix would otherwise nest this under /emotes/emote-sets instead of the sibling
        // path the spec names. isActive/isPersonal and observations are assembled here, not carried
        // by the shared ISevenTvEmoteSetListService result: E21 makes "active" a per-route question
        // (this route's answer is Channel.ActiveEmoteSetId, our own observed state — never 7TV's
        // style.activeEmoteSetId, which /me/emote-set-targets and the foreign-channel source picker
        // use instead), and one cached list answer has to serve all three routes' notions of it.
        app.MapGet("/api/channels/{channelName}/emote-sets", async (
            string channelName,
            IChannelService channelService,
            ISevenTvEmoteSetListService emoteSetListService,
            CancellationToken ct) =>
        {
            var channel = await channelService.GetByNameAsync(channelName, ct);
            if (channel is null)
            {
                return Results.NotFound();
            }

            if (channel.TwitchChannelId is null)
            {
                // No sync has ever resolved a Twitch identity for this channel — nothing to ask 7TV
                // about yet. Not a failure: an empty list with the channel's (necessarily empty)
                // ActiveEmoteSetId is a complete answer (spec 6.1's state table).
                return Results.Ok(new EmoteSetListResponse(channel.ActiveEmoteSetId, []));
            }

            var result = await emoteSetListService.ListByTwitchIdAsync(channel.TwitchChannelId, ct);
            return result.Status switch
            {
                EmoteSetListStatus.Ok => Results.Ok(BuildEmoteSetListResponse(channel, result.List!)),
                // 7TV genuinely has no account for this Twitch id — an answer, not a failure (spec
                // 6.1). Channel.ActiveEmoteSetId is still reported: for a tracked channel it is our
                // own observed truth regardless of what 7TV currently says about the account (E21).
                EmoteSetListStatus.NoSevenTvAccount => Results.Ok(new EmoteSetListResponse(channel.ActiveEmoteSetId, [])),
                EmoteSetListStatus.RateLimited
                    or EmoteSetListStatus.Unavailable
                    or EmoteSetListStatus.BudgetExhausted => Results.Json(
                    new { errorCode = ApiErrorCodes.ForeignChannelSevenTvUnavailable },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => throw new UnreachableException(
                    $"Unexpected {nameof(EmoteSetListStatus)} value: {result.Status}.")
            };
        })
        .RequireAuthorization()
        // Ahead of the authorization filter on purpose — see ChannelNameValidationFilter.
        .AddEndpointFilter<ChannelNameValidationFilter>()
        .AddEndpointFilter<UsageStatsAccessAuthorizationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        group.MapPost("/sync-deleted", async (
            string channelName,
            SyncDeletedRequest request,
            HttpContext httpContext,
            IEmoteService emoteService,
            IRedisPublisher redisPublisher,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (request.EmoteIds is null || request.EmoteIds.Count == 0)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdsEmpty });
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var result = await emoteService.MarkDeletedAsync(channelName, request.EmoteIds, actor, ct);
            if (result.NewlyArchivedCount > 0)
            {
                await PublishChannelSyncedAsync(redisPublisher, logger, channelName);
            }

            return Results.Ok(new { archivedCount = result.ArchivedCount, notFoundIds = result.NotFoundIds });
        })
        // Overrides the group's policy: this is the one call that must never be dropped. The emotes
        // are already gone from 7TV by the time it runs, so a 429 here leaves the database diverging
        // from reality — and it used to share a 20/min budget with join and the vote endpoints, which
        // several delete batches in one minute could exhaust.
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        // The restore counterpart: the browser has already re-added the emotes on 7TV, this call
        // un-archives them here and — its actual reason to exist — writes the emotes.syncRestored
        // audit entry. Without it a restore only ever showed up as an anonymous channel.resync
        // (or, under the resync cooldown, not at all).
        group.MapPost("/sync-restored", async (
            string channelName,
            SyncRestoredRequest request,
            HttpContext httpContext,
            IEmoteService emoteService,
            IRedisPublisher redisPublisher,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (request.EmoteIds is null || request.EmoteIds.Count == 0)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdsEmpty });
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var result = await emoteService.MarkRestoredAsync(channelName, request.EmoteIds, actor, ct);
            if (result.NewlyRestoredCount > 0)
            {
                await PublishChannelSyncedAsync(redisPublisher, logger, channelName);
            }

            return Results.Ok(new { restoredCount = result.RestoredCount, notFoundIds = result.NotFoundIds });
        })
        // Same reasoning as sync-deleted: the emotes are already back on 7TV, a dropped call here
        // costs the paper trail and leaves the database stale until the next sync.
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        // The import dialog's after-the-fact bookkeeping call. Unlike its two neighbors it never
        // touches an Emote row: an import creates and un-archives nothing here, the target channel's
        // own resync does that once it runs next (the design's "Nachlauf-Gate"). Its only reason to
        // exist is the emotes.syncImported audit entry — without it, an import would look like an
        // anonymous channel.resync in the log, or nothing at all under the resync cooldown.
        group.MapPost("/sync-imported", async (
            string channelName,
            SyncImportedRequest request,
            HttpContext httpContext,
            IEmoteService emoteService,
            CancellationToken ct) =>
        {
            var vocabularyError = ValidateSyncImportedVocabulary(
                request.SevenTvEmoteIds, request.SourceChannelName, request.SourceKind, request.LeaderboardSort);
            if (vocabularyError is not null)
            {
                return Results.BadRequest(new { errorCode = vocabularyError });
            }

            // TargetEmoteSetId (spec 6.7, E5): stays optional forever, so this only ever rejects a
            // malformed value, never a missing one — the query-string half of EmoteSetIdValidationFilter
            // does not apply here since this is a body field, not a query/route parameter, so the check
            // is inline instead of a shared filter (AK 29).
            if (request.TargetEmoteSetId is not null && !EmoteSetIdValidation.IsValid(request.TargetEmoteSetId))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidEmoteSetId });
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var written = await emoteService.MarkImportedAsync(
                channelName, request.SevenTvEmoteIds, request.SourceChannelName, request.SourceKind,
                request.LeaderboardSort, actor, request.TargetEmoteSetId, ct);
            return written ? Results.NoContent() : Results.NotFound();
        })
        // Same reasoning as its two neighbors above: the emotes were already imported on 7TV by the
        // time this call runs, so a 429 here would only cost the paper trail, not correctness.
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapGet("/set-warning", async (
            string channelName,
            HttpContext httpContext,
            IEmoteSetOwnershipService emoteSetOwnershipService,
            CancellationToken ct,
            string? emoteSetId = null) =>
        {
            var principal = httpContext.User.TryBuildTwitchPrincipal();
            var warning = await emoteSetOwnershipService.CheckAsync(channelName, principal, emoteSetId, ct);
            return Results.Ok(warning);
        })
        // Spec 6.8/E14: format-validated ahead of the handler, same idiom as ChannelNameValidationFilter
        // on the group above. Attached to this one route, not the group — emoteSetId is meaningless on
        // every other route in it.
        .AddEndpointFilter<EmoteSetIdValidationFilter>();

        // Deliberately separate from GET /api/channels/{channelName} (which stays management-only,
        // since it also backs the join-status/leave-button check): the mass-delete panel needs the
        // active set id to render its "Löschen" button, and 7TV editors — who can legitimately delete
        // via 7TV's own permission system — must be able to see it despite not being allowed to manage
        // the channel at all. The slot budget and the tracking start ride along on this same call for
        // exactly that reason: both are for the same audience, and both pages already fetch this.
        group.MapGet("/active-set", async (
            string channelName,
            IEmoteSetStatusService emoteSetStatusService,
            CancellationToken ct) =>
        {
            var status = await emoteSetStatusService.GetAsync(channelName, ct);
            return status is null ? Results.NotFound() : Results.Ok(status);
        });
    }

    /// <summary>
    /// Announces "this channel's emote inventory changed" — the same event the worker's sync paths
    /// publish, because the effect on every open page is identical: the database now reflects what
    /// happened on 7TV (archived after a delete, active again after a restore). Published only when
    /// the call actually changed rows (the live sync often got there first, and a no-op must not
    /// make everyone refetch).
    /// <para>
    /// In the endpoint rather than in EmoteService, exactly like the vote event: the notification
    /// belongs to the request that caused it, and IRedisPublisher in a handler is explicitly
    /// allowed by rule 4. Failure is logged and swallowed — the archiving is committed and the
    /// response must not change because Redis hiccuped.
    /// </para>
    /// </summary>
    /// <summary>
    /// The <c>sync-imported</c> body vocabulary table (spec 6.7), shared verbatim by this group's own
    /// <c>/sync-imported</c> handler above and by the set-centric
    /// <c>POST /api/seventv/emote-sets/{emoteSetId}/sync-imported</c> in <c>SevenTvEndpoints</c> — it
    /// exists exactly once so the two routes cannot drift apart. Ordinal and strictly lower-case (F3,
    /// import plan): the only caller is our own frontend, so a silent case-insensitive fallback would
    /// hide a frontend bug rather than surfacing it. "seventv-channel" is the third vocabulary word
    /// (foreign-import spec E6/F5.1): a channel EmotePurge does not track, read straight from 7TV.
    /// "seventv-leaderboard" is the fourth (leaderboard-import spec E8/F1): a network-wide 7TV ranking,
    /// which has no source channel at all — its origin travels in <paramref name="leaderboardSort"/>
    /// instead. Every word is deliberately its own rather than folded into an existing one — they are
    /// read through different paths and an audit row must still say which one it was, forever. Adding
    /// a word here is never enough on its own: <c>AuditLogQueryService.ProjectDetail</c> has to learn
    /// it too, or every row written with it silently loses its provenance (F5.3/F1 Station 5).
    /// <para>
    /// Returns the <see cref="ApiErrorCodes"/> value for the first violation found, or <c>null</c> when
    /// the body is internally consistent — never a <c>Results</c> value itself, so each caller (whose
    /// filter chain and route shape differ — this group carries <c>ChannelNameValidationFilter</c>, the
    /// set-centric route carries <c>EmoteSetIdValidationFilter</c> instead) decides how to answer.
    /// </para>
    /// </summary>
    internal static string? ValidateSyncImportedVocabulary(
        IReadOnlyList<string>? sevenTvEmoteIds, string? sourceChannelName, string sourceKind, string? leaderboardSort)
    {
        if (sevenTvEmoteIds is null || sevenTvEmoteIds.Count == 0)
        {
            return ApiErrorCodes.EmoteIdsEmpty;
        }

        if (sourceKind is not ("channel" or "file" or "seventv-channel" or "seventv-leaderboard"))
        {
            return ApiErrorCodes.InvalidSourceKind;
        }

        // SourceChannelName is attacker-controlled free text that ends up in jsonb forever (R6, import
        // plan) — validated like every other inbound channel name, but only when the caller actually
        // set one; the kind-versus-name agreement is checked just below.
        if (sourceChannelName is not null && !ChannelNameValidation.IsValid(sourceChannelName))
        {
            return ApiErrorCodes.InvalidChannelName;
        }

        // The kind decides what else may be set, in both directions. Audit rows are write-once and
        // kept forever, so an inconsistent body would leave a permanently wrong entry: "channel"/
        // "seventv-channel" without a name claims an origin they cannot name, "file" or
        // "seventv-leaderboard" with one gets filed under a channel origin the import never had, and
        // any kind other than "seventv-leaderboard" carrying a LeaderboardSort claims a ranking it did
        // not come from. A LeaderboardSort that is present but outside 7TV's own sort vocabulary is its
        // own error, invalid_leaderboard_sort, because it is not "the wrong kind of import" — the kind
        // is right, the sort code just is not one the endpoint knows how to hand to 7TV.
        var isChannelSourceKind = sourceKind is "channel" or "seventv-channel";
        if (isChannelSourceKind && (string.IsNullOrWhiteSpace(sourceChannelName) || leaderboardSort is not null))
        {
            return ApiErrorCodes.InvalidSourceKind;
        }

        if (sourceKind == "file" && (!string.IsNullOrWhiteSpace(sourceChannelName) || leaderboardSort is not null))
        {
            return ApiErrorCodes.InvalidSourceKind;
        }

        if (sourceKind == "seventv-leaderboard")
        {
            if (!string.IsNullOrWhiteSpace(sourceChannelName) || leaderboardSort is null)
            {
                return ApiErrorCodes.InvalidSourceKind;
            }

            if (!SevenTvLeaderboardSortWireCode.TryParse(leaderboardSort, out _))
            {
                return ApiErrorCodes.InvalidLeaderboardSort;
            }
        }

        return null;
    }

    private static async Task PublishChannelSyncedAsync(
        IRedisPublisher redisPublisher,
        ILogger logger,
        string channelName)
    {
        try
        {
            // No request token on purpose: the write is committed, so a client that hung up right
            // after the delete must not cost every *other* viewer their update.
            await redisPublisher.PublishAsync(
                LiveEvents.Channel,
                new LiveEvent(LiveEvents.ChannelSynced, ChannelName.Normalize(channelName)).Serialize());
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Live-Event '{Type}' für {Channel} konnte nicht veröffentlicht werden.",
                LiveEvents.ChannelSynced, channelName);
        }
    }

    /// <summary>
    /// Assembles the wire response for <c>GET /emote-sets</c> (spec 6.1) from the shared list
    /// service's answer: <c>isActive</c> compares each set's id against <see cref="Channel.ActiveEmoteSetId"/>
    /// — this route's own source of "active" (E21) — and <c>observations</c> is always empty (see
    /// <see cref="EmoteSetSummaryDto"/>). Ordering: the active set first, then every other set ordinal
    /// by name.
    /// </summary>
    private static EmoteSetListResponse BuildEmoteSetListResponse(Channel channel, EmoteSetList list)
    {
        var sets = list.Sets
            .Select(summary => new EmoteSetSummaryDto(
                summary.Id,
                summary.Name,
                summary.Capacity,
                summary.Kind,
                string.Equals(summary.Id, channel.ActiveEmoteSetId, StringComparison.Ordinal),
                summary.IsPersonal,
                summary.OwnerDisplayName,
                []))
            .OrderByDescending(summary => summary.IsActive)
            .ThenBy(summary => summary.Name, StringComparer.Ordinal)
            .ToList();

        return new EmoteSetListResponse(channel.ActiveEmoteSetId, sets);
    }
}

/// <summary>
/// Wire shape of <c>GET /api/channels/{channelName}/emote-sets</c> (spec 6.1).
/// </summary>
internal sealed record EmoteSetListResponse(string ActiveEmoteSetId, IReadOnlyList<EmoteSetSummaryDto> Sets);

/// <summary>
/// One set in <see cref="EmoteSetListResponse"/>. <see cref="IsActive"/> and <see cref="IsPersonal"/>
/// are assembled at the API edge, not carried on <c>EmoteSetSummary</c> — the shared list service's
/// result serves three routes with three different notions of "active" (E21), so the flag belongs to
/// each route's own response, not to the cached value underneath all three.
/// </summary>
/// <param name="Observations">
/// Always <c>[]</c> on this branch: <c>ChannelEmoteSetObservation</c>, the entity this field is
/// specified to read from (spec 6.1), is built in K1 (T1.3a/T1.5) and does not exist here yet. K4 is
/// the only consumer (Konzept 8.4/8.5) and lands after both K1 and K2 have merged — so the field is
/// part of the wire contract now, with a value nobody reads yet, rather than a name K1 might pick
/// differently later (Vorentscheidung 1 of this task's brief).
/// </param>
internal sealed record EmoteSetSummaryDto(
    string Id,
    string Name,
    int? Capacity,
    string Kind,
    bool IsActive,
    bool IsPersonal,
    string? OwnerDisplayName,
    IReadOnlyList<EmoteSetObservationDto> Observations);

internal sealed record EmoteSetObservationDto(DateTime FromUtc, DateTime? ToUtc);

internal sealed record SyncDeletedRequest(IReadOnlyList<string> EmoteIds);

internal sealed record SyncRestoredRequest(IReadOnlyList<string> EmoteIds);

// LeaderboardSort is the wire code (SevenTvLeaderboardSortWireCode.TrendingDailyWireCode /
// TopAllTimeWireCode) carried separately from SourceChannelName (leaderboard-import spec E8): a
// sort code is not a channel name and never validates as one (ChannelNameValidation.IsValid would
// reject "TRENDING_DAILY" — the exact F1/F6-class bug this field exists to avoid, a 400 arriving
// after the 7TV mutation already happened).
// TargetEmoteSetId (spec 6.7, E5) stays optional forever: an old open tab that never learned this
// field is still a valid caller, and the audit row honestly records "no set known" rather than
// failing a mutation that already happened on 7TV. Format-checked in the handler, not by
// EmoteSetIdValidationFilter — that filter reads the query string and route values, not a body field.
internal sealed record SyncImportedRequest(
    IReadOnlyList<string> SevenTvEmoteIds, string? SourceChannelName, string SourceKind,
    string? LeaderboardSort = null, string? TargetEmoteSetId = null);
