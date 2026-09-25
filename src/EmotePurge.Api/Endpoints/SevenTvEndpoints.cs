using System.Diagnostics;
using EmotePurge.Api.Auth;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
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
            bool refresh = false, // query string; a C# default is what makes minimal API treat it
                                  // as optional instead of answering a plain request with 400
            string? emoteSetId = null) =>
        {
            // refresh=true (T2, spec E3) bypasses the hardening decorator's 60 s cache but still
            // passes through the same rate-limit policy and the same circuit breaker — no separate
            // policy was ever needed for it.
            //
            // emoteSetId (spec 6.4/E8): the set-ID read mode. No identity resolution runs at all on
            // this path — neither Helix nor the 7TV userByConnection lookup — so channelName is
            // echoed onto the response, never used to look anything up.
            var result = emoteSetId is not null
                ? await foreignEmoteSetService.GetForeignEmoteSetBySetIdAsync(channelName, emoteSetId, refresh, ct)
                : await foreignEmoteSetService.GetForeignEmoteSetAsync(channelName, refresh, ct);

            // Mirrors the state table in spec section 5 one-to-one. SevenTvRateLimited and
            // SevenTvUnavailable deliberately share a branch and a code: the table has one row for
            // "7TV nicht erreichbar / 429", because the caller cannot act on the two any differently.
            // NoActiveEmoteSet also covers the set-ID mode's "7TV kennt dieses Set nicht" (spec 6.4,
            // Vorentscheidung 4) — same code, same reasoning: a caller cannot act on "unknown id"
            // differently from "no active set configured".
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
                _ => throw new UnreachableException(
                    $"Unexpected {nameof(ForeignEmoteSetLookupStatus)} value: {result.Status}.")
            };
        })
        // Spec 6.4/E14: format-validated ahead of the handler, same idiom as ChannelNameValidationFilter
        // above.
        .AddEndpointFilter<EmoteSetIdValidationFilter>();

        // GET /api/seventv/channels/{channelName}/emote-sets (spec 6.3/K3): the source-set picker's
        // list route. A route sibling of the /emotes group above (registered on `app`, not `group`)
        // for the same reason EmoteEndpoints' own tracked-channel /emote-sets route is a sibling of
        // its /emotes group rather than nested under it — the group's own prefix would otherwise turn
        // this into /emotes/emote-sets. Same filter chain and policy as the group above, because it
        // is the same "any logged-in caller, no role in the channel" contract, just for the set list
        // instead of the active set's preview.
        app.MapGet("/api/seventv/channels/{channelName}/emote-sets", async (
            string channelName,
            IForeignEmoteSetService foreignEmoteSetService,
            CancellationToken ct) =>
        {
            var result = await foreignEmoteSetService.GetForeignEmoteSetListAsync(channelName, ct);

            // Mirrors the state table this route shares with GET …/emotes above (spec 6.3: "Zustände
            // wie GET …/emotes") — including NoSevenTvAccount answering 404 here, not the 200-with-
            // empty-list a *tracked* channel's own /emote-sets route (EmoteEndpoints, spec 6.1)
            // answers for the same underlying EmoteSetListStatus.NoSevenTvAccount.
            return result.Status switch
            {
                ForeignEmoteSetListLookupStatus.Ok => Results.Ok(BuildForeignEmoteSetListResponse(result.List!)),
                ForeignEmoteSetListLookupStatus.ChannelNotOnTwitch =>
                    Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotOnTwitch }),
                ForeignEmoteSetListLookupStatus.TwitchUnavailable => Results.Json(
                    new { errorCode = ApiErrorCodes.ForeignChannelTwitchUnavailable },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                ForeignEmoteSetListLookupStatus.NoSevenTvAccount =>
                    Results.NotFound(new { errorCode = ApiErrorCodes.ForeignChannelNoSevenTvAccount }),
                ForeignEmoteSetListLookupStatus.SevenTvUnavailable
                    or ForeignEmoteSetListLookupStatus.SevenTvRateLimited
                    or ForeignEmoteSetListLookupStatus.ProviderBudgetExhausted => Results.Json(
                    new { errorCode = ApiErrorCodes.ForeignChannelSevenTvUnavailable },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => throw new UnreachableException(
                    $"Unexpected {nameof(ForeignEmoteSetListLookupStatus)} value: {result.Status}.")
            };
        })
        .RequireAuthorization()
        .AddEndpointFilter<ChannelNameValidationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.ForeignEmoteLookup);

        // GET /api/seventv/me/emote-set-targets (spec 6.2/E6): the target picker's own offer list —
        // the caller's own account plus every channel they hold a 7TV editor grant for. No channel
        // name in the route at all (RequireAuthorization only, like /api/channels/mine), so no
        // ChannelNameValidationFilter; ForeignEmoteLookup because opening the dialog is exactly the
        // "a caller pulls 7TV requests" case that policy exists to bound (E6: 1 + k permits per open).
        var meGroup = app.MapGroup("/api/seventv/me")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicyNames.ForeignEmoteLookup);

        meGroup.MapGet("/emote-set-targets", async (
            HttpContext httpContext,
            ISevenTvEditorService editorService,
            ISevenTvEmoteSetListService emoteSetListService,
            IChannelService channelService,
            CancellationToken ct) =>
        {
            var principal = httpContext.User.TryBuildTwitchPrincipal();
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var accounts = new List<EmoteSetTargetAccount>();
            var sevenTvUnavailable = false;

            // Own account first (spec 6.2's ordering contract): principal.TwitchUserId is enough for
            // userByConnection (E7) — no ResolveSevenTvIdentityAsync round trip needed first.
            var (ownAccount, ownUnavailable) = await ResolveEmoteSetTargetAccountAsync(
                principal.TwitchUserId, principal.TwitchLogin, isOwnAccount: true,
                emoteSetListService, channelService, ct);
            accounts.Add(ownAccount);
            sevenTvUnavailable |= ownUnavailable;

            var grantsResult = await editorService.GetEditorGrantsAsync(principal.TwitchUserId, ct);
            if (grantsResult.Status == SevenTvLookupStatus.Unavailable)
            {
                // Matches MyChannelsService's reading of the same result type: NoSevenTvAccount is a
                // complete answer ("this user edits nothing, because they have no 7TV account at
                // all"), not a degradation — only a genuine Unavailable means the editor_of list may
                // be incomplete.
                sevenTvUnavailable = true;
            }
            else if (grantsResult.Status == SevenTvLookupStatus.Ok)
            {
                var editorEntries = grantsResult.Grants!.Entries
                    .Where(entry => !string.Equals(entry.TwitchChannelId, principal.TwitchUserId, StringComparison.Ordinal))
                    .OrderBy(entry => entry.ChannelLogin, StringComparer.Ordinal);

                foreach (var entry in editorEntries)
                {
                    var (account, unavailable) = await ResolveEmoteSetTargetAccountAsync(
                        entry.TwitchChannelId, entry.ChannelLogin, isOwnAccount: false,
                        emoteSetListService, channelService, ct);
                    accounts.Add(account);
                    sevenTvUnavailable |= unavailable;
                }
            }

            // Second pass (spec 5.8/E19/AK 29-30): editable can only be decided once every account
            // of this response has been resolved — the owner of a set listed under account A may be
            // a different account B of this same response, and only after B's own list has been read
            // is B's id known to be readable. EmoteSetEditability is the exact rule
            // IImportTargetOwnershipService.CheckAsync applies for the same question (F16).
            var readableAccountSevenTvUserIds = accounts
                .Where(account => account.SevenTvUserId is not null)
                .Select(account => account.SevenTvUserId!)
                .ToHashSet(StringComparer.Ordinal);

            var accountsWithEditability = accounts
                .Select(account => account with
                {
                    Sets = account.Sets
                        .Select(set => set with
                        {
                            Editable = EmoteSetEditability.IsEditable(set.OwnerSevenTvUserId, readableAccountSevenTvUserIds),
                        })
                        .ToList(),
                })
                .ToList();

            return Results.Ok(new EmoteSetTargetsResponse(accountsWithEditability, sevenTvUnavailable));
        });

        // POST /api/seventv/emote-sets/{emoteSetId}/sync-imported (spec 6.7/E22, F7): the set-centric
        // counterpart of EmoteEndpoints' own /sync-imported — exists because a target set's account
        // need not be a channel EmotePurge tracks at all, and the channel-scoped route 404s without a
        // Channel row (F7). Bookkeeping, not ForeignEmoteLookup: like its channel-scoped sibling, the
        // 7TV mutation already happened by the time this call runs, so a spent read budget must not
        // drop the paper trail. That policy only fits because the owner check below costs no
        // unguarded 7TV request (spec section 32): it answers from the cached, budgeted set lists,
        // and its one direct lookup runs under the provider budget and breaker.
        // EmoteSetIdValidationFilter here validates the *route* value, not a query string — see the
        // filter's own remarks.
        var emoteSetGroup = app.MapGroup("/api/seventv/emote-sets/{emoteSetId}")
            .RequireAuthorization()
            .AddEndpointFilter<EmoteSetIdValidationFilter>()
            .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        emoteSetGroup.MapPost("/sync-imported", async (
            string emoteSetId,
            SyncImportedToSetRequest request,
            HttpContext httpContext,
            IImportTargetOwnershipService ownershipService,
            IEmoteService emoteService,
            CancellationToken ct) =>
        {
            // Step 3 of the 6.7 ladder: the exact same vocabulary table as the channel-scoped
            // endpoint, pulled out into one shared static method so it cannot exist twice (T2.4).
            var vocabularyError = EmoteEndpoints.ValidateSyncImportedVocabulary(
                request.SevenTvEmoteIds, request.SourceChannelName, request.SourceKind, request.LeaderboardSort);
            if (vocabularyError is not null)
            {
                return Results.BadRequest(new { errorCode = vocabularyError });
            }

            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            // Step 4: does the actor own emoteSetId, or hold a 7TV editor grant on its owner?
            var ownership = await ownershipService.CheckAsync(actor.TwitchUserId, actor.Login, emoteSetId, ct);

            switch (ownership.Status)
            {
                case SevenTvEmoteSetOwnershipStatus.SetNotFound:
                    return Results.NotFound(new { errorCode = ApiErrorCodes.EmoteSetNotFound });
                case SevenTvEmoteSetOwnershipStatus.Forbidden:
                    // Bare Forbid(), like the four existing IEndpointFilter-based authorization
                    // filters (spec 6.7) — no error-code body, since "you may not do this" needs no
                    // further explanation a caller could act on.
                    return Results.Forbid();
                case SevenTvEmoteSetOwnershipStatus.Unavailable:
                    // No audit entry on this branch: nothing was determined, let alone imported.
                    return Results.Json(
                        new { errorCode = ApiErrorCodes.ForeignChannelSevenTvUnavailable },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // Step 5: the service call is the only place that writes the audit row — ChannelName =
            // null, TargetType = "emoteSet" (spec 6.7).
            await emoteService.MarkImportedToSetAsync(
                emoteSetId, ownership.OwnerSevenTvUserId!, ownership.OwnerTwitchLogin!, request.SevenTvEmoteIds,
                request.SourceChannelName, request.SourceKind, request.LeaderboardSort, actor, ct);

            return Results.NoContent();
        });

        // POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted and .../sync-restored (restore-per-set
        // spec 5.1, E3): the set-centric reports of a delete, a restore and a replace's removals. The
        // reported set, not a channel, is the subject — every tracked channel with that set active is
        // written, and an untracked set still leaves a paper entry. Authorized like sync-imported
        // above, by the owner check rather than the channel role (DECISIONS 2026-09-25, "Who may
        // report is decided by 7TV editing rights"), and on Bookkeeping for the same reason. The two
        // handlers share their ladder (stages 3-4) and their aftermath (stages 6-7); only the service
        // call and the name of the count on the wire differ.
        emoteSetGroup.MapPost("/sync-deleted", async (
            string emoteSetId,
            SyncInSetRequest request,
            HttpContext httpContext,
            IImportTargetOwnershipService ownershipService,
            IEmoteService emoteService,
            IRedisPublisher redisPublisher,
            IChannelResyncCooldown resyncCooldown,
            IChannelService channelService,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var ladder = await PassSyncInSetLadderAsync(emoteSetId, request, httpContext, ownershipService, ct);
            if (ladder.Rejection is not null)
            {
                return ladder.Rejection;
            }

            // Stage 5.
            var result = await emoteService.MarkDeletedInSetAsync(
                emoteSetId, ladder.OwnerSevenTvUserId!, ladder.OwnerTwitchLogin!, ladder.OwnerTwitchUserId!, request.SevenTvEmoteIds!,
                ladder.ExpectedChannelName, ladder.Actor!, ct);
            var resyncTriggered = await PublishAndResyncAfterSyncInSetAsync(
                result.Channels, result.UnresolvedChannel, ladder.Actor!, redisPublisher, resyncCooldown, channelService, logger);

            return Results.Ok(new SyncDeletedInSetResponse(
                result.ReportedCount,
                [.. result.Channels.Select(channel => new SyncDeletedInSetChannelResponse(channel.ChannelName, channel.Count, channel.NotFoundIds))],
                ToUnresolvedChannelResponse(result.UnresolvedChannel),
                resyncTriggered));
        });

        emoteSetGroup.MapPost("/sync-restored", async (
            string emoteSetId,
            SyncInSetRequest request,
            HttpContext httpContext,
            IImportTargetOwnershipService ownershipService,
            IEmoteService emoteService,
            IRedisPublisher redisPublisher,
            IChannelResyncCooldown resyncCooldown,
            IChannelService channelService,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var ladder = await PassSyncInSetLadderAsync(emoteSetId, request, httpContext, ownershipService, ct);
            if (ladder.Rejection is not null)
            {
                return ladder.Rejection;
            }

            // Stage 5.
            var result = await emoteService.MarkRestoredInSetAsync(
                emoteSetId, ladder.OwnerSevenTvUserId!, ladder.OwnerTwitchLogin!, ladder.OwnerTwitchUserId!, request.SevenTvEmoteIds!,
                ladder.ExpectedChannelName, ladder.Actor!, ct);
            var resyncTriggered = await PublishAndResyncAfterSyncInSetAsync(
                result.Channels, result.UnresolvedChannel, ladder.Actor!, redisPublisher, resyncCooldown, channelService, logger);

            return Results.Ok(new SyncRestoredInSetResponse(
                result.ReportedCount,
                [.. result.Channels.Select(channel => new SyncRestoredInSetChannelResponse(channel.ChannelName, channel.Count, channel.NotFoundIds))],
                ToUnresolvedChannelResponse(result.UnresolvedChannel),
                resyncTriggered));
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
                _ => throw new UnreachableException(
                    $"Unexpected {nameof(SevenTvLeaderboardStatus)} value: {result.Status}.")
            };
        });
    }

    /// <summary>
    /// One account's answer for <c>GET /me/emote-set-targets</c> (spec 6.2): resolves whether it maps
    /// to one of our tracked, bot-active channels, then its set list from the same
    /// <see cref="ISevenTvEmoteSetListService"/> the other two list routes share. The bool half of the
    /// tuple is exactly <see cref="EmoteSetTargetAccount.SetsUnavailable"/> — returned alongside
    /// rather than read back off the account afterwards, so the caller can OR it into the response's
    /// overall <c>sevenTvUnavailable</c> without re-deriving it from the DTO.
    /// </summary>
    private static async Task<(EmoteSetTargetAccount Account, bool Unavailable)> ResolveEmoteSetTargetAccountAsync(
        string twitchChannelId,
        string twitchLogin,
        bool isOwnAccount,
        ISevenTvEmoteSetListService emoteSetListService,
        IChannelService channelService,
        CancellationToken cancellationToken)
    {
        var trackedChannel = await channelService.GetActiveByTwitchChannelIdAsync(twitchChannelId, cancellationToken);
        var result = await emoteSetListService.ListByTwitchIdAsync(twitchChannelId, cancellationToken);

        // E7/E21: a tracked channel's own observed state always wins over 7TV's opinion of "active" —
        // computed once, the same way regardless of what the list lookup below answered, because a
        // tracked channel's ActiveEmoteSetId comes from our database, not from this request's 7TV call.
        var activeEmoteSetId = trackedChannel?.ActiveEmoteSetId
            ?? (result.Status == EmoteSetListStatus.Ok ? result.List!.SevenTvActiveEmoteSetId : null);

        if (result.Status == EmoteSetListStatus.Ok)
        {
            var sets = result.List!.Sets
                .Select(summary => ToEmoteSetTargetSummary(
                    summary, string.Equals(summary.Id, activeEmoteSetId, StringComparison.Ordinal)))
                .OrderByDescending(summary => summary.IsActive)
                .ThenBy(summary => summary.Name, StringComparer.Ordinal)
                .ToList();

            // Spec 5.8: the account's own 7TV id, only set when its list was actually read — the
            // second pass below treats this as "this id may own an editable set". 7TV's answer can
            // in principle omit it even on a successful list (E7's "always set on a list read from
            // 7TV" is what 7TV normally does, not a guarantee this code relies on).
            return (new EmoteSetTargetAccount(
                twitchChannelId, twitchLogin, isOwnAccount, trackedChannel?.ChannelName,
                activeEmoteSetId, sets, SetsUnavailable: false, result.List!.SevenTvUserId), false);
        }

        if (result.Status == EmoteSetListStatus.NoSevenTvAccount)
        {
            // An answer, not a failure (spec 6.1's state table, reused here): this account genuinely
            // has no 7TV account, so an empty set list is correct, not degraded.
            return (new EmoteSetTargetAccount(
                twitchChannelId, twitchLogin, isOwnAccount, trackedChannel?.ChannelName,
                activeEmoteSetId, [], SetsUnavailable: false, SevenTvUserId: null), false);
        }

        return (new EmoteSetTargetAccount(
            twitchChannelId, twitchLogin, isOwnAccount, trackedChannel?.ChannelName,
            activeEmoteSetId, [], SetsUnavailable: true, SevenTvUserId: null), true);
    }

    /// <summary>
    /// <c>Editable</c> is a placeholder here (spec 5.8) — the handler's second pass recomputes it
    /// once every account of the response is known, because a set's owner may be a different
    /// account of the same response whose list has not been resolved yet at this point.
    /// </summary>
    private static EmoteSetTargetSummaryDto ToEmoteSetTargetSummary(EmoteSetSummary summary, bool isActive) => new(
        summary.Id, summary.Name, summary.Capacity, summary.Kind, isActive, summary.IsPersonal, summary.OwnerDisplayName,
        summary.OwnerSevenTvUserId, Editable: false);

    /// <summary>
    /// Assembles the wire response for <c>GET /api/seventv/channels/{channelName}/emote-sets</c>
    /// (spec 6.3) from the shared list service's answer — deliberately the same shape as
    /// <c>EmoteEndpoints.EmoteSetListResponse</c> (6.1), reusing that file's <c>EmoteSetSummaryDto</c>
    /// (both <c>internal</c>, same assembly) rather than a parallel type, so the frontend's single
    /// <c>EmoteSetListResponse</c> TypeScript model serves both routes without knowing which produced
    /// it. <c>isActive</c> compares each set's id against <c>list.SevenTvActiveEmoteSetId</c> — 7TV's
    /// own opinion (E21), never a <c>Channel</c> row, which this path never resolves at all (spec
    /// 6.3). <c>observations</c> is always <c>[]</c>: <c>ChannelEmoteSetObservation</c> only exists
    /// for channels we track, and this route by definition never is one. A <c>null</c>
    /// <see cref="EmoteSetList.SevenTvActiveEmoteSetId"/> becomes <c>""</c> on the wire — the same
    /// "no known active set" spelling 6.1 already uses for <c>Channel.ActiveEmoteSetId</c> before the
    /// first sync — so the frontend's non-nullable <c>activeEmoteSetId: string</c> holds for both
    /// routes without a second, nullable variant.
    /// </summary>
    private static ForeignEmoteSetListResponse BuildForeignEmoteSetListResponse(EmoteSetList list)
    {
        var activeEmoteSetId = list.SevenTvActiveEmoteSetId ?? string.Empty;
        var sets = list.Sets
            .Select(summary => new EmoteSetSummaryDto(
                summary.Id,
                summary.Name,
                summary.Capacity,
                summary.Kind,
                string.Equals(summary.Id, activeEmoteSetId, StringComparison.Ordinal),
                summary.IsPersonal,
                summary.OwnerDisplayName,
                []))
            .OrderByDescending(summary => summary.IsActive)
            .ThenBy(summary => summary.Name, StringComparer.Ordinal)
            .ToList();

        return new ForeignEmoteSetListResponse(activeEmoteSetId, sets);
    }

    /// <summary>
    /// Stages 3-4 of the set-centric <c>sync-deleted</c>/<c>sync-restored</c> ladder (restore-per-set
    /// spec 5.1): the body, then the owner check. A non-null <see cref="SyncInSetLadder.Rejection"/>
    /// is the answer; nothing was reported, audited or resynced on any of those exits. Otherwise the
    /// actor, the resolved owner identity (7TV id, Twitch login and Twitch id) and the normalized expected channel (Regel 9) are set.
    /// </summary>
    private static async Task<SyncInSetLadder> PassSyncInSetLadderAsync(
        string emoteSetId,
        SyncInSetRequest request,
        HttpContext httpContext,
        IImportTargetOwnershipService ownershipService,
        CancellationToken ct)
    {
        if (request.SevenTvEmoteIds is not { Count: > 0 })
        {
            return SyncInSetLadder.Reject(Results.BadRequest(new { errorCode = ApiErrorCodes.EmoteIdsEmpty }));
        }

        if (request.ExpectedChannelName is not null && !ChannelNameValidation.IsValid(request.ExpectedChannelName))
        {
            return SyncInSetLadder.Reject(Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName }));
        }

        var actor = httpContext.User.TryBuildAuditActor();
        if (actor is null)
        {
            return SyncInSetLadder.Reject(Results.Unauthorized());
        }

        // The same owner check and the same three exits as sync-imported: 404 with a code, a bare
        // 403, and 503 when 7TV could not be asked.
        var ownership = await ownershipService.CheckAsync(actor.TwitchUserId, actor.Login, emoteSetId, ct);
        switch (ownership.Status)
        {
            case SevenTvEmoteSetOwnershipStatus.SetNotFound:
                return SyncInSetLadder.Reject(Results.NotFound(new { errorCode = ApiErrorCodes.EmoteSetNotFound }));
            case SevenTvEmoteSetOwnershipStatus.Forbidden:
                return SyncInSetLadder.Reject(Results.Forbid());
            case SevenTvEmoteSetOwnershipStatus.Unavailable:
                return SyncInSetLadder.Reject(Results.Json(
                    new { errorCode = ApiErrorCodes.ForeignChannelSevenTvUnavailable },
                    statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        var expectedChannelName = request.ExpectedChannelName is null ? null : ChannelName.Normalize(request.ExpectedChannelName);
        return new SyncInSetLadder(
            null, actor, ownership.OwnerSevenTvUserId, ownership.OwnerTwitchLogin, ownership.OwnerTwitchUserId, expectedChannelName);
    }

    /// <summary>
    /// Stages 6-7 of the set-centric report (restore-per-set spec 5.1, 5.4, E17): one
    /// <c>channel.synced</c> per channel whose rows this call actually changed, then one guarded
    /// resync per hit channel and for an <c>activeSetDiffers</c> mismatch — never for
    /// <c>notTracked</c>, which has nothing to resync and must not behave differently for a blocked
    /// channel. Returns the channels whose resync was triggered (<c>resyncTriggered</c>).
    /// </summary>
    /// <remarks>
    /// The resync is what heals a report that lied (F12): the reported rows change at once, on the
    /// strength of cached rights, and the worker's read of 7TV puts every row back to the truth. That
    /// is why none of these steps take the request's token — like the live event, they run after the
    /// write is committed, and a client hanging up right after the report must not skip its resync.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> PublishAndResyncAfterSyncInSetAsync(
        IReadOnlyList<SyncInSetChannelResultDto> channels,
        UnresolvedChannelDto? unresolvedChannel,
        AuditActor actor,
        IRedisPublisher redisPublisher,
        IChannelResyncCooldown resyncCooldown,
        IChannelService channelService,
        ILogger logger)
    {
        foreach (var channel in channels.Where(channel => channel.NewlyChangedCount > 0))
        {
            await EmoteEndpoints.PublishChannelSyncedAsync(redisPublisher, logger, channel.ChannelName);
        }

        var resyncCandidates = channels.Select(channel => channel.ChannelName).ToList();
        if (unresolvedChannel is { Reason: UnresolvedChannelReasons.ActiveSetDiffers })
        {
            resyncCandidates.Add(unresolvedChannel.ChannelName);
        }

        var resyncTriggered = new List<string>();
        foreach (var channelName in resyncCandidates)
        {
            if (await TryTriggerGuardedResyncAsync(channelName, actor, resyncCooldown, channelService, logger))
            {
                resyncTriggered.Add(channelName);
            }
        }

        return resyncTriggered;
    }

    /// <summary>
    /// One channel's resync under the per-channel cooldown, the same claim-trigger-release sequence as
    /// <c>POST /api/channels/{channelName}/resync</c> (<see cref="ChannelEndpoints"/>). A cooldown that
    /// is not acquired means a resync ran within the window and its result is on the way (F15) — no
    /// error, just <c>false</c>. A resync that was claimed but not triggered (channel gone or no
    /// longer active) hands its slot back.
    /// </summary>
    /// <remarks>
    /// A failure here is logged and swallowed, like the live event's: the report is committed, and
    /// answering it with a 500 would make the client retry a report that already succeeded. The
    /// worker's periodic resync still reaches the channel within its next tick.
    /// <para>
    /// <c>internal</c> because the legacy Guid-keyed <c>sync-deleted</c>/<c>sync-restored</c> in
    /// <see cref="EmoteEndpoints"/> reuses this same stage 7 (restore-per-set spec 5.6, E4) rather
    /// than duplicating the claim/trigger/release sequence a third time.
    /// </para>
    /// </remarks>
    internal static async Task<bool> TryTriggerGuardedResyncAsync(
        string channelName,
        AuditActor actor,
        IChannelResyncCooldown resyncCooldown,
        IChannelService channelService,
        ILogger logger)
    {
        var cooldownAcquired = false;
        try
        {
            var cooldown = await resyncCooldown.TryBeginAsync(channelName, CancellationToken.None);
            if (!cooldown.Acquired)
            {
                return false;
            }

            cooldownAcquired = true;
            var result = await channelService.TriggerResyncAsync(channelName, actor, CancellationToken.None);
            if (result == ChannelResyncResult.Triggered)
            {
                return true;
            }

            await resyncCooldown.ReleaseAsync(channelName, CancellationToken.None);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Resync after a set-centric report could not be triggered for {Channel}.", channelName);

            // The claim already succeeded — a throw from TriggerResyncAsync must still hand the
            // slot back, or a broadcaster who just hit a transient error is locked out of a real
            // retry for the rest of the cooldown window. Wrapped so a release failure cannot turn
            // an already-committed report into a 500 either.
            if (cooldownAcquired)
            {
                try
                {
                    await resyncCooldown.ReleaseAsync(channelName, CancellationToken.None);
                }
                catch (Exception releaseEx)
                {
                    logger.LogWarning(releaseEx, "Releasing the resync cooldown after a failed trigger also failed for {Channel}.", channelName);
                }
            }

            return false;
        }
    }

    private static UnresolvedChannelResponse? ToUnresolvedChannelResponse(UnresolvedChannelDto? unresolvedChannel) =>
        unresolvedChannel is null ? null : new UnresolvedChannelResponse(unresolvedChannel.ChannelName, unresolvedChannel.Reason);

    /// <summary>
    /// What <see cref="PassSyncInSetLadderAsync"/> hands back: either a <see cref="Rejection"/>, or
    /// everything stage 5 needs — never both.
    /// </summary>
    private sealed record SyncInSetLadder(
        IResult? Rejection,
        AuditActor? Actor,
        string? OwnerSevenTvUserId,
        string? OwnerTwitchLogin,
        string? OwnerTwitchUserId,
        string? ExpectedChannelName)
    {
        public static SyncInSetLadder Reject(IResult rejection) => new(rejection, null, null, null, null, null);
    }
}

