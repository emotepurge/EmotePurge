using EmotePurge.Infrastructure.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The shelf-life rule in isolation (spec 2026-09-13, section 6, AK 7). Five inputs, four fixed
/// answers and two clamps — and the clamps are the point: both hints come from outside this process,
/// so an unclamped hint would let 7TV (or a breaker fed by it) set our retry rate.
/// </summary>
public class SevenTvLeaderboardTtlPolicyTests
{
    /// <summary>A complete fill — and an empty list is a complete fill, not a failure.</summary>
    [Fact]
    public void Hit_LastsAnHour()
    {
        Assert.Equal(
            TimeSpan.FromHours(1),
            SevenTvLeaderboardTtlPolicy.TimeToLiveFor(SevenTvLeaderboardFillOutcome.Hit()));
    }

    /// <summary>
    /// A rate limit hint below the floor is raised to it: 7TV asking us back in thirty seconds is
    /// not a reason to try again in thirty seconds, given the search bucket it guards.
    /// </summary>
    [Fact]
    public void RateLimited_BelowTheFloor_IsRaisedToSixtySeconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            SevenTvLeaderboardTtlPolicy.TimeToLiveFor(
                SevenTvLeaderboardFillOutcome.RateLimited(TimeSpan.FromSeconds(30))));
    }

    /// <summary>An implausibly long hint is capped, so no sort key freezes for the rest of the run.</summary>
    [Fact]
    public void RateLimited_AboveTheCeiling_IsCappedAtAnHour()
    {
        Assert.Equal(
            TimeSpan.FromHours(1),
            SevenTvLeaderboardTtlPolicy.TimeToLiveFor(
                SevenTvLeaderboardFillOutcome.RateLimited(TimeSpan.FromHours(5))));
    }

    /// <summary>A hint inside the band is honoured unchanged — the clamps are guards, not a rewrite.</summary>
    [Fact]
    public void RateLimited_InsideTheBand_IsHonouredAsGiven()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            SevenTvLeaderboardTtlPolicy.TimeToLiveFor(
                SevenTvLeaderboardFillOutcome.RateLimited(TimeSpan.FromMinutes(10))));
    }

    /// <summary>No hint at all falls back to the floor, not to zero and not to the ceiling.</summary>
    [Fact]
    public void RateLimited_WithoutAHint_FallsBackToSixtySeconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            SevenTvLeaderboardTtlPolicy.TimeToLiveFor(SevenTvLeaderboardFillOutcome.RateLimited(null)));
    }

    /// <summary>5xx, timeout, unparseable body: one fixed minute, no hint involved.</summary>
    [Fact]
    public void Unavailable_LastsSixtySeconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            SevenTvLeaderboardTtlPolicy.TimeToLiveFor(SevenTvLeaderboardFillOutcome.Unavailable()));
    }

    /// <summary>
    /// An open breaker is a derived duration, not a fifth fixed value: the breaker's own remaining
    /// time, clamped so that a breaker about to close does not invite an immediate retry storm and a
    /// long lockout does not outlive the hit shelf-life.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(30, 30)]
    [InlineData(7200, 3600)]
    public void BreakerOpen_UsesRemainingOpenTimeClampedToOneSecondAndOneHour(
        int remainingOpenSeconds, int expectedSeconds)
    {
        var ttl = SevenTvLeaderboardTtlPolicy.TimeToLiveFor(
            SevenTvLeaderboardFillOutcome.BreakerOpen(TimeSpan.FromSeconds(remainingOpenSeconds)));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ttl);
    }

    /// <summary>
    /// Self-inflicted congestion, so the shortest shelf-life of the five: nothing upstream went
    /// wrong, and the window will have moved on in half a minute.
    /// </summary>
    [Fact]
    public void BudgetRefused_LastsThirtySeconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            SevenTvLeaderboardTtlPolicy.TimeToLiveFor(SevenTvLeaderboardFillOutcome.BudgetRefused()));
    }

    /// <summary>
    /// A future outcome must not silently inherit somebody else's shelf-life — the budget would be
    /// the one paying for that drift.
    /// </summary>
    [Fact]
    public void UnknownOutcome_Throws_RatherThanDefaulting()
    {
        var unknown = new SevenTvLeaderboardFillOutcome((SevenTvLeaderboardFillOutcomeKind)99, null);

        Assert.Throws<ArgumentOutOfRangeException>(() => SevenTvLeaderboardTtlPolicy.TimeToLiveFor(unknown));
    }
}
