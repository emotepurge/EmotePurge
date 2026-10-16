using Xunit;
using EmotePurge.Infrastructure.Logging.Query;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class LogHistogramBucketsTests
{
    private static DateTime Utc(int day, int hour, int minute, int second = 0, int ms = 0) =>
        new(2026, 10, day, hour, minute, second, ms, DateTimeKind.Utc);

    private static void Expect(DateTime from, DateTime to, TimeSpan width, DateTime bucketFrom, DateTime bucketTo, int count)
    {
        var b = LogHistogramBuckets.Compute(from, to);
        Assert.Equal(width, b.Width);
        Assert.Equal(bucketFrom, b.BucketFromUtc);
        Assert.Equal(bucketTo, b.BucketToUtc);
        Assert.Equal(count, b.Count);
        AssertIdempotent(b);
    }

    private static void AssertIdempotent(LogBucketing b)
    {
        var again = LogHistogramBuckets.Compute(b.BucketFromUtc, b.BucketToUtc);
        Assert.Equal(b, again);
    }

    [Fact]
    public void Preset24h_Unaligned_At120730() =>
        Expect(Utc(13, 12, 7, 30), Utc(14, 12, 7, 30), TimeSpan.FromMinutes(15), Utc(13, 12, 0), Utc(14, 12, 15), 97);

    [Fact]
    public void Preset24h_Aligned_At121500() =>
        Expect(Utc(13, 12, 15), Utc(14, 12, 15), TimeSpan.FromMinutes(15), Utc(13, 12, 15), Utc(14, 12, 15), 96);

    [Fact]
    public void Preset1h_At120001() =>
        Expect(Utc(14, 11, 0, 1), Utc(14, 12, 0, 1), TimeSpan.FromMinutes(1), Utc(14, 11, 0), Utc(14, 12, 1), 61);

    [Fact]
    public void Preset1h_At120000_Aligned() =>
        Expect(Utc(14, 11, 0), Utc(14, 12, 0), TimeSpan.FromMinutes(1), Utc(14, 11, 0), Utc(14, 12, 0), 60);

    [Fact]
    public void Preset6h_At2358() =>
        Expect(Utc(14, 17, 58), Utc(14, 23, 58), TimeSpan.FromMinutes(5), Utc(14, 17, 55), Utc(15, 0, 0), 73);

    [Fact]
    public void Preset7d_OnTuesday1330() =>
        Expect(Utc(6, 13, 30), Utc(13, 13, 30), TimeSpan.FromHours(2), Utc(6, 12, 0), Utc(13, 14, 0), 85);

    [Fact]
    public void Unaligned_100301_To_101701_Is10Seconds_85Buckets() =>
        Expect(Utc(14, 10, 3, 1), Utc(14, 10, 17, 1), TimeSpan.FromSeconds(10), Utc(14, 10, 3, 0), Utc(14, 10, 17, 10), 85);

    [Fact]
    public void Unaligned_EntryAt100301_IsCovered_EntryBeforeFromIsNot()
    {
        var b = LogHistogramBuckets.Compute(Utc(14, 10, 3, 1), Utc(14, 10, 17, 1));
        Assert.True(Utc(14, 10, 3, 1) >= b.BucketFromUtc);
        Assert.Equal(Utc(14, 10, 3, 0), b.BucketFromUtc); // a bucket start, so the entry at 10:03:00.5 lies in the first bucket but is not counted (exact bounds)
        Assert.True(Utc(14, 10, 3, 0, 500) < Utc(14, 10, 3, 1));
    }

    [Fact]
    public void Threshold_50Minutes_Is30Seconds_100Buckets() =>
        Expect(Utc(14, 10, 0), Utc(14, 10, 50), TimeSpan.FromSeconds(30), Utc(14, 10, 0), Utc(14, 10, 50), 100);

    [Fact]
    public void Threshold_50Minutes1Second_Falls_To1Minute_51Buckets() =>
        Expect(Utc(14, 10, 0), Utc(14, 10, 50, 1), TimeSpan.FromMinutes(1), Utc(14, 10, 0), Utc(14, 10, 51), 51);

    [Fact]
    public void OneHourAligned_IsNeverThirtySeconds()
    {
        var b = LogHistogramBuckets.Compute(Utc(14, 10, 0), Utc(14, 11, 0));
        Assert.NotEqual(TimeSpan.FromSeconds(30), b.Width);
        Assert.Equal(TimeSpan.FromMinutes(1), b.Width);
        Assert.Equal(60, b.Count);
    }

    [Fact]
    public void Narrowing_15MinuteBucket_Is10Seconds_90Buckets() =>
        Expect(Utc(14, 12, 0), Utc(14, 12, 15), TimeSpan.FromSeconds(10), Utc(14, 12, 0), Utc(14, 12, 15), 90);

    [Fact]
    public void Narrowing_2HourBucket_Is2Minutes_60Buckets() =>
        Expect(Utc(14, 12, 0), Utc(14, 14, 0), TimeSpan.FromMinutes(2), Utc(14, 12, 0), Utc(14, 14, 0), 60);

    [Fact]
    public void LongestSupportedRange_StillResolves()
    {
        var b = LogHistogramBuckets.Compute(Utc(1, 6, 0), Utc(9, 18, 0));
        Assert.True(b.Count <= LogHistogramBuckets.MaxBuckets);
        AssertIdempotent(b);
    }

    [Fact]
    public void RangeBeyondWidestStep_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LogHistogramBuckets.Compute(Utc(1, 0, 0), Utc(1, 0, 0).AddDays(60)));

    [Fact]
    public void EmptyOrReversedRange_Throws()
    {
        Assert.Throws<ArgumentException>(() => LogHistogramBuckets.Compute(Utc(14, 10, 0), Utc(14, 10, 0)));
        Assert.Throws<ArgumentException>(() => LogHistogramBuckets.Compute(Utc(14, 10, 1), Utc(14, 10, 0)));
    }

    [Fact]
    public void Property_RandomValidRanges()
    {
        var random = new Random(383);
        var origin = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var maxSpan = TimeSpan.FromDays(8).Ticks;
        for (var i = 0; i < 5000; i++)
        {
            var from = origin.AddTicks(random.NextInt64(TimeSpan.FromDays(30).Ticks));
            var span = random.NextInt64(TimeSpan.TicksPerSecond, maxSpan + 1);
            var to = from.AddTicks(span);

            var b = LogHistogramBuckets.Compute(from, to);

            Assert.True(b.BucketFromUtc <= from && b.BucketToUtc >= to, $"not covered {from:o}..{to:o}");
            Assert.True(b.Count <= LogHistogramBuckets.MaxBuckets);
            Assert.Equal(b.Count * b.Width.Ticks, (b.BucketToUtc - b.BucketFromUtc).Ticks);
            Assert.Equal(0, b.BucketFromUtc.Ticks % b.Width.Ticks);
            Assert.Equal(0, b.BucketToUtc.Ticks % b.Width.Ticks);

            // minimal width: the next smaller ladder step would exceed 100 buckets
            var index = LogHistogramBuckets.Ladder.ToList().IndexOf(b.Width);
            Assert.True(index >= 0);
            if (index > 0)
            {
                var smaller = LogHistogramBuckets.Ladder[index - 1].Ticks;
                var smallerCount = ((to.Ticks + smaller - 1) / smaller * smaller - from.Ticks / smaller * smaller) / smaller;
                Assert.True(smallerCount > LogHistogramBuckets.MaxBuckets);
            }

            // every bucket sent back as a click is valid input and snaps to itself
            AssertIdempotent(b);
            var clickFrom = b.BucketFromUtc.AddTicks(b.Width.Ticks * random.Next(b.Count));
            var click = LogHistogramBuckets.Compute(clickFrom, clickFrom + b.Width);
            Assert.True(click.Count <= LogHistogramBuckets.MaxBuckets);
            Assert.Equal(clickFrom, click.BucketFromUtc);
            Assert.Equal(clickFrom + b.Width, click.BucketToUtc);
        }
    }
}