/// <summary>
/// Wire shape of <c>GET /api/seventv/channels/{channelName}/emote-sets</c> (spec 6.3) — see
/// <see cref="SevenTvEndpoints.BuildForeignEmoteSetListResponse"/> for why this mirrors
/// <c>EmoteEndpoints.EmoteSetListResponse</c> (6.1) rather than sharing its literal C# type (the two
/// are top-level types in the same namespace and file-scoped elsewhere in this codebase, so the name
/// cannot be reused verbatim — only the wire shape has to match, which it does property for
/// property).
/// </summary>
internal sealed record ForeignEmoteSetListResponse(string ActiveEmoteSetId, IReadOnlyList<EmoteSetSummaryDto> Sets);

/// <summary>Wire shape of <c>GET /api/seventv/me/emote-set-targets</c> (spec 6.2).</summary>
internal sealed record EmoteSetTargetsResponse(IReadOnlyList<EmoteSetTargetAccount> Accounts, bool SevenTvUnavailable);

/// <param name="TwitchLogin">
/// The grant's own copy of the login (or the principal's, for the own account) — sorting and paper
/// trail (spec 6.7), never the displayed owner: that is <see cref="EmoteSetTargetSummaryDto.OwnerDisplayName"/>
/// on each set.
/// </param>
/// <param name="TrackedChannelName">
/// Non-null exactly when a <c>Channel</c> row with this Twitch id has <c>IsBotActive = true</c>; the
/// picker's tracked/untracked grouping reads this field alone (spec 6.2).
/// </param>
/// <param name="SevenTvUserId">
/// <c>userByConnection.id</c> of this account (spec 2026-09-24 restore-per-set addendum, 5.8) — set
/// only when this account's own set list was read successfully; <c>null</c> for
/// <c>SetsUnavailable</c> and for an account with no 7TV account at all. The other half of
/// <c>Editable</c> on each set below: a set is editable when its <c>ownerSevenTvUserId</c> equals
/// the <c>sevenTvUserId</c> of one readable account of this same response.
/// </param>
internal sealed record EmoteSetTargetAccount(
    string TwitchChannelId,
    string TwitchLogin,
    bool IsOwnAccount,
    string? TrackedChannelName,
    string? ActiveEmoteSetId,
    IReadOnlyList<EmoteSetTargetSummaryDto> Sets,
    bool SetsUnavailable,
    string? SevenTvUserId);

