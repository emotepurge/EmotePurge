using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Pure and clock-free — the shape of the one log line DataRetentionWorker writes per tick.
public class RetentionRunSummaryFormatterTests
{
    [Fact]
    public void Format_AllZero_StillProducesEveryField()
    {
        var summary = new RetentionRunSummary(
            Enforced: false,
            ReferenceTimeUtc: DateTime.UtcNow,
            TokensCleared: 0,
            Accounts: new AccountRetentionCounts(0, 0, 0, 0, false, 0, 0, 0),
            VoteSessions: new VoteSessionRetentionCounts(0, 0, 0),
            AuditEntriesDeleted: 0,
            Channels: new ChannelRetentionCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

        var line = RetentionRunSummaryFormatter.Format(summary);

        Assert.Equal(
            "tokens cleared: 0; " +
            "accounts deleted: 0, still active: 0, not found: 0, failed: 0, cap reached: False, " +
            "votes deleted: 0 (0 in open sessions), audit entries pseudonymised: 0; " +
            "vote sessions deleted: 0, votes deleted: 0, ballot entries deleted: 0; " +
            "audit log entries deleted: 0; " +
            "channels restamped: 0, purged: 0, still active: 0, not found: 0, failed: 0, " +
            "emotes deleted: 0, usage rows deleted: 0, live days deleted: 0, vote sessions deleted: 0, " +
            "votes deleted: 0, observation intervals deleted: 0",
            line);
    }

    [Fact]
    public void Format_CarriesEveryCountThroughToItsOwnLabel()
    {
        var summary = new RetentionRunSummary(
            Enforced: true,
            ReferenceTimeUtc: DateTime.UtcNow,
            TokensCleared: 3,
            Accounts: new AccountRetentionCounts(
                Deleted: 2,
                StillActive: 1,
                NotFound: 4,
                Failed: 5,
                CapReached: true,
                VotesDeleted: 6,
                VotesInOpenSessionsDeleted: 7,
                AuditEntriesPseudonymised: 8),
            VoteSessions: new VoteSessionRetentionCounts(Deleted: 9, VotesDeleted: 10, BallotEntriesDeleted: 11),
            AuditEntriesDeleted: 12,
            Channels: new ChannelRetentionCounts(
                Restamped: 13,
                Purged: 14,
                StillActive: 15,
                NotFound: 16,
                Failed: 17,
                EmotesDeleted: 18,
                UsageRowsDeleted: 19,
                LiveDaysDeleted: 20,
                VoteSessionsDeleted: 21,
                VotesDeleted: 22,
                ObservationsDeleted: 23));

        var line = RetentionRunSummaryFormatter.Format(summary);

        Assert.Equal(
            "tokens cleared: 3; " +
            "accounts deleted: 2, still active: 1, not found: 4, failed: 5, cap reached: True, " +
            "votes deleted: 6 (7 in open sessions), audit entries pseudonymised: 8; " +
            "vote sessions deleted: 9, votes deleted: 10, ballot entries deleted: 11; " +
            "audit log entries deleted: 12; " +
            "channels restamped: 13, purged: 14, still active: 15, not found: 16, failed: 17, " +
            "emotes deleted: 18, usage rows deleted: 19, live days deleted: 20, vote sessions deleted: 21, " +
            "votes deleted: 22, observation intervals deleted: 23",
            line);
    }
}
