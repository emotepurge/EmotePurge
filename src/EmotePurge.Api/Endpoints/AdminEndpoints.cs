using System.Security.Claims;
using EmotePurge.Api.Auth;
using EmotePurge.Api.Health;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Options;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// Everything under /api/admin. Authorization is declared once on the group — every endpoint added
/// here is global-admin-only by construction, so a new one cannot be forgotten. No rate-limit policy:
/// these are DB/Redis-only reads for a single-digit number of allowlisted logins (same call as the
/// pre-existing admin endpoints in ChannelEndpoints).
/// </summary>
public static class AdminEndpoints
{
    // The partition kind shared by most of the policies described below (RateLimitRejection's
    // ResolveUserKey): the authenticated Twitch user, falling back to the remote IP. Kept as one
    // named constant so the five policies that share it cannot spell it differently by accident.
    private const string PerUserPartition = "twitch-user";

    // Enough to act on without turning a support page into a 500-name wall. Every list that uses it
    // ships its untruncated total alongside.
    private const int DeficitListLimit = 50;

    public static void MapAdminEndpoints(this WebApplication app)
    {
        // Deliberately no rate-limit policy, group-wide and including the /live stream below: the
        // allowlist is the guard here, and internal tooling for a handful of named logins has no abuse
        // surface a request budget would close. See the class summary.
        var group = app.MapGroup("/api/admin")
            .RequireAuthorization()
            .AddEndpointFilter<GlobalAdminAuthorizationFilter>();

        group.MapGet("/health", async (IWorkerHealthReader healthReader, CancellationToken ct) =>
        {
            // Admin counterpart to the public GET /api/worker/health (Z1 split): same snapshot, same
            // derived statuses — via the shared WorkerHealthStatus so the thresholds cannot drift —
            // but the full operational detail the public badge must not leak.
            var snapshot = await healthReader.ReadAsync(ct);
            if (snapshot is null)
            {
                // The key expired or was never written. Reported as the same shape with everything
                // unknown rather than a shorter error body, so the page renders one layout instead of
                // branching on a second contract. snapshotAvailable is the flag that says which it is.
                return Results.Ok(new
                {
                    snapshotAvailable = false,
                    status = "unknown",
                    isConnected = false,
                    lastMessageReceivedUtc = (DateTime?)null,
                    lastFrameReceivedUtc = (DateTime?)null,
                    connectAttemptedUtc = (DateTime?)null,
                    secondsSinceLastMessage = (int?)null,
                    sevenTv = new
                    {
                        status = "unknown",
                        enabled = false,
                        connected = false,
                        lastFrameUtc = (DateTime?)null,
                        lastDispatchUtc = (DateTime?)null,
                        connectAttemptedUtc = (DateTime?)null,
                        secondsSinceLastFrame = (int?)null,
                        desiredChannelCount = (int?)null,
                        desiredSubscriptionCount = (int?)null,
                        unacknowledgedCount = (int?)null,
                        subscriptionLimit = WorkerHealthStatus.SevenTvSubscriptionLimit,
                        resyncIntervalSeconds = (int?)null,
                    },
                    flush = new
                    {
                        consecutiveFailures = (int?)null,
                        lastSuccessUtc = (DateTime?)null,
                        lastRowCount = (int?)null,
                        pendingEmoteCount = (int?)null,
                    },
                    worker = new
                    {
                        instanceId = (string?)null,
                        processStartedUtc = (DateTime?)null,
                    },
                });
            }

            var derived = WorkerHealthStatus.Derive(snapshot, DateTime.UtcNow);

            return Results.Ok(new
            {
                snapshotAvailable = true,
                status = derived.Status,
                isConnected = snapshot.IsConnected,
                lastMessageReceivedUtc = snapshot.LastMessageReceivedUtc,
                // The liveness signal behind the "stale" status since 2026-08-03 — surfacing it here
                // keeps the admin page able to explain that status, mirroring sevenTv.lastFrameUtc.
                lastFrameReceivedUtc = snapshot.TwitchLastFrameUtc,
                connectAttemptedUtc = snapshot.ConnectAttemptedUtc,
                secondsSinceLastMessage = derived.SecondsSinceLastMessage,
                sevenTv = new
                {
                    status = derived.SevenTvStatus,
                    enabled = snapshot.SevenTvEnabled,
                    connected = snapshot.SevenTvConnected,
                    lastFrameUtc = snapshot.SevenTvLastFrameUtc,
                    lastDispatchUtc = snapshot.SevenTvLastDispatchUtc,
                    connectAttemptedUtc = snapshot.SevenTvConnectAttemptedUtc,
                    secondsSinceLastFrame = derived.SevenTvSecondsSinceLastFrame,
                    desiredChannelCount = snapshot.SevenTvDesiredChannelCount,
                    desiredSubscriptionCount = snapshot.SevenTvDesiredSubscriptionCount,
                    unacknowledgedCount = snapshot.SevenTvUnacknowledgedCount,
                    // 7TV's own figure from the last Hello when we have it. The constant stays as the
                    // fallback for the window where the socket has never connected — or where the
                    // worker still predates the field during a rolling deploy.
                    subscriptionLimit = snapshot.SevenTvSubscriptionLimit ?? WorkerHealthStatus.SevenTvSubscriptionLimit,
                    // Not a limit but a rate: with one REST call per channel per tick, this is the
                    // divisor the UI needs to say "34 requests per 60s" instead of implying a quota.
                    resyncIntervalSeconds = snapshot.SevenTvResyncIntervalSeconds,
                },
                flush = new
                {
                    consecutiveFailures = snapshot.FlushConsecutiveFailures,
                    lastSuccessUtc = snapshot.FlushLastSuccessUtc,
                    lastRowCount = snapshot.FlushLastRowCount,
                    pendingEmoteCount = snapshot.PendingEmoteCount,
                },
                worker = new
                {
                    instanceId = snapshot.WorkerInstanceId,
                    processStartedUtc = snapshot.ProcessStartedUtc,
                },
            });
        });

        // Read-only snapshot for the "Rate Limits" admin section (design 4 of the #33 architecture
        // doc): the effective configuration always comes from IOptions<RateLimitingOptions>, complete
        // whether or not the counter store answers. Deliberately its own endpoint, not folded into
        // /health: that contract's "same shape when unavailable" promise is about the worker snapshot,
        // and a second, unrelated availability flag on the same payload would blur which one flipped.
        group.MapGet("/rate-limits", async (
            IRateLimitTelemetryReader telemetryReader,
            IOptions<RateLimitingOptions> rateLimitOptions,
            CancellationToken ct) =>
        {
            var snapshot = await telemetryReader.ReadAsync(ct);
            var countersByPolicy = snapshot.Policies.ToDictionary(p => p.PolicyName, StringComparer.Ordinal);

            // Built from configuration, not from the snapshot: a policy the counter store never saw
            // traffic for — because nobody hit it, or because telemetryAvailable is false — still has
            // a budget and still guards a route. Reversing the direction would make a quiet policy
            // indistinguishable from one that was never registered.
            var policies = RateLimitPolicyDescriptors(rateLimitOptions.Value)
                .Select(descriptor =>
                {
                    var counters = countersByPolicy.GetValueOrDefault(descriptor.Name);
                    return new
                    {
                        name = descriptor.Name,
                        type = descriptor.Type,
                        capacity = descriptor.Capacity,
                        tokensPerPeriod = descriptor.TokensPerPeriod,
                        replenishmentPeriodSeconds = descriptor.ReplenishmentPeriodSeconds,
                        windowSeconds = descriptor.WindowSeconds,
                        partition = descriptor.Partition,
                        queueLimit = descriptor.QueueLimit,
                        acceptedLastMinute = counters?.AcceptedLastMinute ?? 0,
                        rejectedLastMinute = counters?.RejectedLastMinute ?? 0,
                        acceptedLast24Hours = counters?.AcceptedLast24Hours ?? 0,
                        rejectedLast24Hours = counters?.RejectedLast24Hours ?? 0,
                    };
                })
                .ToList();

            return Results.Ok(new
            {
                telemetryAvailable = snapshot.TelemetryAvailable,
                policies,
                lastLocalRejection = snapshot.LastLocalRejection,
                // Caches and providers are not completed against a fixed list the way policies are:
                // which ones exist is derived from the counters themselves (RateLimitTelemetryStore),
                // matching how the store already reports providers — one fewer index to keep in sync,
                // at the honest price that something silent for the whole retention window drops out.
                caches = snapshot.Caches,
                providers = snapshot.Providers,
            });
        });

        group.MapGet("/roster", async (
            IWorkerRosterReader rosterReader,
            IAdminChannelQueryService channelQueryService,
            CancellationToken ct) =>
        {
            // Its own endpoint rather than more fields on /health: /health is refetched on every
            // worker.health event (~20s) by every open monitoring page and carries a deliberately
            // stable "same shape when unavailable" contract. Two independent availability flags in
            // one payload would be exactly the ambiguity that contract exists to avoid.
            var snapshot = await rosterReader.ReadAsync(ct);
            var trackedChannels = await channelQueryService.ListActiveChannelNamesAsync(ct);

            if (snapshot is null)
            {
                return Results.Ok(new
                {
                    snapshotAvailable = false,
                    trackedChannelCount = trackedChannels.Count,
                    ceilings = Ceilings(),
                });
            }

            var rosterByChannel = snapshot.Channels.ToDictionary(
                c => c.ChannelName, StringComparer.OrdinalIgnoreCase);

            // "Missing" means not confirmed, which covers both absent-from-the-roster and
            // present-but-unconfirmed: for the question this page answers — is this channel actually
            // being counted — the two are the same failure.
            var missingFromIrc = trackedChannels
                .Where(name => !rosterByChannel.TryGetValue(name, out var row) || !row.IrcJoinConfirmed)
                .ToList();
            var missingFromSevenTv = trackedChannels
                .Where(name => !rosterByChannel.TryGetValue(name, out var row) || !row.SevenTvEmoteSetAcknowledged)
                .ToList();
            // The other direction: the worker holds a channel the database no longer considers
            // active. A leave that never reached the worker looks exactly like this.
            var unknownToDatabase = snapshot.Channels
                .Select(c => c.ChannelName)
                .Where(name => !trackedChannels.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            return Results.Ok(new
            {
                snapshotAvailable = true,
                snapshot.GeneratedAtUtc,
                // Staleness as a number the client does not have to derive from two clocks. The TTL
                // only ever says "gone"; this says "present, and two minutes old".
                ageSeconds = (int)(DateTime.UtcNow - snapshot.GeneratedAtUtc).TotalSeconds,
                snapshot.WorkerInstanceId,
                snapshot.ProcessStartedUtc,
                // Without this a redeploy reads as a total outage: _desiredChannels is empty until
                // boot recovery has rejoined everything, so every channel is "missing" for a minute.
                snapshot.BootRecoveryCompleted,
                snapshot.Truncated,
                trackedChannelCount = trackedChannels.Count,
                rosterChannelCount = snapshot.Channels.Count,
                ircConfirmedCount = snapshot.Channels.Count(c => c.IrcJoinConfirmed),
                sevenTvAcknowledgedCount = snapshot.Channels.Count(c => c.SevenTvEmoteSetAcknowledged),
                // Capped lists plus their untruncated totals: a short list that silently stood for a
                // long one would read as "almost fine" on the page where that matters most.
                missingFromIrc = missingFromIrc.Take(DeficitListLimit).ToList(),
                missingFromIrcTotal = missingFromIrc.Count,
                missingFromSevenTv = missingFromSevenTv.Take(DeficitListLimit).ToList(),
                missingFromSevenTvTotal = missingFromSevenTv.Count,
                unknownToDatabase = unknownToDatabase.Take(DeficitListLimit).ToList(),
                unknownToDatabaseTotal = unknownToDatabase.Count,
                ceilings = Ceilings(),
            });
        });

        group.MapGet("/channels/{channelName}", async (
            string channelName,
            IAdminChannelQueryService channelQueryService,
            IWorkerRosterReader rosterReader,
            ITwitchLiveStatusReader liveStatusReader,
            CancellationToken ct) =>
        {
            var channel = await channelQueryService.GetAsync(channelName, ct);
            if (channel is null)
            {
                return Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound });
            }

            // Same row shape as the list (see AdminChannelDto): a drilldown that says "unknown"
            // while the list says "live" would be the two-views-disagreeing failure the DTO
            // comment warns about.
            var liveStatus = await liveStatusReader.ReadAsync(ct);
            channel = channel with
            {
                LiveState = ChannelLiveStates.Derive(
                    liveStatus?.LiveChannelLogins.ToHashSet(), channel.ChannelName, channel.IsBotActive),
            };

            var snapshot = await rosterReader.ReadAsync(ct);
            var rosterRow = snapshot?.Channels
                .FirstOrDefault(c => string.Equals(c.ChannelName, channel.ChannelName, StringComparison.OrdinalIgnoreCase));

            return Results.Ok(new
            {
                channel,
                roster = new
                {
                    available = snapshot is not null,
                    ageSeconds = snapshot is null
                        ? (int?)null
                        : (int)(DateTime.UtcNow - snapshot.GeneratedAtUtc).TotalSeconds,
                    bootRecoveryCompleted = snapshot?.BootRecoveryCompleted,
                    workerInstanceId = snapshot?.WorkerInstanceId,
                    // Null with available:true is the finding, not a gap: the worker published a
                    // roster and this channel is not in it.
                    channel = rosterRow,
                },
            });
        });