/// <summary>
/// Same shape as <c>EmoteSetSummaryDto</c> (<c>EmoteEndpoints.cs</c>) minus <c>observations</c> —
/// spec 6.2 carries it without that field, since the target picker never reads per-set history.
/// </summary>
/// <param name="OwnerSevenTvUserId">
/// <c>owner.id</c> of this set (spec 2026-09-24 restore-per-set addendum, 5.8); <c>null</c> when
/// 7TV reported no owner. On the wire so a test can recompute <see cref="Editable"/>, not so the
/// frontend rebuilds the rule — the frontend reads <see cref="Editable"/>, it never derives it.
/// </param>
/// <param name="Editable">
/// True exactly when <see cref="OwnerSevenTvUserId"/> is the <c>SevenTvUserId</c> of one account of
/// this response whose own list was read successfully — computed by
/// <c>EmoteSetEditability.IsEditable</c>, the same rule
/// <c>IImportTargetOwnershipService.CheckAsync</c> applies for the set-centric import's owner check
/// (F16: a set with no owner id is never editable here, even though a live 7TV lookup might find
/// one for that check).
/// </param>
internal sealed record EmoteSetTargetSummaryDto(
    string Id, string Name, int? Capacity, string Kind, bool IsActive, bool IsPersonal, string? OwnerDisplayName,
    string? OwnerSevenTvUserId, bool Editable);

