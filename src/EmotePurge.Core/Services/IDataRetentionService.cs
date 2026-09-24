namespace EmotePurge.Core.Services;

/// <summary>
/// One retention pass over the five categories of <see cref="RetentionPolicy"/>, in a fixed order:
/// Twitch tokens, inactive accounts, ended vote sessions, audit log entries, deactivated channels.
/// Called by the worker's retention job once per tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dry run and enforced run are the same code up to the write.</b> Both select the same parent rows
/// with the same predicate (one per category) and count the same dependent rows from those parents; the
/// enforced run then deletes what was selected, the dry run does not. A category that runs later sees
/// what an earlier one removed in an enforced run (an ended session whose voter was just deleted has one
/// vote fewer), and the dry run subtracts exactly that overlap, so without concurrent writers both modes
/// report the same numbers.
/// </para>
/// <para>
/// <b>The one write of a dry run</b> is stamping <c>DeactivatedAtUtc</c> on inactive channels that have
/// none (left under an image that predated the column): without it the channel period would never
/// start. The stamp is not destructive and makes no row due in the same pass.
/// </para>
/// <para>
/// Cutoffs are computed once per pass from the injected clock, in UTC. The summary carries counts only
/// — never a login, a user id or a channel name — so it can be logged as is.
/// </para>
/// </remarks>
public interface IDataRetentionService
{
    /// <param name="enforce">
    /// <c>false</c> counts what would be affected and writes nothing but the channel stamps;
    /// <c>true</c> deletes. Comes from <c>Retention:Enforce</c>, default <c>false</c>.
    /// </param>
    Task<RetentionRunSummary> RunAsync(bool enforce, CancellationToken cancellationToken = default);
}

/// <summary>
/// What one retention pass did — or, with <see cref="Enforced"/> <c>false</c>, would have done. Every
/// "deleted"/"cleared" count reads as "would be deleted" in a dry run.
/// </summary>
/// <param name="Enforced">Whether the pass wrote (<c>Retention:Enforce</c>).</param>
/// <param name="ReferenceTimeUtc">The instant every cutoff of this pass was computed from.</param>
/// <param name="TokensCleared">Users whose encrypted Twitch tokens were cleared.</param>
/// <param name="Accounts">Inactive accounts and what went with them.</param>
/// <param name="VoteSessions">Ended vote sessions and what went with them.</param>
/// <param name="AuditEntriesDeleted">Audit log entries older than the period.</param>
/// <param name="Channels">Deactivated channels and what went with them.</param>
public sealed record RetentionRunSummary(
    bool Enforced,
    DateTime ReferenceTimeUtc,
    int TokensCleared,
    AccountRetentionCounts Accounts,
    VoteSessionRetentionCounts VoteSessions,
    int AuditEntriesDeleted,
    ChannelRetentionCounts Channels);

/// <summary>The inactive-account category of a <see cref="RetentionRunSummary"/>.</summary>
/// <param name="Deleted">Accounts deleted through <see cref="IAccountDeletionService"/>.</param>
/// <param name="StillActive">
/// Candidates the deletion's recheck under the row lock found active again. Always 0 in a dry run,
/// which does not take the lock.
/// </param>
/// <param name="NotFound">Candidates that were already gone (deleted concurrently). Always 0 in a dry run.</param>
/// <param name="Failed">
/// Candidates whose deletion threw; rolled back, still candidates on the next pass. Always 0 in a dry run.
/// </param>
/// <param name="CapReached">
/// More accounts were due than <c>Retention:MaxAccountsPerRun</c> allows per pass; the rest follow on
/// the next pass.
/// </param>
/// <param name="VotesDeleted">Every vote of the deleted accounts, across all sessions.</param>
/// <param name="VotesInOpenSessionsDeleted">The part of <see cref="VotesDeleted"/> in still-running sessions.</param>
/// <param name="AuditEntriesPseudonymised">
/// Audit entries rewritten to the deleted-user marker, counted per account as
/// <see cref="AccountDeletionResult.AuditEntriesPseudonymised"/> does.
/// </param>
public sealed record AccountRetentionCounts(
    int Deleted,
    int StillActive,
    int NotFound,
    int Failed,
    bool CapReached,
    int VotesDeleted,
    int VotesInOpenSessionsDeleted,
    int AuditEntriesPseudonymised);

/// <summary>The ended-vote-session category of a <see cref="RetentionRunSummary"/>.</summary>
/// <param name="Deleted">Ended sessions deleted.</param>
/// <param name="VotesDeleted">Their votes (not counting votes an account deletion in the same pass already removed).</param>
/// <param name="BallotEntriesDeleted">Their explicit ballot rows (<c>VoteSessionEmote</c>).</param>
public sealed record VoteSessionRetentionCounts(int Deleted, int VotesDeleted, int BallotEntriesDeleted);

/// <summary>The deactivated-channel category of a <see cref="RetentionRunSummary"/>.</summary>
/// <param name="Restamped">
/// Inactive channels without a <c>DeactivatedAtUtc</c> that were stamped with the pass's reference
/// time — the period starts there. Written in a dry run too.
/// </param>
/// <param name="Purged">Channels deleted with their whole history.</param>
/// <param name="StillActive">
/// Candidates the purge's recheck under the row lock found active again or not due. Always 0 in a dry run.
/// </param>
/// <param name="NotFound">Candidates already gone (purged or renamed concurrently). Always 0 in a dry run.</param>
/// <param name="Failed">Candidates whose purge threw; rolled back, retried on the next pass. Always 0 in a dry run.</param>
/// <param name="EmotesDeleted">Emote rows of the purged channels.</param>
/// <param name="UsageRowsDeleted">Daily usage rows of those emotes.</param>
/// <param name="LiveDaysDeleted">Live-coverage days of the purged channels.</param>
/// <param name="VoteSessionsDeleted">
/// Vote sessions of the purged channels that the session category had not already removed (open ones,
/// and ended ones younger than their own period).
/// </param>
/// <param name="VotesDeleted">
/// Votes in those sessions, not counting votes an account deletion in the same pass already removed.
/// </param>
public sealed record ChannelRetentionCounts(
    int Restamped,
    int Purged,
    int StillActive,
    int NotFound,
    int Failed,
    int EmotesDeleted,
    int UsageRowsDeleted,
    int LiveDaysDeleted,
    int VoteSessionsDeleted,
    int VotesDeleted);
