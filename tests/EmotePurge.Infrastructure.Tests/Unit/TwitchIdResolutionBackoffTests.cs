using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The per-channel backoff that stops a channel whose Twitch id never resolves from costing a 7TV
/// search every minute (design note <c>docs/Konzept-7TV-Such-Budget-2026-10-03.md</c>, 2.3).
/// </summary>
public class TwitchIdResolutionBackoffTests
{
    private static readonly SevenTvSearchBudgetOptions Options = new();

    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(6, 1920)]
    [InlineData(7, 3600)]
    [InlineData(500, 3600)]
    public void DelayAfter_DoublesFromTheBaseUpToTheCeiling(int misses, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), TwitchIdResolutionBackoff.DelayAfter(misses, Options));
    }

    [Fact]
    public void AStuckChannel_SettlesAtTwentyFourSearchesADay()
    {
        // The number the issue is about: 1440 a day before, one per ceiling interval once the ramp
        // is over. Ten simulated days, ticking like the periodic resync, counting only the last.
        var clock = new HandWoundTimeProvider();
        var backoff = new TwitchIdResolutionBackoff(Options, clock);
        var lastDayStart = clock.Now + TimeSpan.FromDays(9);
        var end = clock.Now + TimeSpan.FromDays(10);
        var searchesOnLastDay = 0;

        for (; clock.Now < end; clock.Advance(TimeSpan.FromMinutes(1)))
        {
            if (backoff.IsDue("c1", out _))
            {
                backoff.RecordMiss("c1");
                if (clock.Now >= lastDayStart)
                {
                    searchesOnLastDay++;
                }
            }
        }

        Assert.Equal(24, searchesOnLastDay);
    }

    [Fact]
    public void AnUnknownChannel_IsDue()
    {
        var backoff = new TwitchIdResolutionBackoff(Options, new HandWoundTimeProvider());

        Assert.True(backoff.IsDue("c1", out var retryIn));
        Assert.Equal(TimeSpan.Zero, retryIn);
    }

    [Fact]
    public void AMiss_HoldsTheChannelBackUntilTheDelayHasPassed()
    {
        var clock = new HandWoundTimeProvider();
        var backoff = new TwitchIdResolutionBackoff(Options, clock);

        var (misses, delay) = backoff.RecordMiss("c1");

        Assert.Equal(1, misses);
        Assert.Equal(TimeSpan.FromSeconds(60), delay);
        Assert.False(backoff.IsDue("c1", out var retryIn));
        Assert.Equal(TimeSpan.FromSeconds(60), retryIn);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(backoff.IsDue("c1", out _));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(backoff.IsDue("c1", out _));
    }

    [Fact]
    public void ASuccess_ForgetsTheChannel()
    {
        var clock = new HandWoundTimeProvider();
        var backoff = new TwitchIdResolutionBackoff(Options, clock);
        backoff.RecordMiss("c1");
        backoff.RecordMiss("c1");

        backoff.RecordSuccess("c1");

        Assert.True(backoff.IsDue("c1", out _));
        Assert.Equal(1, backoff.RecordMiss("c1").ConsecutiveMisses);
    }

    [Fact]
    public void Channels_BackOffIndependently()
    {
        var backoff = new TwitchIdResolutionBackoff(Options, new HandWoundTimeProvider());

        backoff.RecordMiss("c1");

        Assert.False(backoff.IsDue("c1", out _));
        Assert.True(backoff.IsDue("c2", out _));
    }
}