        // Inside the group, not next to the channel stream in LiveEndpoints: that is what makes the
        // group's GlobalAdminAuthorizationFilter apply to it by construction (see the class comment).
        group.MapGet("/live", (
            HttpContext httpContext,
            ILiveEventStream liveEventStream,
            LiveStreamKeepaliveOptions keepaliveOptions,
            LiveStreamConnectionRegistry connectionRegistry,
            CancellationToken ct) => LiveEndpoints.OpenAdminAsync(httpContext, liveEventStream, keepaliveOptions, connectionRegistry, ct));

        group.MapGet("/channels", async (
            IAdminChannelQueryService channelQueryService,
            ITwitchLiveStatusReader liveStatusReader,
            CancellationToken ct) =>
        {
            // The single global channel list since 2026-07-31: it replaced the narrower
            // GET /api/channels of the overview's admin section, which was removed with that section.
            var channels = await channelQueryService.ListAsync(ct);

            // Live state is snapshot-scoped, not row-scoped: one poll timestamp for the whole
            // response, three-way per channel (only bot-active channels were polled at all).
            var liveStatus = await liveStatusReader.ReadAsync(ct);
            var liveLogins = liveStatus is null ? null : liveStatus.LiveChannelLogins.ToHashSet();

            return Results.Ok(new
            {
                channels = channels
                    .Select(c => c with { LiveState = ChannelLiveStates.Derive(liveLogins, c.ChannelName, c.IsBotActive) })
                    .ToList(),
                livePolledAtUtc = liveStatus?.GeneratedAtUtc,
            });
        });

