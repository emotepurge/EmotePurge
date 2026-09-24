using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

/// <summary>
/// Renders a <see cref="RetentionRunSummary"/> as the one log line <see cref="DataRetentionWorker"/>
/// writes per tick. Pure and clock-free so the shape of the line is testable without a logger or a
/// database — every field of the summary is a count, so nothing here ever touches a login, a user id
/// or a channel name (the class comment on <see cref="IDataRetentionService"/> is the contract this
/// keeps).
/// </summary>
public static class RetentionRunSummaryFormatter
{
    public static string Format(RetentionRunSummary summary)
    {
        var accounts = summary.Accounts;
        var voteSessions = summary.VoteSessions;
        var channels = summary.Channels;

        return
            $"tokens cleared: {summary.TokensCleared}; " +
            $"accounts deleted: {accounts.Deleted}, still active: {accounts.StillActive}, " +
            $"not found: {accounts.NotFound}, failed: {accounts.Failed}, cap reached: {accounts.CapReached}, " +
            $"votes deleted: {accounts.VotesDeleted} ({accounts.VotesInOpenSessionsDeleted} in open sessions), " +
            $"audit entries pseudonymised: {accounts.AuditEntriesPseudonymised}; " +
            $"vote sessions deleted: {voteSessions.Deleted}, votes deleted: {voteSessions.VotesDeleted}, " +
            $"ballot entries deleted: {voteSessions.BallotEntriesDeleted}; " +
            $"audit log entries deleted: {summary.AuditEntriesDeleted}; " +
            $"channels restamped: {channels.Restamped}, purged: {channels.Purged}, " +
            $"still active: {channels.StillActive}, not found: {channels.NotFound}, failed: {channels.Failed}, " +
            $"emotes deleted: {channels.EmotesDeleted}, usage rows deleted: {channels.UsageRowsDeleted}, " +
            $"live days deleted: {channels.LiveDaysDeleted}, vote sessions deleted: {channels.VoteSessionsDeleted}, " +
            $"votes deleted: {channels.VotesDeleted}, observation intervals deleted: {channels.ObservationsDeleted}";
    }
}
