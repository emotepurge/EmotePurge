using EmotePurge.Infrastructure.SevenTv;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="ForeignEmoteSetProviderBudget"/> in isolation (spec 2026-09-09, E5b) — no Redis, no
/// decorator: just the two limits and, above all, the fact that they are two. The concurrency gate
/// admits whole lookups; the rate window is charged by the individual upstream requests a lookup
/// makes. The cross-user, cross-channel claim ("a third concurrent lookup for a *different* channel
/// still waits") is <c>HardenedForeignEmoteSetServiceTests</c>'s job in <c>Integration/</c>, since
/// that needs the decorator's real Redis cache dependency; this file only pins the budget's own
/// behaviour.
/// </summary>
public class ForeignEmoteSetProviderBudgetTests
{
    [Fact]
    public async Task WithinBothLimits_AcquiresImmediately()
    {
        using var budget = new ForeignEmoteSetProviderBudget(TimeProvider.System);

        var permit = await budget.TryAcquireConcurrencySlotAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.NotNull(permit);
        permit.Dispose();
    }

    /// <summary>
    /// A third caller beyond <see cref="ForeignEmoteSetProviderBudget.MaxConcurrent"/> waits rather
    /// than failing outright — and succeeds the moment an earlier permit is released, well inside the
    /// timeout it was given.
    /// </summary>
    [Fact]
    public async Task BeyondConcurrencyLimit_WaitsForARelease_ThenAcquires()
    {
        using var budget = new ForeignEmoteSetProviderBudget(TimeProvider.System);
        var first = await budget.TryAcquireConcurrencySlotAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var second = await budget.TryAcquireConcurrencySlotAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);

        var thirdTask = budget.TryAcquireConcurrencySlotAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(50); // give the third caller a real chance to (wrongly) return early
        Assert.False(thirdTask.IsCompleted);

        first!.Dispose();
        var third = await thirdTask;