/// <summary>
/// Body of <c>POST /api/seventv/emote-sets/{emoteSetId}/sync-imported</c> (spec 6.7) — the same
/// shape as <c>EmoteEndpoints.SyncImportedRequest</c> minus <c>TargetEmoteSetId</c>: the route
/// already carries the target set, so repeating it in the body would just be a second, potentially
/// disagreeing source of truth for the same value.
/// </summary>
internal sealed record SyncImportedToSetRequest(
    IReadOnlyList<string> SevenTvEmoteIds, string? SourceChannelName, string SourceKind, string? LeaderboardSort = null);

/// <summary>
/// Body of <c>POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted</c> and <c>…/sync-restored</c>
/// (restore-per-set spec 5.1). No <c>emoteSetId</c> in the body, for the same reason as
/// <see cref="SyncImportedToSetRequest"/>: the route carries it.
/// </summary>
/// <param name="SevenTvEmoteIds">The 7TV ids the client mutated; missing or empty is 400 <c>emote_ids_empty</c>.</param>
/// <param name="ExpectedChannelName">
/// The tracked channel the client expects to hit (spec E18) — the target account's channel when the
/// set is its active one — or <c>null</c> when it expects none. Validated with
/// <c>ChannelNameValidation.IsValid</c> and normalized before it reaches the service (Regel 9).
/// </param>
internal sealed record SyncInSetRequest(IReadOnlyList<string>? SevenTvEmoteIds, string? ExpectedChannelName);

