using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Pure logic, no containers: the streak that decides when a 7TV "0 emotes" answer is believed.
public class EmptySetConfirmationTrackerTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

    private readonly HandWoundTimeProvider _clock = new();

    private EmptySetConfirmationTracker Create(int confirmations = 3, int spacingSeconds = 45, int resyncSeconds = 60) =>
        new(new EmptySetConfirmationOptions
        {
            EmptySetConfirmations = confirmations,
            EmptySetConfirmationSpacingSeconds = spacingSeconds,
            ResyncIntervalSeconds = resyncSeconds
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
    public void ZeroOlderThanTenSpacings_RestartsTheStreakInsteadOfConfirming()
    {
        var tracker = Create();

        tracker.ObserveZero("c1", "setA");
        _clock.Advance(Tick);
        tracker.ObserveZero("c1", "setA");
        _clock.Advance(TimeSpan.FromSeconds(451));
        var third = tracker.ObserveZero("c1", "setA");

        Assert.True(third.Counted);
        Assert.False(third.Accept);
        Assert.Equal(1, third.Streak);
    }

    [Fact]
    public void ZeroSpacing_SkipsTheMaxAgeBound()
    {
        var tracker = Create(spacingSeconds: 0);

        tracker.ObserveZero("c1", "setA");
        tracker.ObserveZero("c1", "setA");
        _clock.Advance(TimeSpan.FromDays(1));
        var third = tracker.ObserveZero("c1", "setA");

        Assert.True(third.Accept);
    }

    [Theory]
    [InlineData(45, 600)]
    [InlineData(5, 60)]
    public void ZerosOnTheResyncCadence_AreAcceptedOnTheThird_EvenWhenSlowerThanTenSpacings(int spacingSeconds, int resyncSeconds)
    {
        var tracker = Create(spacingSeconds: spacingSeconds, resyncSeconds: resyncSeconds);
        var tick = TimeSpan.FromSeconds(resyncSeconds);

        tracker.ObserveZero("c1", "setA");
        _clock.Advance(tick);
        tracker.ObserveZero("c1", "setA");
        _clock.Advance(tick);
        var third = tracker.ObserveZero("c1", "setA");

        Assert.True(third.Accept);
        Assert.Equal(3, third.Streak);
    }

    [Fact]
    public void GapBeyondThreeResyncTicks_RestartsTheStreak()
    {
        var tracker = Create(spacingSeconds: 45, resyncSeconds: 600);

        tracker.ObserveZero("c1", "setA");
        _clock.Advance(TimeSpan.FromSeconds(600));
        tracker.ObserveZero("c1", "setA");
        _clock.Advance(TimeSpan.FromSeconds(1801));
        var third = tracker.ObserveZero("c1", "setA");

        Assert.False(third.Accept);
        Assert.Equal(1, third.Streak);
    }
}
