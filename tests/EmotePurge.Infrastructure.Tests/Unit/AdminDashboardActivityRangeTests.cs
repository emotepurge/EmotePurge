using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class AdminDashboardActivityRangeTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("30d", AdminDashboardActivityRange.Days30)]
    [InlineData("90d", AdminDashboardActivityRange.Days90)]
    [InlineData("13mo", AdminDashboardActivityRange.Months13)]
    public void Parse_AcceptsTheThreeWireNames(string value, AdminDashboardActivityRange expected)
    {
        Assert.Equal(expected, AdminDashboardActivityRanges.Parse(value));
        Assert.Equal(value, AdminDashboardActivityRanges.ToWire(expected));
    }

    [Theory]
    [InlineData("30D")]
    [InlineData(" 30d")]
    [InlineData("30d ")]
    [InlineData("7d")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_RejectsEverythingElse(string? value)
    {
        Assert.Null(AdminDashboardActivityRanges.Parse(value));
    }

    [Fact]
    public void Window_30d_IsExactlyThirtyCalendarDaysIncludingToday()
    {
        var (from, to) = AdminDashboardActivityRanges.GetWindow(AdminDashboardActivityRange.Days30, Utc(2026, 10, 11, 23, 59));

        Assert.Equal(new DateOnly(2026, 9, 12), from);
        Assert.Equal(new DateOnly(2026, 10, 11), to);
        Assert.Equal(30, to.DayNumber - from.DayNumber + 1);
    }

    [Fact]
    public void Window_90d_IsExactlyNinetyCalendarDays()
    {
        var (from, to) = AdminDashboardActivityRanges.GetWindow(AdminDashboardActivityRange.Days90, Utc(2026, 10, 11));

        Assert.Equal(90, to.DayNumber - from.DayNumber + 1);
    }

    [Theory]
    [InlineData(2026, 10, 11, 2025, 9, 1)]
    [InlineData(2027, 1, 3, 2025, 12, 1)]
    [InlineData(2026, 3, 31, 2025, 2, 1)]
    public void Window_13mo_StartsOnTheFirstOfTheMonthThirteenMonthsBack(int y, int m, int d, int fy, int fm, int fd)
    {
        var (from, to) = AdminDashboardActivityRanges.GetWindow(AdminDashboardActivityRange.Months13, Utc(y, m, d));

        Assert.Equal(new DateOnly(fy, fm, fd), from);
        Assert.Equal(new DateOnly(y, m, d), to);
    }

    [Fact]
    public void Window_RejectsNonUtcTimes()
    {
        var local = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Local);

        Assert.Throws<ArgumentException>(() => AdminDashboardActivityRanges.GetWindow(AdminDashboardActivityRange.Days30, local));
    }
}