/// <summary>
/// Answer of <c>POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted</c> (restore-per-set spec 5.3).
/// <see cref="Channels"/> empty and <see cref="UnresolvedChannel"/> null together mean "paper only".
/// </summary>
/// <param name="ReportedCount">The reported 7TV ids after ordinal deduplication.</param>
/// <param name="Channels">The hit tracked channels, ordinal by name.</param>
/// <param name="UnresolvedChannel">The expected channel when it was not hit, with the reason; otherwise <c>null</c>.</param>
/// <param name="ResyncTriggered">
/// The channels this call actually triggered a resync for (stage 7). A channel whose cooldown was
/// held is absent — a resync is on its way already, and the client must not start another (F15).
/// </param>
internal sealed record SyncDeletedInSetResponse(
    int ReportedCount,
    IReadOnlyList<SyncDeletedInSetChannelResponse> Channels,
    UnresolvedChannelResponse? UnresolvedChannel,
    IReadOnlyList<string> ResyncTriggered);

/// <summary>One hit channel of <see cref="SyncDeletedInSetResponse"/>: rows found (archived now or before) and the ids without a row there.</summary>
internal sealed record SyncDeletedInSetChannelResponse(string ChannelName, int ArchivedCount, IReadOnlyList<string> NotFoundIds);

/// <summary>Mirror of <see cref="SyncDeletedInSetResponse"/> for <c>…/sync-restored</c>.</summary>
internal sealed record SyncRestoredInSetResponse(
    int ReportedCount,
    IReadOnlyList<SyncRestoredInSetChannelResponse> Channels,
    UnresolvedChannelResponse? UnresolvedChannel,
    IReadOnlyList<string> ResyncTriggered);

/// <summary>One hit channel of <see cref="SyncRestoredInSetResponse"/>.</summary>
internal sealed record SyncRestoredInSetChannelResponse(string ChannelName, int RestoredCount, IReadOnlyList<string> NotFoundIds);

/// <summary>
/// The expected channel the report missed (spec E18): <c>reason</c> is <c>notTracked</c> (missing,
/// left or blocked — the block is never named) or <c>activeSetDiffers</c> (tracked, but its stored
/// active set is another one, F13).
/// </summary>
internal sealed record UnresolvedChannelResponse(string ChannelName, string Reason);
