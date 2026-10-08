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

        Assert.True(budget.TryCharge(out _));
        Assert.True(budget.TryCharge(out _));
        Assert.True(budget.TryCharge(out _));
    }

    [Fact]
    public void BeyondTheLimit_IsRefused()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new ContactSendBudget(3, Window, clock);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(budget.TryCharge(out _));
        }

        Assert.False(budget.TryCharge(out _));
    }

    [Fact]
    public void TheWindowSlides_FreeingOneSlotPerAgedOutPermit()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new ContactSendBudget(3, Window, clock);
        Assert.True(budget.TryCharge(out _)); // the permit whose expiry frees the next slot
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(budget.TryCharge(out _));
        Assert.True(budget.TryCharge(out _));
        Assert.False(budget.TryCharge(out _));

        clock.Advance(Window - TimeSpan.FromMinutes(10));
        // The first permit has now aged out — exactly one slot opens, not the whole window.
        Assert.True(budget.TryCharge(out _));
        Assert.False(budget.TryCharge(out _));
    }

    [Fact]
    public void ARefusalConsumesNothing()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new ContactSendBudget(3, Window, clock);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(budget.TryCharge(out _));
        }

        // Five refusals must not push the eventual legitimate request further out.
        for (var i = 0; i < 5; i++)
        {
            Assert.False(budget.TryCharge(out _));
        }

        clock.Advance(Window);
        Assert.True(budget.TryCharge(out _));
        Assert.True(budget.TryCharge(out _));
        Assert.True(budget.TryCharge(out _));
        Assert.False(budget.TryCharge(out _));
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
        Assert.True(budget.TryCharge(out var reservation));
        Assert.False(budget.TryCharge(out _));

        budget.Release(reservation);

        Assert.True(budget.TryCharge(out _));
    }

    [Fact]
    public void Release_WithTheDefaultReservation_IsANoOp()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new ContactSendBudget(1, Window, clock);

        budget.Release(default);

        Assert.True(budget.TryCharge(out _));
        Assert.False(budget.TryCharge(out _));
    }

    /// <summary>
    /// Revised 2026-09-24 (Codex P2): <see cref="ContactSendBudget.Release"/> used to drop whichever
    /// entry was newest, on the assumption that a charge and its own release always run back-to-back
    /// with nothing else interleaved. Two sends overlapping in flight break that assumption — charge A,
    /// charge B, then A's send fails — and the old "drop newest" behaviour refunded B's still-good
    /// reservation while leaving A's, the one that actually failed, occupying a slot. This pins the
    /// correct contract: <c>Release</c> always frees the specific reservation it was given, regardless
    /// of what else was charged in between.
    /// </summary>
    [Fact]
    public void Release_FreesExactlyItsOwnReservation_EvenWhenAnotherChargeHappenedInBetween()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new ContactSendBudget(2, Window, clock);
        Assert.True(budget.TryCharge(out var reservationA)); // permit A, granted at t=0
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(budget.TryCharge(out _)); // permit B, granted at t=10m — A's own interleaved caller

        budget.Release(reservationA); // must drop A specifically, leaving B occupying its slot

        // At t=65m, A (granted at t=0, and released) is long gone either way; what distinguishes a
        // correct release from the old "drop newest" bug is whether B — granted at t=10m — is still
        // occupying a slot. A correct release left B in place, so only one more charge fits before the
        // budget refuses again. A release that (wrongly) dropped B instead would leave the budget with
        // two free slots here.
        clock.Advance(TimeSpan.FromMinutes(55));
        Assert.True(budget.TryCharge(out _));
        Assert.False(budget.TryCharge(out _));
    }

    /// <summary>
    /// A releases twice (a defensive scenario the class contract says must not happen, but must not
    /// corrupt an unrelated entry either) does not free a second slot, nor does it touch whatever
    /// happens to occupy the reservation's old list position afterwards.
    /// </summary>
    [Fact]
    public void Release_CalledTwiceForTheSameReservation_FreesOnlyOneSlot()
    {
        var clock = new HandWoundTimeProvider();
        var budget = new ContactSendBudget(2, Window, clock);
        Assert.True(budget.TryCharge(out var reservation));

        budget.Release(reservation);
        Assert.True(budget.TryCharge(out var replacement)); // occupies the now-free slot

        budget.Release(reservation); // stale — must not also evict `replacement`

        Assert.True(budget.TryCharge(out _));
        Assert.False(budget.TryCharge(out _));
    }
}