        group.MapPost("/channels/{channelName}/resync", async (
            string channelName,
            ClaimsPrincipal principal,
            IChannelService channelService,
            CancellationToken ct) =>
        {
            var actor = principal.TryBuildAuditActor();
            if (actor is null)
            {
                // Unreachable behind RequireAuthorization + the admin filter; guard, not a case.
                return Results.Unauthorized();
            }

            // 202, not 200: the command protocol is one-way, so success means "the worker was
            // told", never "the sync finished". Completion shows up as LastSyncedAtUtc in
            // GET /channels on the next refresh.
            //
            // No IChannelResyncCooldown here, unlike the channel-scoped POST /api/channels/{name}
            // /resync — on purpose. This is the escape hatch for the support case where that
            // cooldown is in the way, and it is reachable only by the handful of allowlisted admin
            // logins. Do not "unify" the two handlers.
            var result = await channelService.TriggerResyncAsync(channelName, actor, ct);
            return result switch
            {
                ChannelResyncResult.Triggered => Results.Accepted(),
                ChannelResyncResult.NotFound => Results.NotFound(new { errorCode = ApiErrorCodes.ChannelNotFound }),
                ChannelResyncResult.NotActive => Results.Conflict(new { errorCode = ApiErrorCodes.ChannelNotJoined }),
                _ => Results.Problem(),
            };
        });

