using Xunit;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Logging.Query;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class LogTimeRangeTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 1, 0, DateTimeKind.Utc);

    [Fact]
    public void NoInput_DefaultsTo24h()
    {
        var range = LogTimeRange.Resolve(new LogRangeRequest(null, null, null), Now);
        Assert.Equal(new LogExactRange(Now.AddHours(-24), Now, true), range);
    }

    [Theory]
    [InlineData("1h", 1)]
    [InlineData("6h", 6)]
    [InlineData("24h", 24)]
    [InlineData("7d", 168)]
    public void Presets_ResolveToExactBounds(string preset, int hours)
    {
        var range = LogTimeRange.Resolve(new LogRangeRequest(preset, null, null), Now);
        Assert.Equal(Now, range.ToUtc);
        Assert.Equal(Now.AddHours(-hours), range.FromUtc);
        Assert.True(range.IsRelative);
    }

    [Fact]
    public void SevenDays_At1201_KeepsTheExactBounds_NotSnapped()
    {
        var range = LogTimeRange.Resolve(new LogRangeRequest("7d", null, null), Now);
        Assert.Equal(new DateTime(2026, 10, 7, 12, 1, 0, DateTimeKind.Utc), range.FromUtc);
        Assert.Equal(Now, range.ToUtc);
    }

    [Fact]
    public void ReturnedBounds_AreAcceptedWhenSentBack()
    {
        var first = LogTimeRange.Resolve(new LogRangeRequest("7d", null, null), Now);
        var later = Now.AddMinutes(3);
        var second = LogTimeRange.Resolve(new LogRangeRequest(null, first.FromUtc, first.ToUtc), later);
        Assert.Equal(first.FromUtc, second.FromUtc);
        Assert.Equal(first.ToUtc, second.ToUtc);
        Assert.False(second.IsRelative);
    }

    [Fact]
    public void RunningBucketClick_At1201With7d_IsAccepted()
    {
        var bucket = LogHistogramBuckets.Compute(Now.AddDays(-7), Now);
        Assert.Equal(new DateTime(2026, 10, 14, 14, 0, 0, DateTimeKind.Utc), bucket.BucketToUtc);
        var from = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        var range = LogTimeRange.Resolve(new LogRangeRequest(null, from, bucket.BucketToUtc), Now);
        Assert.Equal((from, bucket.BucketToUtc), (range.FromUtc, range.ToUtc));
    }

    [Theory]
    [InlineData("2d")]
    [InlineData("")]
    [InlineData("24H")]
    [InlineData("1h ")]
    public void UnknownPreset_Throws(string preset) =>
        Assert.Throws<LogRangeException>(() => LogTimeRange.Resolve(new LogRangeRequest(preset, null, null), Now));

    [Fact]
    public void Mixed_RangeWithFromOrTo_Throws()
    {
        Assert.Throws<LogRangeException>(() => LogTimeRange.Resolve(new LogRangeRequest("1h", Now.AddHours(-2), Now), Now));
        Assert.Throws<LogRangeException>(() => LogTimeRange.Resolve(new LogRangeRequest("1h", Now.AddHours(-2), null), Now));
        Assert.Throws<LogRangeException>(() => LogTimeRange.Resolve(new LogRangeRequest("1h", null, Now), Now));
    }

    [Fact]
    public void OnlyFromOrOnlyTo_Throws()
    {
        Assert.Throws<LogRangeException>(() => LogTimeRange.Resolve(new LogRangeRequest(null, Now.AddHours(-2), null), Now));
        Assert.Throws<LogRangeException>(() => LogTimeRange.Resolve(new LogRangeRequest(null, null, Now), Now));
    }

    [Fact]
    public void FromNotBeforeTo_Throws()
    {
        Assert.Throws<LogRangeException>(() => LogTimeRange.Resolve(new LogRangeRequest(null, Now, Now), Now));
        Assert.Throws<LogRangeException>(() => LogTimeRange.Resolve(new LogRangeRequest(null, Now, Now.AddSeconds(-1)), Now));
    }

    [Fact]
    public void EightDays_AreAccepted_EightDaysPlusOneSecondIsRejected()
    {
        var to = Now.AddHours(-1);
        var ok = LogTimeRange.Resolve(new LogRangeRequest(null, to.AddDays(-8), to), Now);
        Assert.Equal(TimeSpan.FromDays(8), ok.ToUtc - ok.FromUtc);
        Assert.Throws<LogRangeException>(() => LogTimeRange.Resolve(new LogRangeRequest(null, to.AddDays(-8).AddSeconds(-1), to), Now));
    }

    [Fact]
    public void ToTwelveHoursAhead_IsAccepted_PlusOneSecondIsRejected()
    {
        var to = Now.AddHours(12);
        Assert.Equal(to, LogTimeRange.Resolve(new LogRangeRequest(null, to.AddHours(-2), to), Now).ToUtc);
        Assert.Throws<LogRangeException>(() =>
            LogTimeRange.Resolve(new LogRangeRequest(null, to.AddHours(-2), to.AddSeconds(1)), Now));
    }

    [Fact]
    public void AbsoluteRange_NormalizesKindToUtc()
    {
        var from = DateTime.SpecifyKind(Now.AddHours(-2), DateTimeKind.Unspecified);
        var range = LogTimeRange.Resolve(new LogRangeRequest(null, from, Now), Now);
        Assert.Equal(DateTimeKind.Utc, range.FromUtc.Kind);
        Assert.Equal(Now.AddHours(-2), range.FromUtc);
    }

    // ---- keysOnly (L2) ----

    [Fact]
    public void Keys_WithoutTo_EndsAtNow()
    {
        var range = LogTimeRange.ResolveKeys(Now.AddHours(-3), null, Now);
        Assert.Equal(Now, range.ToUtc);
        Assert.Equal(Now.AddHours(-3), range.FromUtc);
    }

    [Fact]
    public void Keys_WithTo_UsesIt() => Assert.Equal(Now.AddHours(-1), LogTimeRange.ResolveKeys(Now.AddHours(-3), Now.AddHours(-1), Now).ToUtc);

    [Fact]
    public void Keys_StillEnforceSpanFutureAndOrder()
    {
        Assert.Throws<LogRangeException>(() => LogTimeRange.ResolveKeys(Now.AddDays(-8).AddSeconds(-1), null, Now));
        Assert.Equal(Now, LogTimeRange.ResolveKeys(Now.AddDays(-8), null, Now).ToUtc);
        Assert.Throws<LogRangeException>(() => LogTimeRange.ResolveKeys(Now.AddHours(-1), Now.AddHours(12).AddSeconds(1), Now));
        Assert.Throws<LogRangeException>(() => LogTimeRange.ResolveKeys(Now.AddSeconds(1), null, Now));
        Assert.Throws<LogRangeException>(() => LogTimeRange.ResolveKeys(Now, Now, Now));
    }
}