        Assert.NotNull(third);
        third!.Dispose();
        second!.Dispose();
    }

    /// <summary>A caller that is still waiting when its timeout elapses gets <c>null</c>, not an exception.</summary>
    [Fact]
    public async Task BeyondConcurrencyLimit_TimesOut_ReturnsNull_NotAnException()
    {
        using var budget = new ForeignEmoteSetProviderBudget(TimeProvider.System);
        var first = await budget.TryAcquireConcurrencySlotAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var second = await budget.TryAcquireConcurrencySlotAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);

        var third = await budget.TryAcquireConcurrencySlotAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Null(third);
        first!.Dispose();
        second!.Dispose();
    }

    /// <summary>
    /// The two limits are independent: admitting a lookup costs no request permit, so the whole
    /// minute's worth of requests is still available to the lookup that was just admitted. The
    /// opposite — one permit per admitted lookup — is what let one resolution's twelve upstream
    /// requests travel on a single permit.
    /// </summary>
    [Fact]
    public async Task TakingAConcurrencySlot_ChargesNoRequestPermit()
    {
        var clock = NewClock();
        using var budget = new ForeignEmoteSetProviderBudget(clock.Provider);

        using var slot = await budget.TryAcquireConcurrencySlotAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(slot);

        for (var i = 0; i < ForeignEmoteSetProviderBudget.MaxRequestsPerWindow; i++)
        {
            Assert.True(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
        }

        Assert.False(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
    }

    /// <summary>
    /// And the reverse: charging request permits never occupies a lookup slot, so the concurrency gate
    /// keeps meaning "two lookups", not "two requests".
    /// </summary>
    [Fact]
    public async Task ChargingRequestPermits_OccupiesNoConcurrencySlot()
    {
        var clock = NewClock();
        using var budget = new ForeignEmoteSetProviderBudget(clock.Provider);

        for (var i = 0; i < 12; i++)
        {
            Assert.True(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
        }

        using var first = await budget.TryAcquireConcurrencySlotAsync(TimeSpan.Zero, CancellationToken.None);
        using var second = await budget.TryAcquireConcurrencySlotAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    /// <summary>The rate window is spent by request permits, and refuses the 61st within the minute.</summary>
    [Fact]
    public async Task BeyondRateWindow_WithinTheSameWindow_RefusesRatherThanGrantingAnExtraPermit()
    {
        var clock = NewClock();
        using var budget = new ForeignEmoteSetProviderBudget(clock.Provider);

        for (var i = 0; i < ForeignEmoteSetProviderBudget.MaxRequestsPerWindow; i++)
        {
            Assert.True(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
        }

        Assert.False(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
    }

    /// <summary>
    /// The window rolls, it does not reset wholesale. Half the budget is spent at the start of a
    /// minute and half at its 59th second; two seconds later only the first half has aged out, so
    /// exactly that half frees up — not the whole budget. A fixed window would hand out 60 fresh
    /// permits at the boundary, i.e. ~120 within the rolling minute that straddles it, which is the
    /// double burst this limit exists to keep off 7TV.
    /// </summary>
    [Fact]
    public async Task TheWindowRolls_TheBoundaryDoesNotHandOutASecondFullBudget()
    {
        var clock = NewClock();
        using var budget = new ForeignEmoteSetProviderBudget(clock.Provider);
        const int half = ForeignEmoteSetProviderBudget.MaxRequestsPerWindow / 2;

        for (var i = 0; i < half; i++)
        {
            Assert.True(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
        }

        clock.Advance(TimeSpan.FromSeconds(59));
        for (var i = 0; i < half; i++)
        {
            Assert.True(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
        }

        Assert.False(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));

        // Two seconds later the first half is older than the window and the second half is not.
        clock.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < half; i++)
        {
            Assert.True(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
        }

        Assert.False(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
    }

    /// <summary>A permit that has fully aged out is returned — the window frees, it does not leak.</summary>
    [Fact]
    public async Task OnceAFullWindowHasPassed_TheWholeBudgetIsAvailableAgain()
    {
        var clock = NewClock();
        using var budget = new ForeignEmoteSetProviderBudget(clock.Provider);

        for (var i = 0; i < ForeignEmoteSetProviderBudget.MaxRequestsPerWindow; i++)
        {
            Assert.True(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
        }

        clock.Advance(ForeignEmoteSetProviderBudget.Window);

        for (var i = 0; i < ForeignEmoteSetProviderBudget.MaxRequestsPerWindow; i++)
        {
            Assert.True(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
        }
    }

    // ---- #457: the preview reserve (AK 2b) ----

    /// <summary>
    /// AK 2b: with 40 permits held, a charge that must leave 20 free is refused at once, while a
    /// charge without a reserve keeps succeeding up to the unchanged ceiling of 60. protects: preview
    /// pages can never take permits 41–60. fails_when: the reserve charge succeeds, or a no-reserve
    /// charge is refused before 60.
    /// </summary>
    [Fact]
    public async Task WithFortyPermitsHeld_AReserveChargeIsRefused_WhileANoReserveChargeRunsToSixty()
    {
        var clock = NewClock();
        using var budget = new ForeignEmoteSetProviderBudget(clock.Provider);
        const int previewShare = ForeignEmoteSetProviderBudget.MaxRequestsPerWindow - ForeignEmoteSetProviderBudget.PreviewReserve;

        for (var i = 0; i < previewShare; i++)
        {
            Assert.True(await budget.TryChargeRequestAsync(ForeignEmoteSetProviderBudget.PreviewReserve, TimeSpan.Zero));
        }

        Assert.False(await budget.TryChargeRequestAsync(ForeignEmoteSetProviderBudget.PreviewReserve, TimeSpan.Zero));
        for (var i = previewShare; i < ForeignEmoteSetProviderBudget.MaxRequestsPerWindow; i++)
        {
            Assert.True(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
        }

        Assert.False(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
    }

    /// <summary>
    /// AK 2b, the waiting half: a reserve charge waits up to its timeout for headroom above the
    /// reserve, is refused when none frees in time, and is granted once enough permits age out —
    /// with more than 40 held, that takes more than the oldest one expiring.
    /// </summary>
    [Fact]
    public async Task AReserveCharge_WaitsForHeadroomAboveTheReserve_AndIsRefusedWhenNoneFreesInTime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var budget = new ForeignEmoteSetProviderBudget(time);
        // 45 permits, one a second: the reserve charge needs the count at 39, i.e. six to age out.
        for (var i = 0; i < 45; i++)
        {
            Assert.True(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var refused = budget.TryChargeRequestAsync(
            ForeignEmoteSetProviderBudget.PreviewReserve, ForeignEmoteSetProviderBudget.DefaultRequestWaitTimeout);
        time.Advance(ForeignEmoteSetProviderBudget.DefaultRequestWaitTimeout);
        Assert.False(await refused.WaitAsync(TimeSpan.FromSeconds(5)));

        // Now t = 50 s; the sixth-oldest permit (t = 5 s) ages out at t = 65 s.
        var granted = budget.TryChargeRequestAsync(ForeignEmoteSetProviderBudget.PreviewReserve, TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        Assert.False(granted.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await granted.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static FakeClock NewClock() => new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public TimeProvider Provider => this;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
