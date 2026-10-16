using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class LogCaptureBudgetTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public void Refund_SameDay_GivesOneAdmissionBack()
    {
        var budget = new LogCaptureBudget(1, new FakeTimeProvider(Noon));
        Assert.True(budget.TryAdmit(LogEntryLevel.Warning, out var day));
        Assert.False(budget.TryAdmit(LogEntryLevel.Warning));

        budget.Refund(day);

        Assert.True(budget.TryAdmit(LogEntryLevel.Warning));
        Assert.False(budget.TryAdmit(LogEntryLevel.Warning));
    }

    [Fact]
    public void Refund_ForAnotherDay_TouchesNothing()
    {
        var time = new FakeTimeProvider(Noon);
        var budget = new LogCaptureBudget(1, time);
        Assert.True(budget.TryAdmit(LogEntryLevel.Warning, out var yesterday));
        time.Advance(TimeSpan.FromDays(1));
        Assert.True(budget.TryAdmit(LogEntryLevel.Warning, out var today));
        Assert.Equal(yesterday + 1, today);

        budget.Refund(yesterday);

        Assert.False(budget.TryAdmit(LogEntryLevel.Warning));
    }

    [Fact]
    public void Refund_NothingUsed_DoesNotGoBelowZero()
    {
        var budget = new LogCaptureBudget(1, new FakeTimeProvider(Noon));
        Assert.True(budget.TryAdmit(LogEntryLevel.Warning, out var day));
        budget.Refund(day);

        budget.Refund(day);

        Assert.True(budget.TryAdmit(LogEntryLevel.Warning));
        Assert.False(budget.TryAdmit(LogEntryLevel.Warning));
    }

    [Fact]
    public void StaleClockRead_NeverRollsTheDayBack()
    {
        var fake = new FakeTimeProvider(Noon);
        var clock = new InterleavingTimeProvider(fake);
        var budget = new LogCaptureBudget(2, clock);

        // The outer call reads day D; before it touches the state, another admission moves it to D+1.
        clock.BeforeNextRead = () =>
        {
            fake.Advance(TimeSpan.FromDays(1));
            Assert.True(budget.TryAdmit(LogEntryLevel.Warning));
        };
        Assert.True(budget.TryAdmit(LogEntryLevel.Warning, out var charged));

        Assert.Equal(fake.GetUtcNow().UtcDateTime.Ticks / TimeSpan.TicksPerDay, charged);
        // Both admissions count against D+1; writing (D, 1) back would have reopened D+1.
        Assert.False(budget.TryAdmit(LogEntryLevel.Warning));
    }

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

    /// <summary>Runs a hook after reading the clock and before returning, to interleave a second admission.</summary>
    private sealed class InterleavingTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        public Action? BeforeNextRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            var now = inner.GetUtcNow();
            var hook = BeforeNextRead;
            BeforeNextRead = null;
            hook?.Invoke();
            return now;
        }
    }
}
