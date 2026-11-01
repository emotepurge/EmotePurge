using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
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

    /// <summary>
    /// AK 2b, end to end through the real client: thirty two-page previews (with their verification
    /// re-reads) against one shared budget take at most 40 permits, and an editor-grants lookup
    /// (2 permits) and a set-owner lookup (1 permit) still succeed afterwards, with exactly 17
    /// no-reserve permits left beyond them. The budget's waits are cut to zero here: with every
    /// permit fresh, the 5 s first-pass wait could not free one anyway, and the point is the count.
    /// fails_when: previews take permits 41–60, or a permission/ownership read is refused.
    /// no_claim: multi-replica deployments (the budget is in-process).
    /// </summary>
    [Fact]
    public async Task ThirtyTwoPagePreviews_LeaveTheReserveToTheEditorGrantsAndOwnerLookups()
    {
        var clock = NewClock();
        using var budget = new ForeignEmoteSetProviderBudget(clock.Provider);
        var handler = new SevenTvGqlRouteHandler()
            .Answer(SevenTvGqlRouteHandler.Owner, body => Json(body.Contains("owner_id", StringComparison.Ordinal)
                ? """{"data":{"emote_set":{"owner_id":"01OWNER"}}}"""
                : TwoPageSet(PageOf(body))))
            .Answer(SevenTvGqlRouteHandler.Identity, _ => Json(
                """{"data":{"user_by_connection":{"id":"01ACTOR","connections":[{"platform":"TWITCH","id":"100","emote_set_id":null}]}}}"""))
            .Answer(SevenTvGqlRouteHandler.EditorOf, _ => Json(
                """{"data":{"user":{"editor_of":[{"user":{"connections":[{"platform":"TWITCH","id":"200","username":"EditedChannel"}]}}]}}}"""));
        var client = new SevenTvApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") },
            new RecordingRateLimitTelemetry(),
            new WaitlessBudget(budget),
            new RecordingSevenTvSearchBudget(),
            new RecordingLogger<SevenTvApiClient>());

        var previews = new List<SevenTvEmoteSetPreviewResult>();
        for (var i = 0; i < 30; i++)
        {
            previews.Add(await client.GetEmoteSetPreviewAsync($"set-{i}"));
        }

        // 13 verified previews (3 permits each) and the first page of a 14th: 40 permits, no more.
        Assert.Equal(13, previews.Count(p => p.Status == SevenTvPreviewLookupStatus.Ok));
        Assert.All(previews.Where(p => p.Status == SevenTvPreviewLookupStatus.Ok),
            p => Assert.Equal(SevenTvSetReadState.Complete, p.Preview!.ReadState));
        Assert.Equal(17, previews.Count(p => p.Status == SevenTvPreviewLookupStatus.BudgetExhausted));
        Assert.Equal(SevenTvEditorGrantsLookupStatus.Ok, (await client.LookUpEditorGrantsAsync("100")).Status);
        Assert.Equal(SevenTvEmoteSetOwnerLookupStatus.Ok, (await client.LookUpEmoteSetOwnerAsync("set-x")).Status);
        for (var i = 0; i < 17; i++)
        {
            Assert.True(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
        }

        Assert.False(await budget.TryChargeRequestAsync(TimeSpan.Zero, CancellationToken.None));
    }

    private static int PageOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("variables").GetProperty("page").GetInt32();
    }

    private static string TwoPageSet(int page) =>
        "{\"data\":{\"emote_sets\":{\"emote_set\":{\"name\":\"Set\",\"capacity\":1000,\"emotes\":{\"total_count\":2,\"page_count\":2,\"items\":[{\"alias\":\"A"
        + page + "\",\"emote\":{\"id\":\"e" + page + "\",\"default_name\":\"e" + page + "\"}}]}}}}}";

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static FakeClock NewClock() => new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

    // The real budget with every wait cut to zero, reserve passed through unchanged.
    private sealed class WaitlessBudget(ForeignEmoteSetProviderBudget inner) : IForeignUpstreamRequestBudget
    {
        public Task<bool> TryChargeRequestAsync(CancellationToken cancellationToken = default) =>
            inner.TryChargeRequestAsync(TimeSpan.Zero, cancellationToken);

        public Task<bool> TryChargeRequestAsync(int reserve, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            inner.TryChargeRequestAsync(reserve, TimeSpan.Zero, cancellationToken);
    }

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public TimeProvider Provider => this;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
