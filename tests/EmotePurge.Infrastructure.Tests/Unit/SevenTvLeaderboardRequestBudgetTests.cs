using System.Diagnostics;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="SevenTvLeaderboardRequestBudget"/> in isolation (spec 2026-09-13, section 6): the hard
/// lid of ten upstream requests per rolling hour and process run, the number the early-warning alarm
/// reads, and the restart contract from AK 33.
/// </summary>
public class SevenTvLeaderboardRequestBudgetTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(60);

    /// <summary>Ten permits inside one window, and each reports its own position for the alarm.</summary>
    [Fact]
    public void UpToTheLimit_EveryRequestIsGranted()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new SevenTvLeaderboardRequestBudget(10, Window, clock);

        for (var expected = 1; expected <= 10; expected++)
        {
            Assert.True(budget.TryCharge(out var usedInWindow));
            Assert.Equal(expected, usedInWindow);
        }
    }

    /// <summary>
    /// The eleventh is refused, reports the window as full, and — the part that matters for a
    /// waiting browser — returns without waiting for anything.
    /// </summary>
    [Fact]
    public void BeyondTheLimit_IsRefusedImmediately_WithoutWaiting()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new SevenTvLeaderboardRequestBudget(10, Window, clock);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(budget.TryCharge(out _));
        }

        var stopwatch = Stopwatch.StartNew();
        var granted = budget.TryCharge(out var usedInWindow);
        stopwatch.Stop();

        Assert.False(granted);
        Assert.Equal(10, usedInWindow);

        // The clock never moved, so any waiting would have to be real waiting. A generous bound: the
        // claim is "no wait at all", not "fast".
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"a refusal must not wait, but took {stopwatch.Elapsed}");
    }

    /// <summary>
    /// Rolling, not fixed: the window frees exactly one slot when the oldest permit ages out, not ten
    /// at a boundary.
    /// </summary>
    [Fact]
    public void TheWindowSlides_FreeingOneSlotPerAgedOutPermit()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new SevenTvLeaderboardRequestBudget(10, Window, clock);
        Assert.True(budget.TryCharge(out _)); // the permit whose expiry frees the next slot
        clock.Advance(TimeSpan.FromMinutes(5));
        for (var i = 0; i < 9; i++)
        {
            Assert.True(budget.TryCharge(out _));
        }

        Assert.False(budget.TryCharge(out _));

        // One second before the first permit ages out: still full.
        clock.Advance(Window - TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.False(budget.TryCharge(out var stillFull));
        Assert.Equal(10, stillFull);

        // One second later it has aged out — and exactly one slot opened, not the whole window.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(budget.TryCharge(out var usedInWindow));
        Assert.Equal(10, usedInWindow);
        Assert.False(budget.TryCharge(out _));
    }

    /// <summary>
    /// A refusal consumes nothing. Five refused callers must not push the one legitimate request that
    /// the freed slot belongs to out of its place — if a refusal enqueued a timestamp, this test
    /// would find the window full at the end.
    /// </summary>
    [Fact]
    public void ARefusalConsumesNothing()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new SevenTvLeaderboardRequestBudget(10, Window, clock);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(budget.TryCharge(out _));
        }

        clock.Advance(Window - TimeSpan.FromSeconds(1));
        for (var i = 0; i < 5; i++)
        {
            Assert.False(budget.TryCharge(out var usedInWindow));
            Assert.Equal(10, usedInWindow);
        }

        // All ten original permits age out together here, since they were all taken at the same
        // instant — the five refusals in between added nothing to the window.
        clock.Advance(TimeSpan.FromSeconds(1));
        for (var expected = 1; expected <= 10; expected++)
        {
            Assert.True(budget.TryCharge(out var usedInWindow));
            Assert.Equal(expected, usedInWindow);
        }

        Assert.False(budget.TryCharge(out _));
    }

    /// <summary>
    /// What the alarm watches: the count reported with a grant includes that grant, so the sixth
    /// upstream request of a window is the one that reports six — the alarm can fire before the lid
    /// is anywhere near.
    /// </summary>
    [Fact]
    public void UsedInWindow_CountsTheGrantItReports_AndIgnoresAgedOutPermits()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new SevenTvLeaderboardRequestBudget(10, Window, clock);
        for (var i = 0; i < 5; i++)
        {
            Assert.True(budget.TryCharge(out _));
        }

        Assert.True(budget.TryCharge(out var atTheAlarm));
        Assert.Equal(6, atTheAlarm);

        clock.Advance(Window);
        Assert.True(budget.TryCharge(out var afterTheWindowMovedOn));
        Assert.Equal(1, afterTheWindowMovedOn);
    }

    /// <summary>
    /// The restart contract, AK 33. A restarted process gets a fresh, empty budget: the new instance
    /// grants up to ten of its own inside the same rolling hour, so the honest worst case is
    /// <c>10 × process runs</c> — not "ten an hour, whatever happens". The lid is per run, and this
    /// test is where that is written down rather than assumed.
    /// </summary>
    [Fact]
    public void TwoInstancesOnOneClock_EachGrantTen_SoARestartAddsUpTo()
    {
        var clock = new HandWoundTimeProvider();
        var beforeRestart = new SevenTvLeaderboardRequestBudget(10, Window, clock);

        var grantedBefore = 0;
        for (var i = 0; i < 11; i++)
        {
            if (beforeRestart.TryCharge(out _))
            {
                grantedBefore++;
            }
        }

        // Same rolling hour, new process run.
        clock.Advance(TimeSpan.FromMinutes(20));
        var afterRestart = new SevenTvLeaderboardRequestBudget(10, Window, clock);

        var grantedAfter = 0;
        for (var i = 0; i < 11; i++)
        {
            if (afterRestart.TryCharge(out _))
            {
                grantedAfter++;
            }
        }

        Assert.Equal(10, grantedBefore);
        Assert.Equal(10, grantedAfter);
        Assert.Equal(20, grantedBefore + grantedAfter);
    }

    /// <summary>A budget of zero requests or a window of zero length is a configuration bug, not a lid.</summary>
    [Fact]
    public void NonsensicalConfiguration_IsRejectedAtConstruction()
    {
        var clock = new HandWoundTimeProvider();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SevenTvLeaderboardRequestBudget(0, Window, clock));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SevenTvLeaderboardRequestBudget(10, TimeSpan.Zero, clock));
    }
}
