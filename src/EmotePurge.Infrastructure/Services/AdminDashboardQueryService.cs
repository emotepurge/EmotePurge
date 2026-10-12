using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class AdminDashboardQueryService(AppDbContext db) : IAdminDashboardQueryService
{
    // The chart columns of one sample, never the whole row. The Source text becomes a bool so the
    // stream does not allocate a string per row.
    private const string RawLoadColumns = """
        sample."Id",
        sample."SampledAtUtc",
        sample."Source" = 'Worker' AS "IsWorker",
        sample."ProcessInstanceId",
        sample."PreviousSampledAtUtc",
        sample."ProcessCpuTimeTicks",
        sample."CgroupCpuUsageMicroseconds",
        sample."CgroupCpuQuotaMicroseconds",
        sample."CgroupCpuPeriodMicroseconds",
        sample."CgroupMemoryCurrentBytes",
        sample."CgroupMemoryMaxBytes",
        sample."HostCpuBusyJiffies",
        sample."HostCpuTotalJiffies",
        sample."HostLoadAverage1",
        sample."HostMemoryTotalBytes",
        sample."HostMemoryAvailableBytes",
        sample."HostSwapTotalBytes",
        sample."HostSwapFreeBytes",
        sample."ActiveChannels",
        sample."LiveChannels",
        sample."ChatLines",
        sample."EmoteMatches"
        """;

    // {0} = fromUtc, {1} = toUtc (both inclusive). The window's samples of both sources plus one
    // predecessor per (Source, ProcessInstanceId) group that occurs in the window: a backward LIMIT 1
    // scan of IX_SystemLoadSamples_Source_ProcessInstanceId_SampledAtUtc, the pattern of
    // SystemLoadRollupService.LoadCandidateSamplesAsync. An instance that starts inside the window has
    // no predecessor. Ordered by time so the accumulator can finish one bucket after the other.
    private const string RawLoadSql = """
        WITH range_groups AS (
            SELECT sample."Source", sample."ProcessInstanceId", MIN(sample."SampledAtUtc") AS "FirstSampledAtUtc"
            FROM "SystemLoadSamples" AS sample
            WHERE sample."Source" IN ('Api', 'Worker')
              AND sample."SampledAtUtc" >= {0}
              AND sample."SampledAtUtc" <= {1}
            GROUP BY sample."Source", sample."ProcessInstanceId"
        )
        SELECT
        """ + "\n" + RawLoadColumns + "\n" + """
        FROM "SystemLoadSamples" AS sample
        WHERE sample."Source" IN ('Api', 'Worker')
          AND sample."SampledAtUtc" >= {0}
          AND sample."SampledAtUtc" <= {1}
        UNION ALL
        SELECT predecessor.*
        FROM range_groups AS grp
        JOIN LATERAL (
            SELECT
        """ + "\n" + RawLoadColumns + "\n" + """
            FROM "SystemLoadSamples" AS sample
            WHERE sample."Source" = grp."Source"
              AND sample."ProcessInstanceId" = grp."ProcessInstanceId"
              AND sample."SampledAtUtc" < grp."FirstSampledAtUtc"
            ORDER BY sample."SampledAtUtc" DESC, sample."Id" DESC
            LIMIT 1
        ) AS predecessor ON TRUE
        ORDER BY "SampledAtUtc", "Id"
        """;

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

    // Raw path for any window (internal so a test can read one hour as a single bucket). The window's
    // samples of both sources plus one predecessor per (Source, ProcessInstanceId) group, found with a
    // backward LIMIT 1 index scan like SystemLoadRollupService.LoadCandidateSamplesAsync. Only the chart
    // columns are projected, and the rows are streamed straight into the accumulator in time order, so
    // no unbucketed list of samples is ever held.
    internal async Task<AdminDashboardLoad> GetRawLoadAsync(
        AdminDashboardLoadWindow window,
        string range,
        DateTime generatedAtUtc,
        int samplingIntervalSeconds,
        CancellationToken cancellationToken)
    {
        var accumulator = new AdminDashboardRawLoadAccumulator(window, samplingIntervalSeconds);
        var rows = db.Database.SqlQueryRaw<AdminDashboardLoadSampleRow>(RawLoadSql, window.FromUtc, window.ToUtc);

        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            accumulator.Add(row);
        }

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