        group.MapGet("/users", async (
            int page,
            int pageSize,
            IAdminUserQueryService userQueryService,
            CancellationToken ct) =>
        {
            var (effectivePage, effectivePageSize) = PagingQuery.Clamp(page, pageSize);

            var result = await userQueryService.ListAsync(effectivePage, effectivePageSize, ct);
            return Results.Ok(result);
        });

        group.MapDelete("/users/{twitchUserId}", async (
            string twitchUserId,
            ClaimsPrincipal principal,
            IAccountDeletionService accountDeletionService,
            CancellationToken ct) =>
        {
            var actor = principal.TryBuildAuditActor();
            if (actor is null)
            {
                // Unreachable behind RequireAuthorization + the admin filter; guard, not a case.
                return Results.Unauthorized();
            }

            // AdminRequest, so onlyIfInactiveBeforeUtc is null (only the retention job's Inactivity
            // path rechecks a cutoff) — an admin may delete any account on request, active or not.
            // Deleting one's own account this way is allowed (same precedent as revoke-sessions
            // below); the frontend's confirmation dialog names that case.
            var result = await accountDeletionService.DeleteAsync(
                twitchUserId, actor, AccountDeletionReason.AdminRequest, onlyIfInactiveBeforeUtc: null, ct);
            return result.Outcome switch
            {
                AccountDeletionOutcome.Deleted => Results.NoContent(),
                AccountDeletionOutcome.NotFound => Results.NotFound(),
                // StillActive cannot occur for AdminRequest (IAccountDeletionService contract).
                _ => Results.Problem(),
            };
        });

