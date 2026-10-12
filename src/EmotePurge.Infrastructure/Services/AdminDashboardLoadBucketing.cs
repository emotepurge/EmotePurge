using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The fixed bucket ladder of the admin dashboard load range (#381, spec 5.2) with its hard 400-bucket
/// cap, plus the rounding and code helpers shared by the raw path (<see cref="AdminDashboardRawLoadAccumulator"/>)
/// and the hourly path (<see cref="AdminDashboardHourlyLoadAccumulator"/>). No database access here.
/// </summary>
internal static class AdminDashboardLoadBucketing
{
    internal const int MaximumBuckets = 400;

    /// <summary>
    /// toUtc is now; fromUtc is (now - duration) floored to a UTC multiple of the bucket width; the
    /// buckets run from fromUtc up to and including the one that holds now.
    /// </summary>
    public static AdminDashboardLoadWindow GetWindow(AdminDashboardLoadRange range, DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("nowUtc must have DateTimeKind.Utc.", nameof(nowUtc));
        }

        var bucketSeconds = AdminDashboardLoadRanges.GetBucketSeconds(range);
        var start = nowUtc - AdminDashboardLoadRanges.GetDuration(range, nowUtc);
        var bucketTicks = bucketSeconds * TimeSpan.TicksPerSecond;
        var fromUtc = new DateTime(start.Ticks - start.Ticks % bucketTicks, DateTimeKind.Utc);
        return CreateWindow(fromUtc, nowUtc, bucketSeconds);
    }

    /// <summary>
    /// The hard cap: a window of more than <see cref="MaximumBuckets"/> buckets is a programming error
    /// in the ladder, never a user error, and fails loudly instead of silently exceeding the budget.
    /// </summary>
    internal static AdminDashboardLoadWindow CreateWindow(DateTime fromUtc, DateTime toUtc, int bucketSeconds)
    {
        var bucketTicks = bucketSeconds * TimeSpan.TicksPerSecond;
        if (bucketSeconds <= 0 || fromUtc.Ticks % bucketTicks != 0 || toUtc < fromUtc)
        {
            throw new ArgumentException($"Invalid load window {fromUtc:O}..{toUtc:O} at {bucketSeconds} s.");
        }

        var bucketCount = (toUtc.Ticks - fromUtc.Ticks) / bucketTicks + 1;
        if (bucketCount > MaximumBuckets)
        {
            throw new InvalidOperationException(
                $"The load window {fromUtc:O}..{toUtc:O} at {bucketSeconds} s would need {bucketCount} buckets; the budget is {MaximumBuckets}.");
        }

        return new AdminDashboardLoadWindow(
            DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(toUtc, DateTimeKind.Utc),
            bucketSeconds,
            (int)bucketCount);
    }

    internal static double? Round(double? value, int digits) => value is null ? null : Math.Round(value.Value, digits);

    internal static double? Percent(double? share) => share is null ? null : Math.Round(share.Value * 100d, 1);

    internal static string SourceCode(bool isWorker) => isWorker ? "W" : "A";

    internal static string? Provenance(bool api, bool worker) => (api, worker) switch
    {
        (true, true) => "AW",
        (true, false) => "A",
        (false, true) => "W",
        _ => null,
    };
}

/// <summary>The UTC window of one load response and its bucket arithmetic.</summary>
internal readonly record struct AdminDashboardLoadWindow(DateTime FromUtc, DateTime ToUtc, int BucketSeconds, int BucketCount)
{
    public long BucketTicks => BucketSeconds * TimeSpan.TicksPerSecond;

    public DateTime BucketStartUtc(int index) => new(FromUtc.Ticks + index * BucketTicks, DateTimeKind.Utc);

    /// <summary>Bucket index of a timestamp, or -1 when it lies outside [FromUtc, ToUtc].</summary>
    public int IndexOf(DateTime atUtc) =>
        atUtc < FromUtc || atUtc > ToUtc ? -1 : (int)((atUtc.Ticks - FromUtc.Ticks) / BucketTicks);
}
