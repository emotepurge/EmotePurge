using System.Diagnostics;
using EmotePurge.Api.Auth;
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

            return Results.Ok(new EmoteSetTargetsResponse(accounts, sevenTvUnavailable));
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

            return (new EmoteSetTargetAccount(
                twitchChannelId, twitchLogin, isOwnAccount, trackedChannel?.ChannelName,
                activeEmoteSetId, sets, SetsUnavailable: false), false);
        }

        if (result.Status == EmoteSetListStatus.NoSevenTvAccount)
        {
            // An answer, not a failure (spec 6.1's state table, reused here): this account genuinely
            // has no 7TV account, so an empty set list is correct, not degraded.
            return (new EmoteSetTargetAccount(
                twitchChannelId, twitchLogin, isOwnAccount, trackedChannel?.ChannelName,
                activeEmoteSetId, [], SetsUnavailable: false), false);
        }

        return (new EmoteSetTargetAccount(
            twitchChannelId, twitchLogin, isOwnAccount, trackedChannel?.ChannelName,
            activeEmoteSetId, [], SetsUnavailable: true), true);
    }

    private static EmoteSetTargetSummaryDto ToEmoteSetTargetSummary(EmoteSetSummary summary, bool isActive) => new(
        summary.Id, summary.Name, summary.Capacity, summary.Kind, isActive, summary.IsPersonal, summary.OwnerDisplayName);

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
internal sealed record EmoteSetTargetAccount(
    string TwitchChannelId,
    string TwitchLogin,
    bool IsOwnAccount,
    string? TrackedChannelName,
    string? ActiveEmoteSetId,
    IReadOnlyList<EmoteSetTargetSummaryDto> Sets,
    bool SetsUnavailable);

/// <summary>
/// Same shape as <c>EmoteSetSummaryDto</c> (<c>EmoteEndpoints.cs</c>) minus <c>observations</c> —
/// spec 6.2 carries it without that field, since the target picker never reads per-set history.
/// </summary>
internal sealed record EmoteSetTargetSummaryDto(
    string Id, string Name, int? Capacity, string Kind, bool IsActive, bool IsPersonal, string? OwnerDisplayName);

/// <summary>
/// Body of <c>POST /api/seventv/emote-sets/{emoteSetId}/sync-imported</c> (spec 6.7) — the same
/// shape as <c>EmoteEndpoints.SyncImportedRequest</c> minus <c>TargetEmoteSetId</c>: the route
/// already carries the target set, so repeating it in the body would just be a second, potentially
/// disagreeing source of truth for the same value.
/// </summary>
internal sealed record SyncImportedToSetRequest(
    IReadOnlyList<string> SevenTvEmoteIds, string? SourceChannelName, string SourceKind, string? LeaderboardSort = null);
