namespace EmotePurge.Core.Services;

// Read side of the admin dashboard (#381). Contracts only; the aggregation lives in Infrastructure.
public interface IAdminDashboardQueryService
{
    // One entry per UTC calendar day of the window, gap-free and ascending. nowUtc comes from the
    // caller so the window is testable; it must have DateTimeKind.Utc.
    Task<AdminDashboardActivity> GetActivityAsync(
        AdminDashboardActivityRange range,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public enum AdminDashboardActivityRange
{
    Days30,
    Days90,
    Months13,
}

// Wire names and window arithmetic for AdminDashboardActivityRange. Wire names are exactly
// "30d", "90d" and "13mo"; parsing is ordinal and exact.
public static class AdminDashboardActivityRanges
{
    public static AdminDashboardActivityRange? Parse(string? value) => value switch
    {
        "30d" => AdminDashboardActivityRange.Days30,
        "90d" => AdminDashboardActivityRange.Days90,
        "13mo" => AdminDashboardActivityRange.Months13,
        _ => null,
    };

    public static string ToWire(AdminDashboardActivityRange range) => range switch
    {
        AdminDashboardActivityRange.Days30 => "30d",
        AdminDashboardActivityRange.Days90 => "90d",
        AdminDashboardActivityRange.Months13 => "13mo",
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
    };

    // ToDate is the running (partial) UTC day. 30d/90d cover N calendar days including today; 13mo
    // starts on the first day of the month 13 months before ToDate's month.
    public static (DateOnly FromDate, DateOnly ToDate) GetWindow(AdminDashboardActivityRange range, DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("nowUtc must have DateTimeKind.Utc.", nameof(nowUtc));
        }

        var toDate = DateOnly.FromDateTime(nowUtc);
        var fromDate = range switch
        {
            AdminDashboardActivityRange.Days30 => toDate.AddDays(-29),
            AdminDashboardActivityRange.Days90 => toDate.AddDays(-89),
            AdminDashboardActivityRange.Months13 => new DateOnly(toDate.Year, toDate.Month, 1).AddMonths(-13),
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
        };
        return (fromDate, toDate);
    }
}

public sealed record AdminDashboardActivity(
    string Range,
    DateOnly FromDate,
    DateOnly ToDate,
    bool TodayIsPartial,
    DateTime GeneratedAtUtc,
    IReadOnlyList<AdminDashboardActivityDay> Days);

// LiveChannels counts ChannelLiveDays rows with LiveMinutes > 0 on Date; null means no row exists
// for that day at all (no observation), which is not the same as zero channels.
public sealed record AdminDashboardActivityDay(DateOnly Date, int? LiveChannels);
