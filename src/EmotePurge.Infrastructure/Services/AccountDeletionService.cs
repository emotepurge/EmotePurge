using System.Text.Json;
using System.Text.Json.Nodes;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The account deletion path (data-retention plan, "Der Kontolöschpfad im Detail") — see
/// <see cref="IAccountDeletionService"/> for the contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a row lock and not just a conditional DELETE.</b> Between reading the row and deleting it,
/// three kinds of writers can touch the same user: a login or <c>LastSeenAtUtc</c> stamp (which must
/// win against an inactivity deletion), a late audit writer (whose entry must be pseudonymised or not
/// written at all) and a vote (which would hit the <c>Restrict</c> FK). <c>FOR UPDATE</c> on the user
/// row, taken first and held to the commit, serialises all three against the deletion:
/// </para>
/// <list type="bullet">
/// <item>An uncommitted stamp or login holds a row lock; the deletion waits, then reads the new version
/// under its own lock — the inactivity recheck sees the activity and answers
/// <see cref="AccountDeletionOutcome.StillActive"/>. A stamp arriving after the lock waits and then
/// updates zero rows.</item>
/// <item>A late audit writer takes <c>FOR SHARE</c> (<see cref="UserService.InvalidateRoleCacheAsync"/>)
/// or updates the row in the same save (<see cref="UserService.RevokeSessionsAsync"/>): either it
/// commits first and its entry is pseudonymised below, or it waits and finds no row afterwards.</item>
/// <item>A vote insert checks its FK with a key-share lock on the user row, which conflicts with
/// <c>FOR UPDATE</c>: an in-flight vote either committed before the lock (and is deleted with the rest,
/// since every statement below reads a fresh snapshot) or waits and then fails its FK check against the
/// deleted row. The deletion itself does not fail on that account.</item>
/// </list>
/// <para>
/// The one gap the lock cannot close: an entry with the deleted user as <em>actor</em>, written by an
/// in-flight request of that user after the commit (plan, decision 9). The inactivity path is free of it
/// by the recheck; for an admin request it is accepted.
/// </para>
/// </remarks>
public class AccountDeletionService(
    AppDbContext db,
    IModRoleCache modRoleCache,
    IRateLimitTelemetry rateLimitTelemetry,
    ILogger<AccountDeletionService> logger) : IAccountDeletionService
{
    private const string UserTargetType = "user";

    // The one details key of a "user" target entry that carries the user's identity
    // (user.revokeSessions and user.invalidateRoleCache both write { login }). Every other key stays.
    private const string LoginDetailKey = "login";

    public async Task<AccountDeletionResult> DeleteAsync(
        string twitchUserId,
        AuditActor actor,
        AccountDeletionReason reason,
        DateTime? onlyIfInactiveBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(twitchUserId);
        ArgumentNullException.ThrowIfNull(actor);
        switch (reason)
        {
            case AccountDeletionReason.Inactivity when onlyIfInactiveBeforeUtc is null:
                throw new ArgumentException("An inactivity deletion needs its cutoff, to recheck it under the row lock.", nameof(onlyIfInactiveBeforeUtc));
            case AccountDeletionReason.AdminRequest when onlyIfInactiveBeforeUtc is not null:
                throw new ArgumentException("An admin-requested deletion is unconditional and takes no cutoff.", nameof(onlyIfInactiveBeforeUtc));
            case AccountDeletionReason.Inactivity:
            case AccountDeletionReason.AdminRequest:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown account deletion reason.");
        }

        AccountDeletionResult result;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var user = await db.LockUserAsync(twitchUserId, UserRowLock.ForUpdate, cancellationToken);
            if (user is null)
            {
                // No row, nothing written — not even an audit entry: a no-op is not an event. Also what
                // a second call (or the loser of two concurrent calls) gets, and Redis is deliberately
                // left alone: the id is unverified input then, and must not reach a SCAN pattern.
                return new AccountDeletionResult(AccountDeletionOutcome.NotFound);
            }

            if (reason == AccountDeletionReason.Inactivity && !(LastActivityUtc(user) < onlyIfInactiveBeforeUtc))
            {
                // Rechecked here, under the lock, rather than trusted from the caller's candidate
                // selection: a login or a LastSeenAtUtc stamp in between wins.
                return new AccountDeletionResult(AccountDeletionOutcome.StillActive);
            }

            // Votes first: Vote -> User is Restrict. Every session counts, open ones included — a vote
            // is an opinion with an author, so its score contribution goes with the account.
            var votesInOpenSessions = await db.Votes
                .Where(v => v.UserId == twitchUserId && v.VoteSession.IsActive)
                .CountAsync(cancellationToken);
            var votesDeleted = await db.Votes
                .Where(v => v.UserId == twitchUserId)
                .ExecuteDeleteAsync(cancellationToken);

            var (asTarget, asBoth) = await PseudonymiseTargetEntriesAsync(twitchUserId, cancellationToken);
            var asActor = await db.AuditLogEntries
                .Where(e => e.ActorTwitchUserId == twitchUserId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(e => e.ActorTwitchUserId, AuditActor.DeletedUser.TwitchUserId)
                        .SetProperty(e => e.ActorLogin, AuditActor.DeletedUser.Login),
                    cancellationToken);
            var auditEntriesPseudonymised = asActor + asTarget - asBoth;

            // The encrypted Twitch tokens live on the row and go with it.
            db.Users.Remove(user);

            // Carries no identity of the deleted account: the target is the marker, the details hold
            // only reason and counts. A self-deletion gets the marker as actor too — otherwise this one
            // entry would restore the id and login the statements above just removed everywhere else.
            var recordedActor = actor.TwitchUserId == twitchUserId ? AuditActor.DeletedUser : actor;
            db.AddAuditEntry(
                recordedActor,
                AuditActions.UserDelete,
                targetType: UserTargetType,
                targetId: AuditActor.DeletedUser.TwitchUserId,
                details: new
                {
                    reason = ReasonDetail(reason),
                    votesDeleted,
                    auditEntriesPseudonymised
                });
            await db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            result = new AccountDeletionResult(
                AccountDeletionOutcome.Deleted,
                votesDeleted,
                votesInOpenSessions,
                asActor,
                asTarget,
                auditEntriesPseudonymised);
        }

        await CleanUpRedisAsync(twitchUserId);
        return result;
    }

    /// <summary>
    /// Rewrites every <c>"user"</c> target entry pointing at the account: <c>TargetId</c> and the
    /// <c>login</c> detail become the marker, every other column and detail key stays. Loaded and saved
    /// rather than one bulk UPDATE because the details are JSON that needs parsing. Returns the number
    /// of entries rewritten, and how many of them also had the user as actor (so the caller can count
    /// distinct entries once the actor columns are rewritten in bulk).
    /// </summary>
    /// <remarks>
    /// Deliberately scoped to <c>TargetType = "user"</c> and the one <c>login</c> key rather than "every
    /// string equal to the login": a broadcaster deleting their account still has a channel of the same
    /// name, and that channel's history (<c>channel.rename</c>'s <c>oldLogin</c>, a merge's names) is not
    /// account data and is not theirs to take along.
    /// </remarks>
    private async Task<(int Rewritten, int AlsoActor)> PseudonymiseTargetEntriesAsync(string twitchUserId, CancellationToken cancellationToken)
    {
        var entries = await db.AuditLogEntries
            .Where(e => e.TargetType == UserTargetType && e.TargetId == twitchUserId)
            .ToListAsync(cancellationToken);

        var alsoActor = 0;
        foreach (var entry in entries)
        {
            if (entry.ActorTwitchUserId == twitchUserId)
            {
                alsoActor++;
            }

            entry.TargetId = AuditActor.DeletedUser.TwitchUserId;
            entry.DetailsJson = PseudonymiseLoginDetail(entry.DetailsJson);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (entries.Count, alsoActor);
    }

    /// <summary>
    /// Clears what Redis holds under the deleted id: the role-cache keys (<c>modlist:</c>,
    /// <c>7tveditor:</c>, <c>subcheck:</c>) and the rate-limit telemetry's last-rejection slot. After the
    /// commit, never inside the transaction — Redis cannot roll back with it, and neither call needs the
    /// row. Each step is idempotent and gets one retry; what still fails is logged (a count, no id) and
    /// left to the TTLs: ten minutes for the role keys, 25 hours for the slot, which the next rejected
    /// request of anyone overwrites anyway.
    /// </summary>
    private async Task CleanUpRedisAsync(string twitchUserId)
    {
        // CancellationToken.None: the deletion has committed, so a caller that goes away now (a closed
        // browser tab) must not leave the cleanup half-done.
        var failedSteps = 0;
        if (!await TryTwiceAsync(async () =>
            {
                await modRoleCache.InvalidateUserAsync(twitchUserId, CancellationToken.None);
                return true;
            }))
        {
            failedSteps++;
        }

        // The limiter partitions by the bare user id (the voting policy by "{id}:{sessionId}", which
        // ForgetPartitionAsync matches as a sub-partition).
        if (!await TryTwiceAsync(() => rateLimitTelemetry.ForgetPartitionAsync(twitchUserId, CancellationToken.None)))
        {
            failedSteps++;
        }

        if (failedSteps > 0)
        {
            logger.LogWarning(
                "Account deleted, but {FailedSteps} Redis cleanup step(s) still failed after one retry; the leftover keys expire on their own TTL (10 min role cache, 25 h rate-limit slot).",
                failedSteps);
        }
    }

    private static async Task<bool> TryTwiceAsync(Func<Task<bool>> step)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (await step())
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // Logged once, as a count, by the caller if the retry fails too — an exception message
                // here could carry the key, and with it the id.
            }
        }

        return false;
    }

    private static DateTime LastActivityUtc(User user) =>
        user.LastSeenAtUtc is { } lastSeen && lastSeen > user.LastLogin ? lastSeen : user.LastLogin;

    private static string ReasonDetail(AccountDeletionReason reason) => reason switch
    {
        AccountDeletionReason.AdminRequest => "adminRequest",
        AccountDeletionReason.Inactivity => "inactivity",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };

    private static string? PseudonymiseLoginDetail(string? detailsJson)
    {
        if (detailsJson is null)
        {
            return null;
        }

        // jsonb hands the payload back normalised, so re-serialising is not a meaningful change to it.
        // Anything that is not an object with a "login" key is left exactly as it was.
        if (JsonNode.Parse(detailsJson) is not JsonObject details || !details.ContainsKey(LoginDetailKey))
        {
            return detailsJson;
        }

        details[LoginDetailKey] = AuditActor.DeletedUser.Login;
        return details.ToJsonString(JsonSerializerOptions.Default);
    }
}
