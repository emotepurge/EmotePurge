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

    // The complete, server-bucketed load range (spec 5.1/5.2). 1h/24h read SystemLoadSamples, every
    // longer range reads SystemLoadHourly only. samplingIntervalSeconds is the configured
    // SystemLoad:IntervalSeconds, the same number the rollup worker passes to its pair gap rule.
    // nowUtc must have DateTimeKind.Utc.
    Task<AdminDashboardLoad> GetLoadAsync(
        AdminDashboardLoadRange range,
        DateTime nowUtc,
        int samplingIntervalSeconds,
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

public enum AdminDashboardLoadRange
{
    Hour1,
    Hours24,
    Days7,
    Days30,
    Days90,
    Months13,
}

public enum AdminDashboardLoadSource
{
    Raw,
    Hourly,
}

// Wire names and the fixed bucket ladder for AdminDashboardLoadRange (spec 5.2). Wire names are
// exactly "1h", "24h", "7d", "30d", "90d" and "13mo"; parsing is ordinal and exact. The bucket width
// is a function of the range alone, never of the sampling cadence.
public static class AdminDashboardLoadRanges
{
    public static AdminDashboardLoadRange? Parse(string? value) => value switch
    {
        "1h" => AdminDashboardLoadRange.Hour1,
        "24h" => AdminDashboardLoadRange.Hours24,
        "7d" => AdminDashboardLoadRange.Days7,
        "30d" => AdminDashboardLoadRange.Days30,
        "90d" => AdminDashboardLoadRange.Days90,
        "13mo" => AdminDashboardLoadRange.Months13,
        _ => null,
    };

    public static string ToWire(AdminDashboardLoadRange range) => range switch
    {
        AdminDashboardLoadRange.Hour1 => "1h",
        AdminDashboardLoadRange.Hours24 => "24h",
        AdminDashboardLoadRange.Days7 => "7d",
        AdminDashboardLoadRange.Days30 => "30d",
        AdminDashboardLoadRange.Days90 => "90d",
        AdminDashboardLoadRange.Months13 => "13mo",
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
    };

    public static int GetBucketSeconds(AdminDashboardLoadRange range) => range switch
    {
        AdminDashboardLoadRange.Hour1 => 60,
        AdminDashboardLoadRange.Hours24 => 300,
        AdminDashboardLoadRange.Days7 => 3_600,
        AdminDashboardLoadRange.Days30 => 7_200,
        AdminDashboardLoadRange.Days90 => 21_600,
        AdminDashboardLoadRange.Months13 => 86_400,
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
    };

    // 7d reads hourly rows although raw samples are kept for seven days: at 1 s cadence the
    // seven-day raw scan fails its budget (spec section 7, B1).
    public static AdminDashboardLoadSource GetSource(AdminDashboardLoadRange range) => range switch
    {
        AdminDashboardLoadRange.Hour1 or AdminDashboardLoadRange.Hours24 => AdminDashboardLoadSource.Raw,
        AdminDashboardLoadRange.Days7 or AdminDashboardLoadRange.Days30
            or AdminDashboardLoadRange.Days90 or AdminDashboardLoadRange.Months13 => AdminDashboardLoadSource.Hourly,
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
    };

    // 13mo is a rolling window of 13 calendar months (DateTime.AddMonths clamps the day at the end
    // of a month); the first-of-month start applies to the activity series only.
    public static TimeSpan GetDuration(AdminDashboardLoadRange range, DateTime nowUtc) => range switch
    {
        AdminDashboardLoadRange.Hour1 => TimeSpan.FromHours(1),
        AdminDashboardLoadRange.Hours24 => TimeSpan.FromHours(24),
        AdminDashboardLoadRange.Days7 => TimeSpan.FromDays(7),
        AdminDashboardLoadRange.Days30 => TimeSpan.FromDays(30),
        AdminDashboardLoadRange.Days90 => TimeSpan.FromDays(90),
        AdminDashboardLoadRange.Months13 => nowUtc - nowUtc.AddMonths(-13),
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
    };
}

// One complete load range, column-oriented: every list has exactly BucketStartsUtc.Count entries and
// null means unknown (no valid observation), never zero. Leading buckets before the first observation
// are left out; inner and trailing gaps stay as null. FromUtc/ToUtc are the requested window, so
// BucketStartsUtc[0] can be later than FromUtc but never earlier. Available is false when the window
// holds no observation at all; the lists are then empty and DataThroughUtc is null.
public sealed record AdminDashboardLoad(
    bool Available,
    string Range,
    string SourceKind,
    int BucketSeconds,
    DateTime FromUtc,
    DateTime ToUtc,
    DateTime GeneratedAtUtc,
    DateTime? DataThroughUtc,
    int SamplingIntervalSeconds,
    IReadOnlyList<DateTime> BucketStartsUtc,
    AdminDashboardHostSeries Host,
    AdminDashboardProcessSeries Api,
    AdminDashboardProcessSeries Worker,
    AdminDashboardWorkloadSeries Workload,
    AdminDashboardSwap? Swap);

// Host metrics. Raw ranges take P95/Max over the union of both sources' valid observations per
// bucket; hourly ranges show the highest stored hourly P95 and the highest stored hourly max over all
// (source, hour) rows in the bucket. CPU busy is percent (0-100, one decimal), memory is bytes, load
// average is tasks (two decimals). Provenance is "A", "W", "AW" or null (sources that contributed
// to the bucket); the *ValueSource lists name the source ("A" or "W") whose value is the shown P95,
// ties going to the higher max and then to "W".
public sealed record AdminDashboardHostSeries(
    IReadOnlyList<double?> CpuBusyP95,
    IReadOnlyList<double?> CpuBusyMax,
    IReadOnlyList<long?> MemoryUsedP95Bytes,
    IReadOnlyList<long?> MemoryUsedMaxBytes,
    IReadOnlyList<long?> MemoryTotalBytes,
    IReadOnlyList<double?> LoadAverage1P95,
    IReadOnlyList<double?> LoadAverage1Max,
    IReadOnlyList<string?> Provenance,
    IReadOnlyList<string?> CpuValueSource,
    IReadOnlyList<string?> MemoryValueSource,
    IReadOnlyList<string?> LoadAverageValueSource,
    IReadOnlyList<int> ApiSampleCount,
    IReadOnlyList<int> WorkerSampleCount,
    IReadOnlyList<double?> ApiCpuBusyMean,
    IReadOnlyList<double?> WorkerCpuBusyMean,
    IReadOnlyList<long?> ApiMemoryUsedMeanBytes,
    IReadOnlyList<long?> WorkerMemoryUsedMeanBytes,
    IReadOnlyList<double?> ApiLoadAverage1Mean,
    IReadOnlyList<double?> WorkerLoadAverage1Mean);

// One process (Api or Worker). CPU in cores (four decimals), shares in percent (one decimal), memory
// in bytes. Quota and memory shares exist for raw ranges only and only against a finite limit; in
// hourly ranges they are always null and the limits are the last ones stored for the bucket.
public sealed record AdminDashboardProcessSeries(
    IReadOnlyList<double?> ProcessCpuP95,
    IReadOnlyList<double?> ProcessCpuMax,
    IReadOnlyList<double?> CgroupCpuP95,
    IReadOnlyList<double?> CgroupCpuMax,
    IReadOnlyList<double?> CgroupCpuQuotaCores,
    IReadOnlyList<double?> CgroupCpuQuotaShareP95,
    IReadOnlyList<double?> CgroupCpuQuotaShareMax,
    IReadOnlyList<long?> CgroupMemoryP95Bytes,
    IReadOnlyList<long?> CgroupMemoryMaxBytes,
    IReadOnlyList<long?> CgroupMemoryLimitBytes,
    IReadOnlyList<double?> CgroupMemoryShareP95,
    IReadOnlyList<double?> CgroupMemoryShareMax,
    IReadOnlyList<int> SampleCount);

// Worker-only workload. WorkloadUnit is "perMinute" for raw ranges (interval counters divided by
// their actual interval seconds, times 60) and "perBucket" for hourly ranges (the sum of the stored
// interval counters of every hour in the bucket, never divided and never extrapolated).
// WorkloadIntervalSeconds is raw-only, WorkloadHoursWithData hourly-only; the other one is all null.
public sealed record AdminDashboardWorkloadSeries(
    string WorkloadUnit,
    IReadOnlyList<int?> ActiveChannelsMax,
    IReadOnlyList<int?> LiveChannelsMax,
    IReadOnlyList<double?> ChatLines,
    IReadOnlyList<double?> EmoteMatches,
    IReadOnlyList<double?> WorkloadIntervalSeconds,
    IReadOnlyList<int?> WorkloadHoursWithData);

// Last known host swap in the raw window, only when the host reports swap (SwapTotal > 0).
public sealed record AdminDashboardSwap(long LastSwapUsedBytes, long LastSwapTotalBytes, DateTime ObservedAtUtc);
