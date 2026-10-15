namespace EmotePurge.Infrastructure.Logging.Query;

/// <summary>Snapped histogram bounds: <paramref name="Count"/> buckets of <paramref name="Width"/> from <paramref name="BucketFromUtc"/> to <paramref name="BucketToUtc"/>.</summary>
internal sealed record LogBucketing(TimeSpan Width, DateTime BucketFromUtc, DateTime BucketToUtc, int Count);

/// <summary>Bucket width and snapping for the histogram only (spec section 8.2): the smallest ladder step that yields at most 100 buckets.</summary>
internal static class LogHistogramBuckets
{
    public const int MaxBuckets = 100;

    public static readonly IReadOnlyList<TimeSpan> Ladder =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(3),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12),
    ];

    public static LogBucketing Compute(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
        {
            throw new ArgumentException("toUtc must be after fromUtc.", nameof(toUtc));
        }

        foreach (var width in Ladder)
        {
            var count = Snap(fromUtc, toUtc, width, out var start, out var end);
            if (count <= MaxBuckets)
            {
                return new LogBucketing(width, start, end, (int)count);
            }
        }

        // Unreachable for the supported 8 days + 12 h; fall back to the widest step instead of failing.
        var widest = Ladder[^1];
        var widestCount = Snap(fromUtc, toUtc, widest, out var s, out var e);
        return new LogBucketing(widest, s, e, (int)Math.Min(widestCount, int.MaxValue));
    }

    private static long Snap(DateTime from, DateTime to, TimeSpan width, out DateTime start, out DateTime end)
    {
        var w = width.Ticks;
        var startTicks = from.Ticks / w * w;
        var endTicks = (to.Ticks + w - 1) / w * w;
        start = new DateTime(startTicks, DateTimeKind.Utc);
        end = new DateTime(endTicks, DateTimeKind.Utc);
        return (endTicks - startTicks) / w;
    }
}
