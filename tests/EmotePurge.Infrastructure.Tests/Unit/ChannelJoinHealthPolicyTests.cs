using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class ChannelJoinHealthPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan Ttl = WorkerHealthKeys.RosterTtl;

    private static WorkerRosterChannel Row(
        string name, bool confirmed = false, bool intent = true, int? seconds = null, string? reason = null) =>
        new(name, confirmed, null, null, false, null, false, intent, seconds, reason);

    private static WorkerRosterSnapshot Roster(
        IEnumerable<WorkerRosterChannel>? rows = null,
        int schemaVersion = WorkerRosterSnapshot.CurrentSchemaVersion,
        DateTime? generatedAt = null,
        DateTime? processStarted = null,
        bool bootRecoveryCompleted = true,
        bool truncated = false) =>
        new(generatedAt ?? Now, "w", processStarted ?? Now.AddHours(-5), bootRecoveryCompleted, truncated, rows?.ToList(), schemaVersion);

    private static ActiveChannelActivation Active(string name, DateTime? activatedAt = null) =>
        new(name, activatedAt ?? Now.AddHours(-10));

    private static ChannelJoinHealthReport Evaluate(
        WorkerRosterSnapshot? roster,
        IReadOnlyList<ActiveChannelActivation>? desired = null,
        DateTime? apiStarted = null,
        DateTime? now = null) =>
        ChannelJoinHealthPolicy.Evaluate(roster, desired ?? [], now ?? Now, apiStarted ?? Now.AddHours(-1), Ttl, Grace);

    [Theory]
    [InlineData(179, ChannelJoinHealthStatus.Starting)]
    [InlineData(180, ChannelJoinHealthStatus.Starting)]
    [InlineData(181, ChannelJoinHealthStatus.Unavailable)]
    public void MissingRoster_IsStartingOnlyWhileTheApiIsYoungerThanTheRosterTtl(int apiAgeSeconds, ChannelJoinHealthStatus expected)
    {
        var report = Evaluate(null, apiStarted: Now.AddSeconds(-apiAgeSeconds));

        Assert.Equal(expected, report.Status);
    }

    [Theory]
    [InlineData(179, ChannelJoinHealthStatus.Starting)]
    [InlineData(181, ChannelJoinHealthStatus.Unavailable)]
    public void StaleRoster_IsTreatedLikeAMissingOne(int apiAgeSeconds, ChannelJoinHealthStatus expected)
    {
        var stale = Roster([Row("a", seconds: 9999)], generatedAt: Now.AddSeconds(-181));

        var report = Evaluate(stale, apiStarted: Now.AddSeconds(-apiAgeSeconds));

        Assert.Equal(expected, report.Status);
        Assert.Empty(report.Entries);
    }

    [Fact]
    public void RosterExactlyAtTheTtl_IsStillFresh()
    {
        var report = Evaluate(Roster(generatedAt: Now.AddSeconds(-180)));

        Assert.Equal(ChannelJoinHealthStatus.Healthy, report.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyRoster_IsUnavailable_EmptyOrNot(bool withRows)
    {
        var legacy = Roster(withRows ? [Row("a", seconds: 9999)] : [], schemaVersion: 0);

        var report = Evaluate(legacy, [Active("a")]);

        Assert.Equal(ChannelJoinHealthStatus.Unavailable, report.Status);
    }

    [Fact]
    public void TruncatedRoster_IsUnavailable() =>
        Assert.Equal(ChannelJoinHealthStatus.Unavailable, Evaluate(Roster(truncated: true)).Status);

    [Fact]
    public void FailedDesiredListRead_IsUnavailable() =>
        Assert.Equal(
            ChannelJoinHealthStatus.Unavailable,
            ChannelJoinHealthPolicy.Evaluate(Roster(), null, Now, Now.AddHours(-1), Ttl, Grace).Status);

    [Fact]
    public void EmptyRosterWithEmptyDesiredList_IsHealthy()
    {
        var report = Evaluate(Roster([]), []);

        Assert.Equal(ChannelJoinHealthStatus.Healthy, report.Status);
        Assert.Equal(0, report.AlarmingCount);
    }

    [Theory]
    [InlineData(300, false, ChannelJoinHealthStatus.Healthy)]
    [InlineData(301, true, ChannelJoinHealthStatus.Failing)]
    public void UnconfirmedRow_IsOverdueOnlyBeyondTheGrace(int seconds, bool overdue, ChannelJoinHealthStatus expected)
    {
        var report = Evaluate(Roster([Row("a", seconds: seconds, reason: TwitchJoinFailureReasons.NoConfirmation)]));

        Assert.Equal(expected, report.Status);
        var entry = Assert.Single(report.Entries);
        Assert.Equal(overdue, entry.Overdue);
        Assert.Equal(overdue, entry.CountsForAlarm);
        Assert.Equal(seconds, entry.UnconfirmedSeconds);
    }

    [Fact]
    public void ConfirmedRow_IsNotListed()
    {
        var report = Evaluate(Roster([Row("a", confirmed: true, seconds: 9999)]));

        Assert.Empty(report.Entries);
        Assert.Equal(ChannelJoinHealthStatus.Healthy, report.Status);
    }

    [Fact]
    public void SevenTvOnlyRow_WithAHighNumber_IsIgnored()
    {
        var report = Evaluate(Roster([Row("a", intent: false, seconds: 9999)]));

        Assert.Equal(ChannelJoinHealthStatus.Healthy, report.Status);
        Assert.Empty(report.Entries);
    }

    [Theory]
    [InlineData(TwitchJoinFailureReasons.ChannelSuspended)]
    [InlineData(TwitchJoinFailureReasons.Banned)]
    public void OverdueSuspendedOrBanned_IsListedButDoesNotAlarm(string reason)
    {
        var report = Evaluate(Roster([Row("a", seconds: 5000, reason: reason)]));

        Assert.Equal(ChannelJoinHealthStatus.Healthy, report.Status);
        Assert.Equal(1, report.ExcludedCount);
        Assert.Equal(0, report.AlarmingCount);
        var entry = Assert.Single(report.Entries);
        Assert.True(entry.Overdue);
        Assert.False(entry.CountsForAlarm);
    }

    [Fact]
    public void ExcludedRowPlusAnOverdueNoConfirmation_IsFailing()
    {
        var report = Evaluate(Roster([
            Row("a", seconds: 5000, reason: TwitchJoinFailureReasons.ChannelSuspended),
            Row("b", seconds: 400, reason: TwitchJoinFailureReasons.NoConfirmation)]));

        Assert.Equal(ChannelJoinHealthStatus.Failing, report.Status);
        Assert.Equal(1, report.AlarmingCount);
        Assert.Equal(1, report.ExcludedCount);
    }

    [Fact]
    public void SilentFailurePlusAnUnrelatedLimitRejection_AreBothListedAndAlarming()
    {
        var report = Evaluate(Roster([
            Row("silent", seconds: 600, reason: TwitchJoinFailureReasons.NoConfirmation),
            Row("limit", seconds: 600, reason: TwitchJoinFailureReasons.ConcurrentChannelLimit)]));

        Assert.Equal(ChannelJoinHealthStatus.Failing, report.Status);
        Assert.Equal(2, report.AlarmingCount);
        Assert.All(report.Entries, e => Assert.True(e.CountsForAlarm));
        Assert.Equal(["limit", "silent"], report.Entries.Select(e => e.ChannelName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void UnconfirmedRowWithinTheGrace_IsListedAsWaiting()
    {
        var report = Evaluate(Roster([Row("a", seconds: 10)]));

        Assert.Equal(ChannelJoinHealthStatus.Healthy, report.Status);
        Assert.False(Assert.Single(report.Entries).Overdue);
    }

    [Fact]
    public void BootRecoveryNotCompleted_ChangesNothing()
    {
        var report = Evaluate(Roster([Row("a", seconds: 400)], bootRecoveryCompleted: false));

        Assert.Equal(ChannelJoinHealthStatus.Failing, report.Status);
    }

    [Theory]
    [InlineData(301, true, ChannelJoinHealthStatus.Failing)]
    [InlineData(300, false, ChannelJoinHealthStatus.Healthy)]
    public void ActiveChannelWithoutAWish_IsOverdueOnlyBeyondTheGrace(int waitedSeconds, bool overdue, ChannelJoinHealthStatus expected)
    {
        var report = Evaluate(Roster([]), [Active("a", Now.AddSeconds(-waitedSeconds))]);

        Assert.Equal(expected, report.Status);
        var missing = Assert.Single(report.MissingIntent);
        Assert.Equal(overdue, missing.Overdue);
        Assert.Equal(waitedSeconds, missing.WaitingSeconds);
        Assert.Equal(overdue ? 1 : 0, report.AlarmingCount);
    }

    [Fact]
    public void ActiveChannelWithoutAWish_AfterAFreshWorkerStart_IsNotOverdue()
    {
        var report = Evaluate(Roster([], processStarted: Now.AddSeconds(-100)), [Active("a", Now.AddDays(-30))]);

        Assert.Equal(ChannelJoinHealthStatus.Healthy, report.Status);
        Assert.Equal(100, Assert.Single(report.MissingIntent).WaitingSeconds);
    }

    [Fact]
    public void ActiveChannelWithoutAWish_AfterAWorkerStartLongAgo_UsesTheActivation()
    {
        var report = Evaluate(
            Roster([], processStarted: Now.AddSeconds(-1000)), [Active("a", Now.AddSeconds(-50))]);

        Assert.Equal(50, Assert.Single(report.MissingIntent).WaitingSeconds);
    }

    [Fact]
    public void ActivationInTheFuture_WaitsZero()
    {
        var report = Evaluate(Roster([]), [Active("a", Now.AddSeconds(30))]);

        Assert.Equal(0, Assert.Single(report.MissingIntent).WaitingSeconds);
    }

    [Fact]
    public void ActiveChannelWithASevenTvOnlyRow_CountsAsWithoutAWish()
    {
        var report = Evaluate(Roster([Row("a", intent: false)]), [Active("a")]);

        Assert.Equal(ChannelJoinHealthStatus.Failing, report.Status);
        Assert.Equal("a", Assert.Single(report.MissingIntent).ChannelName);
    }

    [Fact]
    public void ActiveChannelWithAWish_MatchesIgnoringCase()
    {
        var report = Evaluate(Roster([Row("SomeChannel", confirmed: true)]), [Active("somechannel")]);

        Assert.Empty(report.MissingIntent);
        Assert.Equal(ChannelJoinHealthStatus.Healthy, report.Status);
    }

    [Fact]
    public void WishRowOutsideTheDesiredList_IsStillJudgedByTheRosterRule()
    {
        var report = Evaluate(Roster([Row("leaving", seconds: 900, reason: TwitchJoinFailureReasons.NoConfirmation)]), []);

        Assert.Equal(ChannelJoinHealthStatus.Failing, report.Status);
    }

    [Fact]
    public void AlarmingCount_AddsBothKinds()
    {
        var report = Evaluate(
            Roster([Row("a", seconds: 900, reason: TwitchJoinFailureReasons.NoConfirmation)]), [Active("b")]);

        Assert.Equal(2, report.AlarmingCount);
        Assert.Equal(300, report.GraceSeconds);
    }
}
