using EmotePurge.Infrastructure.Contact;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="ContactSendBudget"/> in isolation (docs/DECISIONS.md 2026-09-24, "contact form"): the
/// provider-wide sending ceiling, distinct from the per-IP ASP.NET Core policy the endpoint's own
/// <c>RequireRateLimiting</c> already enforces. Shape mirrors
/// <c>SevenTvLeaderboardRequestBudgetTests</c> — the two classes share the same rolling-window design.
/// </summary>
public class ContactSendBudgetTests
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    [Fact]
    public void UpToTheLimit_EveryChargeSucceeds()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new ContactSendBudget(3, Window, clock);

        Assert.True(budget.TryCharge());
        Assert.True(budget.TryCharge());
        Assert.True(budget.TryCharge());
    }

    [Fact]
    public void BeyondTheLimit_IsRefused()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new ContactSendBudget(3, Window, clock);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(budget.TryCharge());
        }

        Assert.False(budget.TryCharge());
    }

    [Fact]
    public void TheWindowSlides_FreeingOneSlotPerAgedOutPermit()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new ContactSendBudget(3, Window, clock);
        Assert.True(budget.TryCharge()); // the permit whose expiry frees the next slot
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(budget.TryCharge());
        Assert.True(budget.TryCharge());
        Assert.False(budget.TryCharge());

        clock.Advance(Window - TimeSpan.FromMinutes(10));
        // The first permit has now aged out — exactly one slot opens, not the whole window.
        Assert.True(budget.TryCharge());
        Assert.False(budget.TryCharge());
    }

    [Fact]
    public void ARefusalConsumesNothing()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new ContactSendBudget(3, Window, clock);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(budget.TryCharge());
        }

        // Five refusals must not push the eventual legitimate request further out.
        for (var i = 0; i < 5; i++)
        {
            Assert.False(budget.TryCharge());
        }

        clock.Advance(Window);
        Assert.True(budget.TryCharge());
        Assert.True(budget.TryCharge());
        Assert.True(budget.TryCharge());
        Assert.False(budget.TryCharge());
    }

    [Fact]
    public void NonsensicalConfiguration_IsRejectedAtConstruction()
    {
        var clock = new HandWoundTimeProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() => new ContactSendBudget(0, Window, clock));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContactSendBudget(3, TimeSpan.Zero, clock));
    }
}
