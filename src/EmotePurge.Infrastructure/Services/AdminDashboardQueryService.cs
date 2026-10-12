using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class AdminDashboardQueryService(AppDbContext db) : IAdminDashboardQueryService
{
    public async Task<AdminDashboardActivity> GetActivityAsync(
        AdminDashboardActivityRange range,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var (fromDate, toDate) = AdminDashboardActivityRanges.GetWindow(range, nowUtc);

        // One grouped scan of the table itself: no join, no navigation (rule 10). The unfiltered
        // COUNT tells "no row that day" (null) apart from "rows, but none live" (0).
        var rows = await db.ChannelLiveDays
            .Where(d => d.Date >= fromDate && d.Date <= toDate)
            .GroupBy(d => d.Date)
            .Select(g => new { Date = g.Key, Live = g.Count(d => d.LiveMinutes > 0) })
            .ToListAsync(cancellationToken);

        var byDate = rows.ToDictionary(r => r.Date, r => r.Live);
        var days = new List<AdminDashboardActivityDay>(toDate.DayNumber - fromDate.DayNumber + 1);
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            days.Add(new AdminDashboardActivityDay(date, byDate.TryGetValue(date, out var live) ? live : null));
        }

        return new AdminDashboardActivity(
            AdminDashboardActivityRanges.ToWire(range),
            fromDate,
            toDate,
            TodayIsPartial: true,
            nowUtc,
            days);
    }
}
