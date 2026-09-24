using System.Linq.Expressions;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// One retention pass (data-retention plan, "Der Job") — see <see cref="IDataRetentionService"/> for
/// the contract. The categories run in a fixed order, each in its own transaction(s), and never inside
/// an outer one: tokens → accounts → ended vote sessions → audit log → channels.
/// </summary>
/// <remarks>
/// <para>
/// <b>One predicate per category, used by both modes.</b> Each category selects its parents through
/// one expression (<see cref="EndedBefore"/>, <see cref="AccountRetentionQueries.LastActiveBefore"/>,
/// …) and counts the dependent rows from exactly those parents; the enforced run deletes what it
/// selected, the dry run stops before that. The account deletion's own predicates are shared through
/// <see cref="AccountRetentionQueries"/>, since the enforced path runs them inside
/// <see cref="IAccountDeletionService"/>, not here.
/// </para>
/// <para>
/// <b>Overlap between categories.</b> In an enforced pass a later category no longer sees what an
/// earlier one removed: the votes of a deleted account are gone before the session category counts,
/// and ended sessions are gone before a channel purge cascades. Every later cascade count therefore
/// leaves out the votes of the accounts this pass removed (or, dry, would remove) and the sessions the
/// session category covers — a no-op in an enforced pass, the exact correction in a dry one. The two
/// modes differ only where the enforced run meets something the dry run cannot foresee: a recheck under
/// a row lock answering "still active", a concurrent deletion, a failure.
/// </para>
/// <para>
/// <b>Why the change tracker is cleared after every account and channel.</b> This service shares the
/// scope's <see cref="AppDbContext"/> with <see cref="IAccountDeletionService"/> and
/// <see cref="IChannelService"/>. Both track what they load and remove; when one throws, its transaction
/// rolls back but the tracked removal stays pending in the context, and the next item's
/// <c>SaveChangesAsync</c> would replay it — outside the lock and recheck that guarded it, or failing
/// every remaining item on the same error. Clearing after each item makes each one start clean.
/// </para>
/// <para>
/// <b>Logging.</b> Only counts and error kinds (exception type, Postgres SQLSTATE) — never an exception
/// message, which can quote a key value, and never a login, a user id or a channel name.
/// </para>
/// </remarks>
public class DataRetentionService(
    AppDbContext db,
    IAccountDeletionService accountDeletionService,
    IChannelService channelService,
    RetentionOptions options,
    TimeProvider timeProvider,
    ILogger<DataRetentionService> logger) : IDataRetentionService
{
    // Batch sizes bound the duration of one statement, not the amount: every category but accounts loops
    // until nothing is due any more. Constants rather than configuration — nobody tunes them per
    // deployment, and a configurable one would only be one more way to misconfigure the job.
    private const int VoteSessionBatchSize = 500;
    private const int AuditLogBatchSize = 5_000;

    public async Task<RetentionRunSummary> RunAsync(bool enforce, CancellationToken cancellationToken = default)
    {
        // Computed once, so every category of this pass measures against the same instant. UtcDateTime
        // carries DateTimeKind.Utc, which Npgsql requires for timestamptz and the channel purge checks.
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var tokensCleared = await ClearTokensAsync(enforce, now - RetentionPolicy.TwitchTokens, cancellationToken);
        var (accounts, removedAccountIds) = await RunAccountsAsync(enforce, now - RetentionPolicy.InactiveAccount, cancellationToken);
        var sessionCutoff = now - RetentionPolicy.EndedVoteSession;
        var voteSessions = await RunVoteSessionsAsync(enforce, sessionCutoff, removedAccountIds, cancellationToken);
        var auditEntriesDeleted = await RunAuditLogAsync(enforce, now - RetentionPolicy.AuditLogEntry, cancellationToken);
        var channels = await RunChannelsAsync(
            enforce, now, now - RetentionPolicy.DeactivatedChannel, sessionCutoff, removedAccountIds, cancellationToken);

        return new RetentionRunSummary(enforce, now, tokensCleared, accounts, voteSessions, auditEntriesDeleted, channels);
    }

    /// <summary>
    /// Category 1: one conditional statement, no audit entry. Postgres evaluates the WHERE against the
    /// current row version, so a login that commits first (fresh <c>LastLogin</c>) keeps its new tokens.
    /// </summary>
    private async Task<int> ClearTokensAsync(bool enforce, DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var due = db.Users
            .Where(AccountRetentionQueries.LastActiveBefore(cutoffUtc))
            .Where(HoldsTwitchTokens());

        if (!enforce)
        {
            return await due.CountAsync(cancellationToken);
        }

        return await due.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(u => u.TwitchRefreshToken, (string?)null)
                .SetProperty(u => u.TwitchAccessToken, (string?)null)
                .SetProperty(u => u.TwitchAccessTokenExpiresAtUtc, (DateTime?)null)
                .SetProperty(u => u.TwitchTokenScopes, (string?)null),
            cancellationToken);
    }

    /// <summary>
    /// Category 2: at most <see cref="RetentionOptions.MaxAccountsPerRun"/> candidates, each deleted in
    /// its own transaction through <see cref="IAccountDeletionService"/>, which rechecks the cutoff under
    /// the row lock. <c>StillActive</c>, <c>NotFound</c> and failures are counted and do not stop the
    /// others. Returns, besides the counts, the ids this pass removed (dry: would remove) — the later
    /// categories leave their votes out of their own cascade counts.
    /// </summary>
    private async Task<(AccountRetentionCounts Counts, IReadOnlyList<string> RemovedIds)> RunAccountsAsync(
        bool enforce, DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var maxAccounts = options.MaxAccountsPerRun;
        // One more than the cap, to know whether the cap cut the list. The longest-absent first; the id
        // as tie-breaker keeps the selection deterministic.
        var candidates = await db.Users
            .AsNoTracking()
            .Where(AccountRetentionQueries.LastActiveBefore(cutoffUtc))
            .OrderBy(u => u.LastLogin)
            .ThenBy(u => u.Id)
            .Select(u => u.Id)
            .Take(maxAccounts + 1)
            .ToListAsync(cancellationToken);
        var capReached = candidates.Count > maxAccounts;
        if (capReached)
        {
            candidates.RemoveAt(candidates.Count - 1);
        }

        if (candidates.Count == 0)
        {
            return (new AccountRetentionCounts(0, 0, 0, 0, capReached, 0, 0, 0), []);
        }

        return enforce
            ? await DeleteAccountsAsync(candidates, cutoffUtc, capReached, cancellationToken)
            : (await CountAccountsAsync(candidates, capReached, cancellationToken), candidates);
    }

    /// <summary>
    /// The dry run of category 2, set-based over the same candidates and through the same predicates the
    /// deletion uses. The audit count reproduces the deletion's per-account "distinct entries" summed over
    /// all candidates: an entry where one candidate acts on another counts once for each (as the two
    /// deletions do, one rewriting the actor, the other the target), an entry where a candidate acts on
    /// themself counts once.
    /// </summary>
    private async Task<AccountRetentionCounts> CountAccountsAsync(
        IReadOnlyList<string> candidates, bool capReached, CancellationToken cancellationToken)
    {
        var votes = await db.Votes.CastByAnyOf(candidates).CountAsync(cancellationToken);
        var votesInOpenSessions = await db.Votes
            .CastByAnyOf(candidates)
            .Where(v => v.VoteSession.IsActive)
            .CountAsync(cancellationToken);

        var asActor = await db.AuditLogEntries.ActedByAnyOf(candidates).CountAsync(cancellationToken);
        var asTarget = await db.AuditLogEntries.TargetingAnyOf(candidates).CountAsync(cancellationToken);
        var asActorOnThemself = await db.AuditLogEntries
            .TargetingAnyOf(candidates)
            .Where(e => e.TargetId == e.ActorTwitchUserId)
            .CountAsync(cancellationToken);

        return new AccountRetentionCounts(
            Deleted: candidates.Count,
            StillActive: 0,
            NotFound: 0,
            Failed: 0,
            capReached,
            votes,
            votesInOpenSessions,
            asActor + asTarget - asActorOnThemself);
    }

    private async Task<(AccountRetentionCounts Counts, IReadOnlyList<string> RemovedIds)> DeleteAccountsAsync(
        IReadOnlyList<string> candidates, DateTime cutoffUtc, bool capReached, CancellationToken cancellationToken)
    {
        var removed = new List<string>(candidates.Count);
        int stillActive = 0, notFound = 0, failed = 0, votes = 0, votesInOpenSessions = 0, pseudonymised = 0;

        foreach (var id in candidates)
        {
            try
            {
                var result = await accountDeletionService.DeleteAsync(
                    id, AuditActor.System, AccountDeletionReason.Inactivity, cutoffUtc, cancellationToken);
                switch (result.Outcome)
                {
                    case AccountDeletionOutcome.Deleted:
                        removed.Add(id);
                        votes += result.VotesDeleted;
                        votesInOpenSessions += result.VotesInOpenSessionsDeleted;
                        pseudonymised += result.AuditEntriesPseudonymised;
                        break;
                    case AccountDeletionOutcome.StillActive:
                        stillActive++;
                        break;
                    case AccountDeletionOutcome.NotFound:
                        notFound++;
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown account deletion outcome {result.Outcome}.");
                }
            }
            catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
            {
                // A deadlock with a concurrent admin action, a vote racing the deletion into the FK: the
                // deletion rolled back and the account stays a candidate for the next pass.
                failed++;
                logger.LogWarning(
                    "Retention: deleting an inactive account failed ({Failure}); it was rolled back and stays due for the next pass.",
                    DescribeFailure(exception));
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        var counts = new AccountRetentionCounts(
            removed.Count, stillActive, notFound, failed, capReached, votes, votesInOpenSessions, pseudonymised);
        return (counts, removed);
    }

    /// <summary>
    /// Category 3: ended sessions in id batches (keyset, so the dry run walks the same batches the
    /// enforced run deletes). Votes and ballot rows go by DB cascade; counted first, from the same batch.
    /// No audit entry per session (plan, decision 3).
    /// </summary>
    private async Task<VoteSessionRetentionCounts> RunVoteSessionsAsync(
        bool enforce, DateTime cutoffUtc, IReadOnlyList<string> removedAccountIds, CancellationToken cancellationToken)
    {
        int deleted = 0, votes = 0, ballotEntries = 0;
        var lastId = 0L;
        while (true)
        {
            var batch = await db.VoteSessions
                .Where(EndedBefore(cutoffUtc))
                .Where(s => s.Id > lastId)
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .Take(VoteSessionBatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            lastId = batch[^1];
            // The predicate once more on top of the ids: the delete is conditional on the row still being
            // due, not only on having been selected.
            var sessionIds = db.VoteSessions
                .Where(EndedBefore(cutoffUtc))
                .Where(s => batch.Contains(s.Id))
                .Select(s => s.Id);

            votes += await db.Votes
                .Where(v => sessionIds.Contains(v.VoteSessionId) && !removedAccountIds.Contains(v.UserId))
                .CountAsync(cancellationToken);
            ballotEntries += await db.VoteSessionEmotes
                .Where(b => sessionIds.Contains(b.VoteSessionId))
                .CountAsync(cancellationToken);
            deleted += enforce
                ? await db.VoteSessions.Where(s => sessionIds.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken)
                : batch.Count;
        }

        return new VoteSessionRetentionCounts(deleted, votes, ballotEntries);
    }

    /// <summary>
    /// Category 4: entries older than the period, in id batches. Selected ids plus a <c>Contains</c>
    /// delete rather than <c>Take</c> on <c>ExecuteDelete</c>: a row-limited bulk delete is not a
    /// portable SQL shape (Postgres has no <c>DELETE … LIMIT</c>), whereas <c>"Id" = ANY(@ids)</c> is a
    /// plain primary-key delete, and the same keyset walk serves the dry run.
    /// </summary>
    private async Task<int> RunAuditLogAsync(bool enforce, DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var deleted = 0;
        var lastId = 0L;
        while (true)
        {
            var batch = await db.AuditLogEntries
                .Where(e => e.OccurredAtUtc < cutoffUtc && e.Id > lastId)
                .OrderBy(e => e.Id)
                .Select(e => e.Id)
                .Take(AuditLogBatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            lastId = batch[^1];
            deleted += enforce
                ? await db.AuditLogEntries
                    .Where(e => batch.Contains(e.Id) && e.OccurredAtUtc < cutoffUtc)
                    .ExecuteDeleteAsync(cancellationToken)
                : batch.Count;
        }

        return deleted;
    }

    /// <summary>
    /// Category 5: first stamp inactive channels that have no <c>DeactivatedAtUtc</c> (in a dry run too —
    /// otherwise their period never starts; stamped with <paramref name="nowUtc"/>, so never due in this
    /// pass), then purge each due channel through <see cref="IChannelService.PurgeIfInactiveSinceAsync"/>,
    /// which rechecks under the row lock and audits <c>channel.purge</c>. Candidates are selected
    /// untracked by id and name; the cascade is counted by id before the purge, which goes by name.
    /// </summary>
    private async Task<ChannelRetentionCounts> RunChannelsAsync(
        bool enforce,
        DateTime nowUtc,
        DateTime cutoffUtc,
        DateTime sessionCutoffUtc,
        IReadOnlyList<string> removedAccountIds,
        CancellationToken cancellationToken)
    {
        // Conditional, so a join that reactivates the row first is not stamped.
        var restamped = await db.Channels
            .Where(MissingDeactivationStamp())
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.DeactivatedAtUtc, (DateTime?)nowUtc), cancellationToken);

        var candidates = await db.Channels
            .AsNoTracking()
            .Where(DeactivatedBefore(cutoffUtc))
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.ChannelName })
            .ToListAsync(cancellationToken);

        int purged = 0, stillActive = 0, notFound = 0, failed = 0;
        var cascade = ChannelCascade.None;
        foreach (var candidate in candidates)
        {
            var counted = await CountChannelCascadeAsync(candidate.Id, sessionCutoffUtc, removedAccountIds, cancellationToken);
            if (!enforce)
            {
                purged++;
                cascade += counted;
                continue;
            }

            try
            {
                var result = await channelService.PurgeIfInactiveSinceAsync(
                    candidate.ChannelName, cutoffUtc, AuditActor.System, cancellationToken);
                switch (result)
                {
                    case ChannelRetentionPurgeResult.Purged:
                        purged++;
                        cascade += counted;
                        break;
                    case ChannelRetentionPurgeResult.StillActive:
                        stillActive++;
                        break;
                    case ChannelRetentionPurgeResult.NotFound:
                        notFound++;
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown channel purge result {result}.");
                }
            }
            catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
            {
                // E.g. a deadlock on vote rows with a concurrent admin account deletion (accepted, see the
                // decision log): rolled back, due again on the next pass.
                failed++;
                logger.LogWarning(
                    "Retention: purging a deactivated channel failed ({Failure}); it was rolled back and stays due for the next pass.",
                    DescribeFailure(exception));
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return new ChannelRetentionCounts(
            restamped,
            purged,
            stillActive,
            notFound,
            failed,
            cascade.Emotes,
            cascade.UsageRows,
            cascade.LiveDays,
            cascade.VoteSessions,
            cascade.Votes);
    }

    /// <summary>
    /// What a purge of this channel takes down by cascade, leaving out what earlier categories of the pass
    /// cover: the channel's ended sessions due under their own period (category 3) and the votes of the
    /// accounts this pass removes (category 2).
    /// </summary>
    private async Task<ChannelCascade> CountChannelCascadeAsync(
        string channelId, DateTime sessionCutoffUtc, IReadOnlyList<string> removedAccountIds, CancellationToken cancellationToken)
    {
        var emoteIds = db.Emotes.Where(e => e.ChannelId == channelId).Select(e => e.Id);
        var remainingSessionIds = db.VoteSessions
            .Where(s => s.ChannelId == channelId)
            .Where(Not(EndedBefore(sessionCutoffUtc)))
            .Select(s => s.Id);

        return new ChannelCascade(
            Emotes: await emoteIds.CountAsync(cancellationToken),
            UsageRows: await db.UsageStats.CountAsync(u => emoteIds.Contains(u.EmoteId), cancellationToken),
            LiveDays: await db.ChannelLiveDays.CountAsync(d => d.ChannelId == channelId, cancellationToken),
            VoteSessions: await remainingSessionIds.CountAsync(cancellationToken),
            Votes: await db.Votes.CountAsync(
                v => remainingSessionIds.Contains(v.VoteSessionId) && !removedAccountIds.Contains(v.UserId),
                cancellationToken));
    }

    /// <summary>
    /// Ended at least one period ago. <c>COALESCE(EndedAt, StartedAt)</c>: <c>IsActive = false</c> is only
    /// ever set together with <c>EndedAt</c>, so the fallback is theoretical — but a session without an end
    /// is at least as old as its start. Open sessions never match.
    /// </summary>
    private static Expression<Func<VoteSession, bool>> EndedBefore(DateTime cutoffUtc) =>
        s => !s.IsActive && (s.EndedAt ?? s.StartedAt) < cutoffUtc;

    /// <summary>Left, and stamped as deactivated at least one period ago. A missing stamp is never due.</summary>
    private static Expression<Func<Channel, bool>> DeactivatedBefore(DateTime cutoffUtc) =>
        c => !c.IsBotActive && c.DeactivatedAtUtc < cutoffUtc;

    /// <summary>Left under an image that did not stamp yet (the window between migration and deploy).</summary>
    private static Expression<Func<Channel, bool>> MissingDeactivationStamp() =>
        c => !c.IsBotActive && c.DeactivatedAtUtc == null;

    /// <summary>Holds any of the four token columns — they are written and cleared together.</summary>
    private static Expression<Func<User, bool>> HoldsTwitchTokens() =>
        u => u.TwitchRefreshToken != null
            || u.TwitchAccessToken != null
            || u.TwitchAccessTokenExpiresAtUtc != null
            || u.TwitchTokenScopes != null;

    private static Expression<Func<T, bool>> Not<T>(Expression<Func<T, bool>> predicate) =>
        Expression.Lambda<Func<T, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    /// <summary>
    /// Exception type plus, if a Postgres error is in the chain, its SQLSTATE (40P01 deadlock, 23503 FK,
    /// …). Enough to tell the known cases apart without logging a message that may quote a key.
    /// </summary>
    private static string DescribeFailure(Exception exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException postgres)
            {
                return $"{exception.GetType().Name}, SQLSTATE {postgres.SqlState}";
            }
        }

        return exception.GetType().Name;
    }

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private readonly record struct ChannelCascade(int Emotes, int UsageRows, int LiveDays, int VoteSessions, int Votes)
    {
        public static ChannelCascade None => default;

        public static ChannelCascade operator +(ChannelCascade a, ChannelCascade b) => new(
            a.Emotes + b.Emotes,
            a.UsageRows + b.UsageRows,
            a.LiveDays + b.LiveDays,
            a.VoteSessions + b.VoteSessions,
            a.Votes + b.Votes);
    }
}