        group.MapPost("/users/{twitchUserId}/revoke-sessions", async (
            string twitchUserId,
            ClaimsPrincipal principal,
            IUserService userService,
            CancellationToken ct) =>
        {
            var actor = principal.TryBuildAuditActor();
            if (actor is null)
            {
                // Unreachable behind RequireAuthorization + the admin filter; guard, not a case.
                return Results.Unauthorized();
            }

            // Semantically a forced logout, mirroring POST /api/auth/logout for the target user:
            // revocation and token clearing always travel together (the documented invariant that
            // all Twitch token columns are dropped whenever sessions end). Only the revocation is
            // audited — the token clearing is its consequence, not a second action.
            var revoked = await userService.RevokeSessionsAsync(twitchUserId, actor, ct);
            if (!revoked)
            {
                return Results.NotFound();
            }

            await userService.ClearTwitchTokensAsync(twitchUserId, ct);
            return Results.NoContent();
        });

        group.MapPost("/users/{twitchUserId}/invalidate-role-cache", async (
            string twitchUserId,
            ClaimsPrincipal principal,
            IUserService userService,
            CancellationToken ct) =>
        {
            var actor = principal.TryBuildAuditActor();
            if (actor is null)
            {
                // Unreachable behind RequireAuthorization + the admin filter; guard, not a case.
                return Results.Unauthorized();
            }

            // The admin remedy for the documented mod-cache staleness (decision log 2026-07-25):
            // a /unmod -> /mod flip no longer waits out the TTL or a manual Redis delete.
            var removedEntries = await userService.InvalidateRoleCacheAsync(twitchUserId, actor, ct);
            return removedEntries is null
                ? Results.NotFound()
                : Results.Ok(new { removedEntries });
        });

