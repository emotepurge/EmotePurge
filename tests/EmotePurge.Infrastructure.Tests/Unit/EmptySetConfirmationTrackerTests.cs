using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Pure logic, no containers: the streak that decides when a 7TV "0 emotes" answer is believed.
public class EmptySetConfirmationTrackerTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

    private readonly HandWoundTimeProvider _clock = new();

    private EmptySetConfirmationTracker Create(int confirmations = 3, int spacingSeconds = 45) =>
        new(new EmptySetConfirmationOptions
        {
            EmptySetConfirmations = confirmations,
            EmptySetConfirmationSpacingSeconds = spacingSeconds
        }, _clock);

    [Fact]
    public void SameSet_IsAcceptedOnlyOnTheNthSpacedZero()
    {
        var tracker = Create();

        var first = tracker.ObserveZero("c1", "setA");
        _clock.Advance(Tick);
        var second = tracker.ObserveZero("c1", "setA");
        _clock.Advance(Tick);
        var third = tracker.ObserveZero("c1", "setA");

        Assert.False(first.Accept);
        Assert.False(second.Accept);
        Assert.True(third.Accept);
        Assert.Equal([1, 2, 3], new[] { first.Streak, second.Streak, third.Streak });
        Assert.All(new[] { first, second, third }, v => Assert.Equal(3, v.Required));
    }

    [Fact]
    public void BurstWithinTheSpacing_CountsOnlyOnce()
    {
        var tracker = Create();

        tracker.ObserveZero("c1", "setA");
        _clock.Advance(TimeSpan.FromSeconds(2));
        var second = tracker.ObserveZero("c1", "setA");
        _clock.Advance(TimeSpan.FromSeconds(2));
        var third = tracker.ObserveZero("c1", "setA");

        Assert.False(second.Counted);
        Assert.False(third.Counted);
        Assert.False(third.Accept);
        Assert.Equal(1, third.Streak);
    }

    [Fact]
    public void ThrottledObservation_DoesNotMoveTheSpacingWindow()
    {
        var tracker = Create();

        tracker.ObserveZero("c1", "setA");
        _clock.Advance(TimeSpan.FromSeconds(30));
        tracker.ObserveZero("c1", "setA");
        _clock.Advance(TimeSpan.FromSeconds(30));
        var verdict = tracker.ObserveZero("c1", "setA");

        // 60 s after the counted zero, even though the throttled one sat 30 s ago.
        Assert.True(verdict.Counted);
        Assert.Equal(2, verdict.Streak);
    }

    [Fact]
    public void Reset_RestartsTheStreak()
    {
        var tracker = Create();
        tracker.ObserveZero("c1", "setA");
        _clock.Advance(Tick);
        tracker.ObserveZero("c1", "setA");

        tracker.Reset("c1");
        _clock.Advance(Tick);
        var verdict = tracker.ObserveZero("c1", "setA");

        Assert.Equal(1, verdict.Streak);
        Assert.False(verdict.Accept);
    }

    [Fact]
    public void DifferentSet_RestartsTheStreak()
    {
        var tracker = Create();
        tracker.ObserveZero("c1", "setA");
        _clock.Advance(Tick);
        tracker.ObserveZero("c1", "setA");
        _clock.Advance(Tick);

        var verdict = tracker.ObserveZero("c1", "setB");

        Assert.Equal(1, verdict.Streak);
        Assert.False(verdict.Accept);
    }

    [Fact]
    public void Channels_AreCountedIndependently()
    {
        var tracker = Create();
        tracker.ObserveZero("c1", "setA");
        _clock.Advance(Tick);

        var other = tracker.ObserveZero("c2", "setA");

        Assert.Equal(1, other.Streak);
    }

    [Fact]
    public void OneConfirmation_AcceptsImmediately()
    {
        Assert.True(Create(confirmations: 1).ObserveZero("c1", "setA").Accept);
    }

    [Theory]
    [InlineData(0, 45)]
    [InlineData(3, -1)]
    public void Validate_RejectsUnusableValues(int confirmations, int spacing)
    {
        var options = new EmptySetConfirmationOptions
        {
            EmptySetConfirmations = confirmations,
            EmptySetConfirmationSpacingSeconds = spacing
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_AcceptsTheDefaults()
    {
        new EmptySetConfirmationOptions().Validate();
    }

    [Fact]
    public void Streaks_AreKeyedByTheNormalizedChannelName()
    {
        var tracker = Create();
        tracker.ObserveZero("HandOfBlood", "setA");
        _clock.Advance(Tick);

        var second = tracker.ObserveZero(" handofblood ", "setA");
        tracker.Reset("HANDOFBLOOD");
        _clock.Advance(Tick);
        var afterReset = tracker.ObserveZero("handofblood", "setA");

        Assert.Equal(2, second.Streak);
        Assert.Equal(1, afterReset.Streak);
    }

    // The sync resets on a failed lookup (an observation that is not a zero), so the streak is
    // consecutive observations: the zero after the failure starts again at 1.
    [Fact]
    public void ResetAfterAFailedLookup_MakesTheNextZeroTheFirst()
    {
        var tracker = Create();
        tracker.ObserveZero("c1", "setA");
        _clock.Advance(Tick);
        tracker.ObserveZero("c1", "setA");
        _clock.Advance(Tick);

        tracker.Reset("c1");
        _clock.Advance(Tick);
        var verdict = tracker.ObserveZero("c1", "setA");

        Assert.Equal(1, verdict.Streak);
        Assert.False(verdict.Accept);
    }

    // No maximum age: without a Reset in between, a long gap is only a gap without observations
    // (a stalled resync, a slow cadence), and the zeros on either side of it are still in a row.
    [Fact]
    public void LongGapWithoutReset_StillCountsTowardTheStreak()
    {
        var tracker = Create();
        tracker.ObserveZero("c1", "setA");
        _clock.Advance(TimeSpan.FromHours(1));
        tracker.ObserveZero("c1", "setA");
        _clock.Advance(TimeSpan.FromHours(1));

        var third = tracker.ObserveZero("c1", "setA");

        Assert.True(third.Accept);
        Assert.Equal(3, third.Streak);
    }

    [Fact]
    public void ZeroSpacing_CountsEveryZero()
    {
        var tracker = Create(spacingSeconds: 0);

        tracker.ObserveZero("c1", "setA");
        tracker.ObserveZero("c1", "setA");
        var third = tracker.ObserveZero("c1", "setA");

        Assert.True(third.Accept);
    }
}
