namespace EmotePurge.Infrastructure.Migrations;

// One hand-written switch entry for the AddUsageStatEmoteSetId migration (T1.3b, spec section 4.2,
// E1): a channel that changed its active 7TV emote set at a known point in time. BoundaryUtc is
// deliberately a calendar day, not a timestamp — every rule in 4.2 that touches it only ever
// compares it against UsageStat.Date (itself a DateOnly) via a "::date" cast, so there is no
// time-of-day to lose or misinterpret here. T1.3b's committed SetSwitchAssignments constant is
// expected to reuse this record rather than defining a second, near-identical one.
public sealed record SetSwitchAssignment(
    string TwitchChannelId,
    string OldEmoteSetId,
    string NewEmoteSetId,
    DateOnly BoundaryUtc);

// Pass/fail plus, on failure, the message the migration's RAISE EXCEPTION would carry. A struct
// rather than a bool: every abort check in 4.2 names the offending channel (or, for check 3, the
// channel, the boundary, and the usage range) in its exception text, and the unit tests assert on
// that text so a future edit cannot quietly change which condition a message describes.
public readonly record struct MigrationCheckResult
{
    public bool Passed { get; }
    public string? FailureMessage { get; }

    private MigrationCheckResult(bool passed, string? failureMessage)
    {
        Passed = passed;
        FailureMessage = failureMessage;
    }

    public static MigrationCheckResult Ok() => new(true, null);

    public static MigrationCheckResult Fail(string message) => new(false, message);
}

// The decision logic behind the AddUsageStatEmoteSetId migration's three abort checks and its
// two-stage backfill assignment rule (spec section 4.2), extracted into pure functions so every
// branch is unit-tested without a database (T1.3a). T1.3b renders the same rules as raw SQL
// (RAISE EXCEPTION / UPDATE) inside the actual migration — this type is not on that runtime path at
// all, it is the tested specification the SQL has to match.
//
// All three checks share one restriction, spelled out in 4.2: they only ever see channels that
// exist in the database the migration runs against (and, for checks 1 and 3, only such channels
// with at least one UsageStats row). Callers are responsible for building their inputs that way —
// on an empty database, every dictionary here is empty and every check passes trivially, which is
// exactly what lets PostgresFixture run the full migration chain against a channel-less container.
public static class UsageStatMigrationChecks
{
    // Check 1 (spec 4.2, step 3): for every switch assignment whose channel exists in the database,
    // NewEmoteSetId must equal that channel's current ActiveEmoteSetId, and no channel may carry
    // more than one assignment. activeEmoteSetIdByExistingChannel is keyed by TwitchChannelId and
    // must contain only channels that exist in the database — an assignment whose TwitchChannelId is
    // not a key here is silently skipped, per the restriction above.
    public static MigrationCheckResult CheckAssignmentsAgainstCurrentState(
        IReadOnlyList<SetSwitchAssignment> assignments,
        IReadOnlyDictionary<string, string> activeEmoteSetIdByExistingChannel)
    {
        var byChannel = assignments
            .Where(a => activeEmoteSetIdByExistingChannel.ContainsKey(a.TwitchChannelId))
            .GroupBy(a => a.TwitchChannelId);

        foreach (var group in byChannel)
        {
            if (group.Count() > 1)
            {
                return MigrationCheckResult.Fail(
                    $"set-switch assignment stale or duplicated for channel {group.Key}");
            }

            var assignment = group.Single();
            var activeEmoteSetId = activeEmoteSetIdByExistingChannel[group.Key];
            if (!string.Equals(assignment.NewEmoteSetId, activeEmoteSetId, StringComparison.Ordinal))
            {
                return MigrationCheckResult.Fail(
                    $"set-switch assignment stale or duplicated for channel {group.Key}");
            }
        }

        return MigrationCheckResult.Ok();
    }

    // Check 2 (spec 4.2, step 4): no UsageStats row may belong to a channel with an empty
    // ActiveEmoteSetId — the backfill would otherwise hand such a row an empty EmoteSetId.
    // activeEmoteSetIdByChannelWithUsage covers every channel that has at least one UsageStats row,
    // keyed by whatever channel identifier the caller finds convenient (this check never joins
    // against the switch list, so it does not need to agree with check 1's key kind).
    public static MigrationCheckResult CheckNoEmptyActiveEmoteSetIdOnChannelsWithUsage(
        IReadOnlyDictionary<string, string> activeEmoteSetIdByChannelWithUsage)
    {
        foreach (var (channelId, activeEmoteSetId) in activeEmoteSetIdByChannelWithUsage)
        {
            if (string.IsNullOrEmpty(activeEmoteSetId))
            {
                return MigrationCheckResult.Fail(
                    $"channel {channelId} has usage stats but an empty active emote set id");
            }
        }

        return MigrationCheckResult.Ok();
    }

    // Check 3 (spec 4.2, step 5): every switch assignment's BoundaryUtc must fall within the usage
    // date range (inclusive) of its own channel. usageDateRangeByChannelWithUsage is keyed by
    // TwitchChannelId and covers only channels that both exist and have at least one UsageStats row
    // — an assignment whose channel is missing from it is silently skipped, per the restriction
    // above (no usage rows to misplace, or the channel does not exist at all).
    public static MigrationCheckResult CheckBoundariesWithinUsageRange(
        IReadOnlyList<SetSwitchAssignment> assignments,
        IReadOnlyDictionary<string, (DateOnly MinDate, DateOnly MaxDate)> usageDateRangeByChannelWithUsage)
    {
        foreach (var assignment in assignments)
        {
            if (!usageDateRangeByChannelWithUsage.TryGetValue(
                    assignment.TwitchChannelId, out var range))
            {
                continue;
            }

            if (assignment.BoundaryUtc < range.MinDate || assignment.BoundaryUtc > range.MaxDate)
            {
                return MigrationCheckResult.Fail(
                    $"set-switch boundary {assignment.BoundaryUtc:O} for channel "
                        + $"{assignment.TwitchChannelId} lies outside its usage range "
                        + $"{range.MinDate:O}..{range.MaxDate:O}");
            }
        }

        return MigrationCheckResult.Ok();
    }

    // The two-stage backfill rule (spec 4.2, step 7) for a single UsageStats row: (a) the default is
    // the channel's current ActiveEmoteSetId; (b) a channel with a switch assignment overrides that
    // default with OldEmoteSetId for every row strictly before BoundaryUtc. The boundary day itself,
    // like every day after it, keeps the default — i.e. goes to NewEmoteSetId, which is exactly
    // activeEmoteSetId for a channel whose assignment passed check 1. A channel with no assignment
    // never enters the "before the boundary" branch at all, so it always keeps the default.
    public static string ResolveEmoteSetId(
        IReadOnlyList<SetSwitchAssignment> assignments,
        string twitchChannelId,
        string activeEmoteSetId,
        DateOnly usageDate)
    {
        var assignment = assignments.FirstOrDefault(a => a.TwitchChannelId == twitchChannelId);
        if (assignment is null)
        {
            return activeEmoteSetId;
        }

        return usageDate < assignment.BoundaryUtc ? assignment.OldEmoteSetId : activeEmoteSetId;
    }
}
