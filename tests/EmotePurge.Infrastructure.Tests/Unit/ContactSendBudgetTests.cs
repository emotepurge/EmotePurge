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

    /// <summary>
    /// <see cref="ContactSendBudget.Release"/> (added 2026-09-24, Codex P2): a caller whose reserved
    /// send then failed at the SMTP step gets its permit back, so the next legitimate attempt — the
    /// visitor retrying, or simply the next caller in the window — is not charged for an e-mail that
    /// was never actually delivered.
    /// </summary>
    [Fact]
    public void Release_FreesOneSlotForTheNextCharge()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new ContactSendBudget(1, Window, clock);
        Assert.True(budget.TryCharge());
        Assert.False(budget.TryCharge());

        budget.Release();

        Assert.True(budget.TryCharge());
    }

    [Fact]
    public void Release_WithNothingGranted_IsANoOp()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new ContactSendBudget(1, Window, clock);

        budget.Release();

        Assert.True(budget.TryCharge());
        Assert.False(budget.TryCharge());
    }

    /// <summary>
    /// Release always drops the most recently granted permit, not the oldest — the pairing this needs
    /// to matter for is one charge immediately followed by its own release (the only sequence
    /// <c>ContactSubmissionService</c> ever produces), not an arbitrary earlier one.
    /// </summary>
    [Fact]
    public void Release_DropsTheMostRecentlyGrantedPermit_NotTheOldest()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new ContactSendBudget(2, Window, clock);
        Assert.True(budget.TryCharge()); // permit A, granted at t=0
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(budget.TryCharge()); // permit B, granted at t=10m

        budget.Release(); // must drop B (the one just granted), leaving only A behind

        // At t=65m, A (granted at t=0) has aged out of the 60-minute window; B (granted at t=10m),
        // had it wrongly been kept instead, would still have 5 minutes left. A correct Release leaves
        // the budget fully empty here, so two charges succeed before a third is refused. A Release
        // that dropped the *oldest* entry instead would have left B still occupying a slot, and only
        // one charge would succeed before the second is refused.
        clock.Advance(TimeSpan.FromMinutes(55));
        Assert.True(budget.TryCharge());
        Assert.True(budget.TryCharge());
        Assert.False(budget.TryCharge());
    }
}
