using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class LogCaptureBudgetTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static int Admit(LogCaptureBudget budget, LogEntryLevel level, int attempts)
    {
        var admitted = 0;
        for (var i = 0; i < attempts; i++)
        {
            if (budget.TryAdmit(level))
            {
                admitted++;
            }
        }

        return admitted;
    }

    [Fact]
    public void Budget_InfoStopsAt80Percent_WarningsUntil100_ResetAtUtcMidnight()
    {
        var time = new FakeTimeProvider(Noon);
        var budget = new LogCaptureBudget(20_000, time);

        Assert.Equal(16_000, Admit(budget, LogEntryLevel.Information, 25_000));
        Assert.Equal(4_000, Admit(budget, LogEntryLevel.Warning, 10_000));
        Assert.False(budget.TryAdmit(LogEntryLevel.Error));
        Assert.False(budget.TryAdmit(LogEntryLevel.Critical));

        // One tick before midnight UTC the day is still exhausted.
        time.SetUtcNow(new DateTimeOffset(2026, 10, 15, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999));
        Assert.False(budget.TryAdmit(LogEntryLevel.Warning));

        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(16_000, Admit(budget, LogEntryLevel.Information, 25_000));
        Assert.Equal(4_000, Admit(budget, LogEntryLevel.Error, 10_000));
    }

    [Fact]
    public void Warnings_ShareThePool_InformationStopsOnceUsageReaches80Percent()
    {
        var time = new FakeTimeProvider(Noon);
        var budget = new LogCaptureBudget(10, time);

        Assert.Equal(8, Admit(budget, LogEntryLevel.Warning, 8));
        Assert.False(budget.TryAdmit(LogEntryLevel.Information));
        Assert.Equal(2, Admit(budget, LogEntryLevel.Critical, 5));
    }

    [Fact]
    public void ZeroBudget_AdmitsNothing()
    {
        var budget = new LogCaptureBudget(0, new FakeTimeProvider(Noon));

        Assert.False(budget.TryAdmit(LogEntryLevel.Information));
        Assert.False(budget.TryAdmit(LogEntryLevel.Critical));
    }

    [Fact]
    public void Concurrent_AdmitsExactlyTheLimit()
    {
        var budget = new LogCaptureBudget(20_000, new FakeTimeProvider(Noon));
        var info = 0;
        var errors = 0;

        Parallel.For(0, 60_000, i =>
        {
            var level = i % 3 == 0 ? LogEntryLevel.Error : LogEntryLevel.Information;
            if (budget.TryAdmit(level))
            {
                Interlocked.Increment(ref level == LogEntryLevel.Error ? ref errors : ref info);
            }
        });

        Assert.Equal(20_000, info + errors);
        Assert.True(info <= 16_000);
    }
}
