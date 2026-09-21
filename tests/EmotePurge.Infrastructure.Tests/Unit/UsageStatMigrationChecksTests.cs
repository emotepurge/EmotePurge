using EmotePurge.Infrastructure.Migrations;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Pure, dependency-free — no container, no migration, no SQL. Exercises the exact decision logic
// the AddUsageStatEmoteSetId migration (T1.3b, spec section 4.2) renders as raw SQL: the three abort
// checks and the two-stage backfill assignment rule. Every function here takes the switch list as a
// parameter; none of them read the committed SetSwitchAssignments constant (that constant, and the
// migration that actually calls this logic in SQL form, belong to T1.3b).
public class UsageStatMigrationChecksTests
{
    private const string ChannelTwitchId = "49140130";
    private const string OldSetId = "01GV88A38G0006FW5TVZVMG507";
    private const string NewSetId = "01J94NYQR0000D15QN0BDGN85E";

    // --- Check 1: assignment against current state (stale / duplicated) ---

    [Fact]
    public void CheckAssignmentsAgainstCurrentState_Passes_WhenNewSetIdMatchesActiveSet()
    {
        var assignments = new[]
        {
            new SetSwitchAssignment(ChannelTwitchId, OldSetId, NewSetId, new DateOnly(2026, 10, 1)),
        };
        var activeByChannel = new Dictionary<string, string> { [ChannelTwitchId] = NewSetId };

        var result = UsageStatMigrationChecks.CheckAssignmentsAgainstCurrentState(
            assignments, activeByChannel);

        Assert.True(result.Passed);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public void CheckAssignmentsAgainstCurrentState_Fails_WhenNewSetIdIsStale()
    {
        var assignments = new[]
        {
            new SetSwitchAssignment(ChannelTwitchId, OldSetId, NewSetId, new DateOnly(2026, 10, 1)),
        };
        // The channel's active set has moved on again since the assignment was written down.
        var activeByChannel = new Dictionary<string, string>
        {
            [ChannelTwitchId] = "01JYETTHISDOESNOTMATCH00000",
        };

        var result = UsageStatMigrationChecks.CheckAssignmentsAgainstCurrentState(
            assignments, activeByChannel);

        Assert.False(result.Passed);
        Assert.Contains(ChannelTwitchId, result.FailureMessage);
    }

    [Fact]
    public void CheckAssignmentsAgainstCurrentState_Fails_WhenChannelHasTwoAssignments()
    {
        var assignments = new[]
        {
            new SetSwitchAssignment(ChannelTwitchId, OldSetId, NewSetId, new DateOnly(2026, 10, 1)),
            new SetSwitchAssignment(
                ChannelTwitchId, NewSetId, "01JZANOTHERSETID0000000000", new DateOnly(2026, 11, 1)),
        };
        var activeByChannel = new Dictionary<string, string> { [ChannelTwitchId] = NewSetId };

        var result = UsageStatMigrationChecks.CheckAssignmentsAgainstCurrentState(
            assignments, activeByChannel);

        Assert.False(result.Passed);
        Assert.Contains(ChannelTwitchId, result.FailureMessage);
    }

    // --- Check 2: no empty active set id on a channel with usage rows ---

    [Fact]
    public void CheckNoEmptyActiveEmoteSetIdOnChannelsWithUsage_Passes_WhenAllPresent()
    {
        var activeByChannel = new Dictionary<string, string>
        {
            [ChannelTwitchId] = NewSetId,
            ["other-channel"] = OldSetId,
        };

        var result = UsageStatMigrationChecks.CheckNoEmptyActiveEmoteSetIdOnChannelsWithUsage(
            activeByChannel);

        Assert.True(result.Passed);
    }

    [Fact]
    public void CheckNoEmptyActiveEmoteSetIdOnChannelsWithUsage_Fails_WhenOneIsEmpty()
    {
        var activeByChannel = new Dictionary<string, string>
        {
            [ChannelTwitchId] = NewSetId,
            ["other-channel"] = string.Empty,
        };

        var result = UsageStatMigrationChecks.CheckNoEmptyActiveEmoteSetIdOnChannelsWithUsage(
            activeByChannel);

        Assert.False(result.Passed);
        Assert.Contains("other-channel", result.FailureMessage);
    }

    // --- Check 3: boundary date plausible ---

    [Fact]
    public void CheckBoundariesWithinUsageRange_Passes_WhenBoundaryWithinRange()
    {
        var assignments = new[]
        {
            new SetSwitchAssignment(ChannelTwitchId, OldSetId, NewSetId, new DateOnly(2026, 10, 1)),
        };
        var rangeByChannel = new Dictionary<string, (DateOnly MinDate, DateOnly MaxDate)>
        {
            [ChannelTwitchId] = (new DateOnly(2026, 7, 1), new DateOnly(2026, 12, 31)),
        };

        var result = UsageStatMigrationChecks.CheckBoundariesWithinUsageRange(
            assignments, rangeByChannel);

        Assert.True(result.Passed);
    }

    [Fact]
    public void CheckBoundariesWithinUsageRange_Fails_WhenBoundaryOutsideRange()
    {
        var assignments = new[]
        {
            // A mistyped year — the exact slip 4.2 calls out as the case this check exists for.
            new SetSwitchAssignment(ChannelTwitchId, OldSetId, NewSetId, new DateOnly(2027, 10, 1)),
        };
        var rangeByChannel = new Dictionary<string, (DateOnly MinDate, DateOnly MaxDate)>
        {
            [ChannelTwitchId] = (new DateOnly(2026, 7, 1), new DateOnly(2026, 12, 31)),
        };

        var result = UsageStatMigrationChecks.CheckBoundariesWithinUsageRange(
            assignments, rangeByChannel);

        Assert.False(result.Passed);
        Assert.Contains(ChannelTwitchId, result.FailureMessage);
    }

    // --- Two-stage backfill assignment rule ---

    [Fact]
    public void ResolveEmoteSetId_ReturnsActiveEmoteSetId_WhenChannelHasNoAssignment()
    {
        var assignments = Array.Empty<SetSwitchAssignment>();

        var resolved = UsageStatMigrationChecks.ResolveEmoteSetId(
            assignments, "some-other-channel", "01ACTIVESETID000000000000", new DateOnly(2026, 9, 15));

        Assert.Equal("01ACTIVESETID000000000000", resolved);
    }

    [Fact]
    public void ResolveEmoteSetId_ReturnsOldEmoteSetId_WhenDateIsBeforeBoundary()
    {
        var assignments = new[]
        {
            new SetSwitchAssignment(ChannelTwitchId, OldSetId, NewSetId, new DateOnly(2026, 10, 1)),
        };

        var resolved = UsageStatMigrationChecks.ResolveEmoteSetId(
            assignments, ChannelTwitchId, NewSetId, new DateOnly(2026, 9, 30));

        Assert.Equal(OldSetId, resolved);
    }

    [Fact]
    public void ResolveEmoteSetId_ReturnsNewEmoteSetId_WhenDateIsOnTheBoundaryDay()
    {
        var assignments = new[]
        {
            new SetSwitchAssignment(ChannelTwitchId, OldSetId, NewSetId, new DateOnly(2026, 10, 1)),
        };

        var resolved = UsageStatMigrationChecks.ResolveEmoteSetId(
            assignments, ChannelTwitchId, NewSetId, new DateOnly(2026, 10, 1));

        Assert.Equal(NewSetId, resolved);
    }

    [Fact]
    public void ResolveEmoteSetId_ReturnsNewEmoteSetId_WhenDateIsAfterBoundary()
    {
        var assignments = new[]
        {
            new SetSwitchAssignment(ChannelTwitchId, OldSetId, NewSetId, new DateOnly(2026, 10, 1)),
        };

        var resolved = UsageStatMigrationChecks.ResolveEmoteSetId(
            assignments, ChannelTwitchId, NewSetId, new DateOnly(2026, 10, 15));

        Assert.Equal(NewSetId, resolved);
    }
}
