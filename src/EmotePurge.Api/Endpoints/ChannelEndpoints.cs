using EmotePurge.Api.Auth;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

public static class ChannelEndpoints
{
    public static void MapChannelEndpoints(this WebApplication app)
    {
        // The validation filter comes first so a malformed name is a 400 with invalid_channel_name
        // before any authorization filter runs — that filter order *is* the error contract (see Z4).
        // One endpoint in this group has no channelName at all ("/mine"); the filter lets it through
        // untouched.
        var group = app.MapGroup("/api/channels")
            .RequireAuthorization()
            .AddEndpointFilter<ChannelNameValidationFilter>();

        group.MapGet("/{channelName}", async (
            string channelName,
            IChannelService channelService,
            CancellationToken ct) =>
        {
            var channel = await channelService.GetByNameAsync(channelName, ct);
            return channel is null
                ? Results.NotFound()
                : Results.Ok(new { channelId = channel.Id, channelName = channel.ChannelName, channel.IsBotActive, channel.ActiveEmoteSetId });
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>()
        // Its first policy: the read itself is one indexed row, but ChannelManagementAuthorizationFilter
        // can reach Helix on a cache miss, and until now nothing bounded how often an account could
        // ask. InteractiveRead, because that is what this is — the join-status probe behind every
        // workspace header.
        .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        // The channel's own activity feed: the same audit log the global admin reads, narrowed to
        // one channel. A broadcaster could not see who on their mod team deleted a vote session
        // until this existed — that answer lived in the global-admin area only.
        //
        // Behind ChannelManagementAuthorizationFilter, *not* the wider usage-stats one: this shows
        // which named moderator did what, and the wider filter also admits the channel's 7TV
        // editors, who are frequently outside the mod team. Attribution is the sensitive part here
        // (SevenTV/Extension#267), so the audience is the mod team itself.
        group.MapGet("/{channelName}/audit-log", async (
            string channelName,
            int page,
            int pageSize,
            string? action,
            string? actor,
            IAuditLogQueryService auditLogQueryService,
            CancellationToken ct) =>
        {
            var (effectivePage, effectivePageSize) = PagingQuery.Clamp(page, pageSize);

            // The channel comes from the route value and from nowhere else. No `channel` query
            // parameter is bound on purpose: binding one would let a caller authorized for their own
            // channel read another channel's log through their own authorized route.
            var filter = new AuditLogFilter(
                PagingQuery.NullIfBlank(action),
                channelName,
                PagingQuery.NullIfBlank(actor));

            var result = await auditLogQueryService.ListAsync(effectivePage, effectivePageSize, filter, ct);
            return Results.Ok(result);
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>()
        // Pure DB read against a covering index, but it is reachable per keystroke through the
        // actor filter — Bookkeeping is the policy for "ours only, still not unlimited".
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        // Deliberately behind no authorization filter of its own: this endpoint *reports* whether the
        // caller would pass those filters, so gating it with one would make it answer only for people
        // who already know the answer. It replaces four separate UI probes that each called a real
        // endpoint and read its success/failure as a permission bit — a navigation to
        // /channels/{c}/usage-stats used to cost four requests, three of them redundant
        // GetUsageTotalsAsync calls, before the page showed anything.
        group.MapGet("/{channelName}/permissions", async (
            string channelName,
            HttpContext httpContext,
            IChannelAccessService channelAccessService,
            IChannelService channelService,
            CancellationToken ct) =>
        {
            var principal = httpContext.User.TryBuildTwitchPrincipal();
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var canManage = await channelAccessService.CanManageChannelAsync(principal, channelName, ct);
            // Short-circuited on purpose: CanViewUsageStatsAsync is a superset of CanManageChannelAsync
            // and its extra branch is the 7TV editor lookup. Calling it unconditionally would drag 7TV
            // into the request path for every manager, who never needs it.
            var canViewUsageStats = canManage || await channelAccessService.CanViewUsageStatsAsync(principal, channelName, ct);
            var channel = await channelService.GetByNameAsync(channelName, ct);

            return Results.Ok(new ChannelPermissionsDto(
                canManage,
                canViewUsageStats,
                channelAccessService.IsGlobalAdmin(principal),
                IsTracked: channel is not null,
                IsBotActive: channel?.IsBotActive ?? false));
        })
        // Ordinary navigation, and the single most requested route in the app: every page that shows
        // anything channel-scoped asks it first. Both access checks can reach Helix or 7TV on a cache
        // miss, but that cost is answered by the caches in front of them, not by a request budget —
        // see the policy comments in Program.cs.
        .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        group.MapGet("/mine", async (
            HttpContext httpContext,
            IMyChannelsService myChannelsService,
            CancellationToken ct) =>
        {
            var principal = httpContext.User.TryBuildTwitchPrincipal();
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var result = await myChannelsService.GetMyChannelsAsync(principal, ct);
            return Results.Ok(result);
        })
        // Still the heaviest endpoint in the app per incoming request, even though its two expensive
        // lookups — the moderated-channel pagination and the 7TV editor grants — now share their
        // server-side caches with the authorization path, so a repeat call inside the TTL reaches
        // neither provider. The client deliberately does not cache this one: the overview refetches
        // it on live events and has to show fresh channel, bot and live state. Unlimited, it let any
        // logged-in account exhaust the app-wide Twitch quota on cache misses alone, at which point
        // Helix returns nothing for everyone and every moderator of every channel silently loses
        // their permissions.
        .RequireRateLimiting(RateLimitPolicyNames.InteractiveRead);

        group.MapPost("/{channelName}/join", async (
            string channelName,
            HttpContext httpContext,
            IChannelService channelService,
            IChannelAccessService channelAccessService,
            CancellationToken ct) =>
        {
            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            // Built again rather than reused from the authorization filter above: that filter only
            // proves the caller may manage *this* channel, it never hands its principal on to the
            // handler. IsGlobalAdmin is a pure claims check (Auth:AdminTwitchLogins), so the extra
            // build costs nothing external — no second Helix/7TV round trip.
            var principal = httpContext.User.TryBuildTwitchPrincipal();
            var isGlobalAdmin = principal is not null && channelAccessService.IsGlobalAdmin(principal);

            var result = await channelService.JoinAsync(channelName, actor, isGlobalAdmin, ct);
            // A switch over every status rather than an `is null` check on Channel: a future third
            // status (e.g. "channel suspended") would otherwise silently fall through the old
            // two-way check and be reported as ChannelNotOnTwitch. This way the compiler flags a
            // missing arm (CS8509) the moment ChannelJoinStatus grows a member.
            //
            // CS8524 is the other half of that check and is silenced on purpose: it fires for
            // undefined casts like (ChannelJoinStatus)2, which no caller can produce here, and it
            // would do so on every build rather than only when the enum actually grows. Silencing
            // it keeps CS8509 — the signal this switch exists for — audible.
#pragma warning disable CS8524
            return result.Status switch
            {
                // 404 on the channel the caller asked to join: Twitch answered that no account holds
                // this login. Deliberately not a 400 — the name is well-formed, it just names nobody,
                // and the caller cannot fix it by spelling it differently.
                ChannelJoinStatus.ChannelNotOnTwitch =>
                    Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotOnTwitch }),

                // 409, the same status TriggerResyncAsync's NotActive case uses for "the state you
                // asked for conflicts with the state we're in": the request is well-formed and the
                // channel exists, but the configured cap (Channels:MaxActiveChannels) has no room for
                // one more active channel right now. Never reached for a channel that is already
                // active or for a global admin — see ChannelService.JoinAsync.
                ChannelJoinStatus.CapacityReached =>
                    Results.Conflict(new { errorCode = ApiErrorCodes.ChannelCapacityReached }),

                // 403: the request is well-formed and the channel exists, but its Twitch id is on the
                // configured block list (Channels:ExcludedChannelIds, GDPR Art. 21 objection) and
                // stays refused no matter who asks — unlike CapacityReached, no caller is exempt.
                ChannelJoinStatus.ChannelExcluded =>
                    Results.Json(
                        new { errorCode = ApiErrorCodes.ChannelExcluded }, statusCode: StatusCodes.Status403Forbidden),

                // The stored login, which is always the one that was asked for: LookupByLoginAsync
                // only reports Found on a normalized name match, so even in the rename case the
                // caller typed the new name and the row now carries it. Returned anyway rather than
                // echoing the raw input, because the normalized form is what every route and cache
                // key downstream uses.
                ChannelJoinStatus.Joined => Results.Ok(new
                {
                    channelId = result.Channel!.Id,
                    channelName = result.Channel.ChannelName,
                    result.Channel.IsBotActive
                }),
            };
#pragma warning restore CS8524
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>()
        // Bookkeeping, not a read budget: by the time this runs the caller has already made the bot a
        // moderator over on Twitch, so a dropped join leaves the two sides disagreeing with nothing to
        // say so — the same reason sync-deleted lives here.
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        // Self-service for the most common support case there is: "I added an emote and it is not
        // showing up". The answer used to be "wait for the next 60-second tick" or "ask the admin".
        //
        // Behind UsageStatsAccessAuthorizationFilter, the *wider* check — the opposite choice from
        // the audit log above, and deliberately so: the person with this problem is usually the
        // channel's 7TV editor, the one who just added the emote. A resync only reads from 7TV and
        // writes nothing anyone else owns; the abuse surface is cost, not authority, and cost is
        // what the cooldown below answers.
        group.MapPost("/{channelName}/resync", async (
            string channelName,
            HttpContext httpContext,
            IChannelService channelService,
            IChannelResyncCooldown resyncCooldown,
            CancellationToken ct) =>
        {
            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            // Claimed before triggering, not after: the two steps must not have a window between
            // them, or fifteen moderators clicking at once all pass the check and all trigger.
            // Deliberately in the handler rather than in a filter — a filter would have to inspect
            // the concrete IResult type to know whether to hand the slot back below.
            var cooldown = await resyncCooldown.TryBeginAsync(channelName, ct);
            if (!cooldown.Acquired)
            {
                httpContext.Response.Headers.RetryAfter = cooldown.RetryAfterSeconds.ToString();
                return Results.Json(
                    new { errorCode = ApiErrorCodes.ResyncCooldownActive, retryAfterSeconds = cooldown.RetryAfterSeconds },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            // 202, not 200: the command protocol is one-way, so success means "the worker was told",
            // never "the sync finished". Completion arrives separately, as a channel.synced live
            // event — which is also the reason this endpoint is worth rate-limiting at all.
            var result = await channelService.TriggerResyncAsync(channelName, actor, ct);
            if (result != ChannelResyncResult.Triggered)
            {
                // Nothing was actually triggered, so the channel must not stay blocked for a full
                // window — the broadcaster who just rejoined wants a resync straight away.
                await resyncCooldown.ReleaseAsync(channelName, ct);
            }

            return result switch
            {
                ChannelResyncResult.Triggered => Results.Accepted(),
                ChannelResyncResult.NotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
                ChannelResyncResult.NotActive => Results.Conflict(new { errorCode = ApiErrorCodes.ChannelNotJoined }),
                _ => Results.Problem(),
            };
        })
        .AddEndpointFilter<UsageStatsAccessAuthorizationFilter>()
        .RequireRateLimiting(RateLimitPolicyNames.ChannelResync);

        group.MapDelete("/{channelName}", async (
            string channelName,
            HttpContext httpContext,
            IChannelService channelService,
            CancellationToken ct) =>
        {
            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            // Deactivates the bot and keeps all history — see ChannelService.LeaveAsync for why this
            // must not be a delete. The destructive variant lives behind /purge below.
            var deactivated = await channelService.LeaveAsync(channelName, actor, ct);
            return deactivated ? Results.NoContent() : Results.NotFound();
        })
        .AddEndpointFilter<ChannelManagementAuthorizationFilter>()
        // A management mutation against our own database, like join above: guarded, but generously.
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        // Admin-only: the only way to irreversibly remove a channel with its emotes, usage history,
        // vote sessions and votes. It has a UI since 2026-07-31 (admin channel page, S1-1 reversed) —
        // reachable only from the global-admin area and behind a typed name confirmation. Not behind
        // ChannelManagementAuthorizationFilter, because that admits moderators — and a positively
        // cached mod status survives a /unmod by up to ten minutes, which is exactly the window in
        // which a just-removed moderator could have destroyed a channel's entire history.
        group.MapDelete("/{channelName}/purge", async (
            string channelName,
            HttpContext httpContext,
            IChannelService channelService,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var actor = httpContext.User.TryBuildAuditActor();
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var purged = await channelService.PurgeAsync(channelName, actor, ct);
            if (purged)
            {
                logger.LogWarning("Channel {Channel} wurde per Admin-Purge samt Historie gelöscht.", channelName);
            }

            return purged ? Results.NoContent() : Results.NotFound();
        })
        .AddEndpointFilter<GlobalAdminAuthorizationFilter>()
        // Same class of action as the leave above. Not on the policy-free /api/admin group despite
        // being admin-only: this route lives in the channel group, and a destructive one is the last
        // place to inherit an exemption by accident.
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);
    }
}

/// <summary>
/// What the caller may do with one channel. <c>CanViewUsageStats</c> is a superset of
/// <c>CanManage</c> (it additionally admits the channel's 7TV editors), so a consumer that only wants
/// "may see the usage tab" must read that field and not <c>CanManage</c>.
/// <para>
/// <c>IsBotActive</c> is part of this payload rather than a second call because leaving a channel now
/// only deactivates it: without it the workspace could not tell a healthy channel from one that has
/// silently stopped collecting, and the reactivate button would have nothing to key off.
/// </para>
/// <para>
/// No <c>IsSevenTvEditor</c> field, despite the review's sketch: nothing consumes it today, and
/// computing it would defeat the short-circuit above and put a 7TV call in every manager's request.
/// Add it together with its first consumer.
/// </para>
/// </summary>
internal sealed record ChannelPermissionsDto(
    bool CanManage,
    bool CanViewUsageStats,
    bool IsGlobalAdmin,
    bool IsTracked,
    bool IsBotActive);
