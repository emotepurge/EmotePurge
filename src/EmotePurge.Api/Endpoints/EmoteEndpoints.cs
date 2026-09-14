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
            if (request.SevenTvEmoteIds is null || request.SevenTvEmoteIds.Count == 0)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdsEmpty });
            }

            // Ordinal and strictly lower-case (F3, import plan): the only caller is our own
            // frontend, so a silent case-insensitive fallback would hide a frontend bug rather than
            // surfacing it.
            // "seventv-channel" is the third member (foreign-import spec E6/F5.1): a channel
            // EmotePurge does not track, read straight from 7TV. "seventv-leaderboard" is the fourth
            // (leaderboard-import spec E8/F1): a network-wide 7TV ranking, which has no source
            // channel at all — its origin travels in LeaderboardSort instead (see the vocabulary
            // table below). Every word is deliberately its own rather than folded into an existing
            // one — they are read through different paths and an audit row must still say which one
            // it was, forever. Adding a word here is never enough on its own:
            // AuditLogQueryService.ProjectDetail has to learn it too, or every row written with it
            // silently loses its provenance (F5.3/F1 Station 5).
            if (request.SourceKind is not ("channel" or "file" or "seventv-channel" or "seventv-leaderboard"))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidSourceKind });
            }

            // SourceChannelName is attacker-controlled free text that ends up in jsonb forever
            // (R6, import plan) — validated like every other inbound channel name, but only when the
            // caller actually set one; the kind-versus-name agreement is checked just below.
            if (request.SourceChannelName is not null && !ChannelNameValidation.IsValid(request.SourceChannelName))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
            }

            // The kind decides what else may be set, in both directions. Audit rows are write-once
            // and kept forever, so an inconsistent body would leave a permanently wrong entry:
            // "channel"/"seventv-channel" without a name claims an origin they cannot name, "file"
            // or "seventv-leaderboard" with one gets filed under a channel origin the import never
            // had, and any kind other than "seventv-leaderboard" carrying a LeaderboardSort claims a
            // ranking it did not come from. This used to be a single binary check ("file versus
            // not-file"), which covered "seventv-channel" correctly only by accident (F5.2) and
            // predicted its own failure for a fourth, source-less kind (comment removed above) —
            // this vocabulary table (leaderboard-import spec F1 Station 3) is that fourth kind's
            // answer. A LeaderboardSort that is present but outside 7TV's own sort vocabulary is its
            // own error, invalid_leaderboard_sort, because it is not "the wrong kind of import" —
            // the kind is right, the sort code just is not one the endpoint knows how to hand to 7TV.
            var isChannelSourceKind = request.SourceKind is "channel" or "seventv-channel";
            if (isChannelSourceKind
                && (string.IsNullOrWhiteSpace(request.SourceChannelName) || request.LeaderboardSort is not null))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidSourceKind });
            }

            if (request.SourceKind == "file"
                && (!string.IsNullOrWhiteSpace(request.SourceChannelName) || request.LeaderboardSort is not null))
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidSourceKind });
            }

            if (request.SourceKind == "seventv-leaderboard")
            {
                if (!string.IsNullOrWhiteSpace(request.SourceChannelName) || request.LeaderboardSort is null)
                {
                    return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidSourceKind });
                }

                if (!SevenTvLeaderboardSortWireCode.TryParse(request.LeaderboardSort, out _))
                {
                    return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidLeaderboardSort });
                }
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var written = await emoteService.MarkImportedAsync(
                channelName, request.SevenTvEmoteIds, request.SourceChannelName, request.SourceKind,
                request.LeaderboardSort, actor, ct);
            return written ? Results.NoContent() : Results.NotFound();
        })
        // Same reasoning as its two neighbors above: the emotes were already imported on 7TV by the
        // time this call runs, so a 429 here would only cost the paper trail, not correctness.
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapGet("/set-warning", async (
            string channelName,
            HttpContext httpContext,
            IEmoteSetOwnershipService emoteSetOwnershipService,
            CancellationToken ct) =>
        {
            var principal = httpContext.User.TryBuildTwitchPrincipal();
            var warning = await emoteSetOwnershipService.CheckAsync(channelName, principal, ct);
            return Results.Ok(warning);
        });

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

        // Same audience as active-set: a name collision silently folds the usage of all but one
        // of the colliding emotes into a single counter, so it distorts exactly the numbers those
        // pages show — and fixing it happens on 7TV, which editors can do without being allowed
        // to manage the channel here.
        group.MapGet("/duplicate-names", async (
            string channelName,
            IDuplicateEmoteNameQueryService duplicateEmoteNameQueryService,
            CancellationToken ct) =>
        {
            var duplicates = await duplicateEmoteNameQueryService.GetAsync(channelName, ct);
            return duplicates is null ? Results.NotFound() : Results.Ok(duplicates);
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
}

internal sealed record SyncDeletedRequest(IReadOnlyList<string> EmoteIds);

internal sealed record SyncRestoredRequest(IReadOnlyList<string> EmoteIds);

// LeaderboardSort is the wire code (SevenTvLeaderboardSortWireCode.TrendingDailyWireCode /
// TopAllTimeWireCode) carried separately from SourceChannelName (leaderboard-import spec E8): a
// sort code is not a channel name and never validates as one (ChannelNameValidation.IsValid would
// reject "TRENDING_DAILY" — the exact F1/F6-class bug this field exists to avoid, a 400 arriving
// after the 7TV mutation already happened).
internal sealed record SyncImportedRequest(
    IReadOnlyList<string> SevenTvEmoteIds, string? SourceChannelName, string SourceKind, string? LeaderboardSort = null);
