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

    public async Task<AdminDashboardLoad> GetLoadAsync(
        AdminDashboardLoadRange range,
        DateTime nowUtc,
        int samplingIntervalSeconds,
        CancellationToken cancellationToken = default)
    {
        if (samplingIntervalSeconds is <= 0 or > SystemLoadOptions.MaximumIntervalSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(samplingIntervalSeconds),
                samplingIntervalSeconds,
                $"The sampling interval must be between 1 and {SystemLoadOptions.MaximumIntervalSeconds} seconds.");
        }

        var window = AdminDashboardLoadBucketing.GetWindow(range, nowUtc);
        var wire = AdminDashboardLoadRanges.ToWire(range);
        return AdminDashboardLoadRanges.GetSource(range) == AdminDashboardLoadSource.Raw
            ? await GetRawLoadAsync(window, wire, nowUtc, samplingIntervalSeconds, cancellationToken)
            : await GetHourlyLoadAsync(window, wire, nowUtc, samplingIntervalSeconds, cancellationToken);
    }

    // Raw path for any window (internal so a test can read one hour as a single bucket). The samples are
    // streamed in time order into the accumulator; see AdminDashboardRawLoadQuery for why this read,
    // and not SQL-side bucketing, carries the 24-hour 1-second case.
    internal async Task<AdminDashboardLoad> GetRawLoadAsync(
        AdminDashboardLoadWindow window,
        string range,
        DateTime generatedAtUtc,
        int samplingIntervalSeconds,
        CancellationToken cancellationToken)
    {
        var accumulator = new AdminDashboardRawLoadAccumulator(window, samplingIntervalSeconds);
        await AdminDashboardRawLoadQuery.ReadIntoAsync(db, window, samplingIntervalSeconds, accumulator, cancellationToken);
        return accumulator.Complete(range, generatedAtUtc);
    }

    // Hourly path: SystemLoadHourly rows with HourUtc in [from, to) of both sources, streamed in hour
    // order. Rows before fromUtc are filtered, not appended: older history never widens the range.
    private async Task<AdminDashboardLoad> GetHourlyLoadAsync(
        AdminDashboardLoadWindow window,
        string range,
        DateTime generatedAtUtc,
        int samplingIntervalSeconds,
        CancellationToken cancellationToken)
    {
        var fromUtc = window.FromUtc;
        var toUtc = window.ToUtc;
        var accumulator = new AdminDashboardHourlyLoadAccumulator(window, samplingIntervalSeconds);
        var rows = db.SystemLoadHourlies
            .AsNoTracking()
            .Where(hourly => hourly.HourUtc >= fromUtc && hourly.HourUtc < toUtc)
            .OrderBy(hourly => hourly.HourUtc)
            .ThenBy(hourly => hourly.Source)
            .AsAsyncEnumerable();

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            accumulator.Add(row);
        }

        return accumulator.Complete(range, generatedAtUtc);
    }
}
