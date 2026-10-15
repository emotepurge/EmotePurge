namespace EmotePurge.Core.Services;

/// <summary>
/// One retention pass over the categories of <see cref="RetentionPolicy"/>, in a fixed order:
/// Twitch tokens, inactive accounts, ended vote sessions, audit log entries, finished chat-log backfill
/// runs, deactivated channels, then system-load rows. Each system-load category is isolated so a
/// telemetry failure cannot skip personal-data retention or the pass summary.
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

    /// <summary>
    /// Expires captured log entries and stale capture heartbeat rows, deliberately <b>outside</b>
    /// <see cref="RunAsync"/>: logs are written continuously and expire in hours, not days, so they run
    /// on their own hourly schedule that neither waits for the boot gate nor shares a failure with the
    /// daily pass. The boundary is exclusive (<c>TimestampUtc &lt; now - RetentionPolicy.LogEntry</c>,
    /// <c>LastFlushUtc &lt; now - RetentionPolicy.LogEntry</c>); saved filters are never touched.
    /// </summary>
    /// <param name="enforce"><c>false</c> counts what would be deleted and writes nothing.</param>
    Task<LogRetentionSummary> ExpireLogsAsync(bool enforce, CancellationToken cancellationToken = default);
}

/// <summary>What one log-expiry pass did, or in a dry run would have done. Counts only.</summary>
/// <param name="Enforced">Whether the pass wrote.</param>
/// <param name="ReferenceTimeUtc">The instant the cutoff was computed from.</param>
/// <param name="LogEntriesDeleted">Log entries older than the period (deleted, or due in a dry run).</param>
/// <param name="LogCaptureStatusRowsDeleted">Heartbeat rows whose last flush is older than the period.</param>
public sealed record LogRetentionSummary(
    bool Enforced,
    DateTime ReferenceTimeUtc,
    int LogEntriesDeleted,
    int LogCaptureStatusRowsDeleted);

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
/// <param name="ChatLogBackfillRunsDeleted">
/// Finished chat-log backfill runs older than their period, with their snapshot rows (#346). Trailing
/// and optional so every existing construction keeps compiling; the step runs after the audit-log one.
/// </param>
/// <param name="SystemLoadRawSamplesDeleted">Old raw samples deleted, or due samples in a dry run.</param>
/// <param name="SystemLoadRawSamplesWithoutHourlyRow">Old raw samples retained because their hour or immediate successor's hour is not rolled up; zero when the raw category failed, with failure shown separately.</param>
/// <param name="SystemLoadHourlyRowsDeleted">Hourly summaries older than thirteen calendar months deleted, or due rows in a dry run.</param>
/// <param name="SystemLoadRawSamplesFailed">Whether raw-sample retention failed; its counts are unknown for this pass.</param>
/// <param name="SystemLoadHourlyRowsFailed">Whether hourly-row retention failed; its count is unknown for this pass.</param>
public sealed record RetentionRunSummary(
    bool Enforced,
    DateTime ReferenceTimeUtc,
    int TokensCleared,
    AccountRetentionCounts Accounts,
    VoteSessionRetentionCounts VoteSessions,
    int AuditEntriesDeleted,
    ChannelRetentionCounts Channels,
    int ChatLogBackfillRunsDeleted = 0,
    int SystemLoadRawSamplesDeleted = 0,
    int SystemLoadRawSamplesWithoutHourlyRow = 0,
    int SystemLoadHourlyRowsDeleted = 0,
    bool SystemLoadRawSamplesFailed = false,
    bool SystemLoadHourlyRowsFailed = false);

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
/// <param name="ObservationsDeleted">
/// Emote-set observation intervals (<c>ChannelEmoteSetObservation</c>, spec section 4.3) of the purged
/// channels — open or closed, since the cascade takes both.
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
    int VotesDeleted,
    int ObservationsDeleted);