        group.MapGet("/audit-log", async (
            int page,
            int pageSize,
            string? action,
            string? channel,
            string? actor,
            IAuditLogQueryService auditLogQueryService,
            CancellationToken ct) =>
        {
            var (effectivePage, effectivePageSize) = PagingQuery.Clamp(page, pageSize);

            // Filters are pass-through: an unknown action or channel simply yields an empty page.
            // Channel normalization happens in the query service, next to the matching rule it feeds.
            // Unlike the channel-scoped counterpart in ChannelEndpoints, the channel here is a real
            // filter and may be absent — this caller is authorized for every channel.
            var filter = new AuditLogFilter(
                PagingQuery.NullIfBlank(action),
                PagingQuery.NullIfBlank(channel),
                PagingQuery.NullIfBlank(actor));

            var result = await auditLogQueryService.ListAsync(effectivePage, effectivePageSize, filter, ct);
            return Results.Ok(result);
        });
    }

    // Shipped in both branches of /roster so the page renders one layout: the ceilings are constants
    // and do not depend on a snapshot being available.
    private static object Ceilings() => new
    {
        twitchConcurrentChannelLimit = WorkerCapacity.TwitchConcurrentChannelLimit,
        twitchJoinBudgetChannels = WorkerCapacity.TwitchJoinBudgetChannels,
    };

    // The one policy list the app registers, described for display rather than re-derived from
    // RateLimitRejection's partitioner delegates: those close over an HttpContext and exist to
    // compute one request's key, not to describe the partitioning strategy in the abstract. Order
    // matches Program.cs's AddRateLimiter registration.
    private static IReadOnlyList<RateLimitPolicyDescriptor> RateLimitPolicyDescriptors(RateLimitingOptions options) =>
    [
        RateLimitPolicyDescriptor.TokenBucket(RateLimitPolicyNames.InteractiveRead, options.InteractiveRead, PerUserPartition),
        RateLimitPolicyDescriptor.TokenBucket(RateLimitPolicyNames.Voting, options.Voting, "twitch-user+vote-session"),
        RateLimitPolicyDescriptor.FixedWindow(RateLimitPolicyNames.Bookkeeping, options.Bookkeeping, PerUserPartition),
        RateLimitPolicyDescriptor.FixedWindow(RateLimitPolicyNames.ChannelResync, options.ChannelResync, PerUserPartition),
        // The two anonymous policies: PartitionPerUser falls back to the remote IP when there is no
        // authenticated Twitch user, which for these two routes is every caller (RateLimitRejection.cs).
        RateLimitPolicyDescriptor.FixedWindow(RateLimitPolicyNames.PublicHealth, options.PublicHealth, "remote-ip"),
        // Issue #247, split from PublicHealth (Codex Sol review, P2): unrelated legitimate callers
        // (browser visitors vs. two machines on a fixed cadence) must not be able to exhaust each
        // other's budget.
        RateLimitPolicyDescriptor.FixedWindow(RateLimitPolicyNames.PublicLegal, options.PublicLegal, "remote-ip"),
        // The foreign-channel preview's per-user half (spec E5a). Its provider-wide half (E5b) is
        // deliberately absent from this list: that budget is not an ASP.NET policy at all, has no
        // partition, and lives in the Infrastructure decorator — listing it here would claim a shape
        // it does not have. What this list must not do is omit a policy that *is* registered, which
        // is what happened until now: a route was guarded by a limiter that the admin snapshot, built
        // solely from this list, showed no trace of (AK 15).
        RateLimitPolicyDescriptor.FixedWindow(RateLimitPolicyNames.ForeignEmoteLookup, options.ForeignEmoteLookup, PerUserPartition),
        // The 7TV leaderboard's per-user half (spec 2026-09-13, E16), same reasoning as
        // ForeignEmoteLookup above: the leaderboard's own window budget across all users is not an
        // ASP.NET policy either and stays out of this list for the same reason.
        RateLimitPolicyDescriptor.FixedWindow(RateLimitPolicyNames.SevenTvLeaderboard, options.SevenTvLeaderboard, PerUserPartition),
        // POST /api/contact's per-IP half (docs/DECISIONS.md 2026-09-24, "contact form"), same split
        // as ForeignEmoteLookup/SevenTvLeaderboard above: the provider-wide ceiling across all
        // visitors (ContactSendBudget) is not an ASP.NET policy and stays out of this list.
        RateLimitPolicyDescriptor.TokenBucket(RateLimitPolicyNames.Contact, options.Contact, "remote-ip"),
    ];

    /// <summary>
    /// One policy's display shape: a stable name, its kind, and the fields that kind actually has —
    /// a token bucket's refill rate has no meaning for a fixed window and vice versa, so the unused
    /// side is <c>null</c> rather than a borrowed number from the other kind.
    /// </summary>
    private sealed record RateLimitPolicyDescriptor(
        string Name,
        string Type,
        int Capacity,
        int? TokensPerPeriod,
        int? ReplenishmentPeriodSeconds,
        int? WindowSeconds,
        string Partition,
        int QueueLimit)
    {
        public static RateLimitPolicyDescriptor TokenBucket(
            string name, RateLimitingOptions.TokenBucketPolicy policy, string partition) =>
            new(
                name,
                "token-bucket",
                policy.TokenLimit,
                policy.TokensPerPeriod,
                policy.ReplenishmentPeriodSeconds,
                WindowSeconds: null,
                partition,
                RateLimitRejection.QueueLimit);

        public static RateLimitPolicyDescriptor FixedWindow(
            string name, RateLimitingOptions.FixedWindowPolicy policy, string partition) =>
            new(
                name,
                "fixed-window",
                policy.PermitLimit,
                TokensPerPeriod: null,
                ReplenishmentPeriodSeconds: null,
                (int)RateLimitRejection.Window.TotalSeconds,
                partition,
                RateLimitRejection.QueueLimit);
    }
}
