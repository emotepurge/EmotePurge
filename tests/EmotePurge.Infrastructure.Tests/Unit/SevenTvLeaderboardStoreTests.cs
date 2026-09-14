using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="SevenTvLeaderboardStore{TValue}"/> in isolation (spec 2026-09-13, section 6, AK 8–11).
/// This is where the feature's upstream lid is actually decided: everything above it — the service,
/// the endpoint, the dialog — can only ask the stock for a key, so a stock that fills a key twice
/// where it should fill it once doubles the lid silently, and nothing further up would notice.
/// </summary>
/// <remarks>
/// The two failures worth the trouble of a test are the ones nobody sees in production: a double
/// fill at the instant of expiry (two readers, two upstream rounds, alarm at eight requests instead
/// of four), and an entry that is dropped instead of replaced after a failed fill, which lets the
/// stock's size drift and takes the failure shelf-life with it. Both are reproduced here
/// deterministically — deliberately, because provoking them live would mean aiming real traffic at
/// 7TV's search bucket.
/// </remarks>
public class SevenTvLeaderboardStoreTests
{
    private const string TrendingDaily = "TRENDING_DAILY";
    private const string TopAllTime = "TOP_ALL_TIME";

    private static readonly TimeSpan HitShelfLife = TimeSpan.FromHours(1);

    /// <summary>
    /// However many readers arrive inside the shelf-life, they share one fill — right up to the last
    /// second before it expires (AK 7's "one second before expiry, no upstream").
    /// </summary>
    [Fact]
    public async Task WithinTheShelfLife_EveryReaderSharesOneFill()
    {
        var clock = new HandWoundTimeProvider();
        var store = new SevenTvLeaderboardStore<Answer>(clock);
        var fills = 0;
        var fill = Fill(() => ++fills, HitShelfLife);

        var first = await store.GetOrFillAsync(TrendingDaily, fill);
        for (var i = 0; i < 50; i++)
        {
            Assert.Same(first, await store.GetOrFillAsync(TrendingDaily, fill));
        }

        clock.Advance(HitShelfLife - TimeSpan.FromSeconds(1));
        Assert.Same(first, await store.GetOrFillAsync(TrendingDaily, fill));

        Assert.Equal(1, fills);
        Assert.Equal(1, store.Count);
    }

    /// <summary>
    /// One second after expiry the next reader refills — once. The readers behind it get that refill,
    /// not one each (AK 7's "one second after expiry, exactly one").
    /// </summary>
    [Fact]
    public async Task OneSecondAfterExpiry_ExactlyOneRefillHappens()
    {
        var clock = new HandWoundTimeProvider();
        var store = new SevenTvLeaderboardStore<Answer>(clock);
        var fills = 0;
        var fill = Fill(() => ++fills, HitShelfLife);

        var stale = await store.GetOrFillAsync(TrendingDaily, fill);
        clock.Advance(HitShelfLife + TimeSpan.FromSeconds(1));

        var refilled = await store.GetOrFillAsync(TrendingDaily, fill);
        for (var i = 0; i < 5; i++)
        {
            Assert.Same(refilled, await store.GetOrFillAsync(TrendingDaily, fill));
        }

        Assert.NotSame(stale, refilled);
        Assert.Equal(2, fills);
        Assert.Equal(1, store.Count);
    }

    /// <summary>
    /// AK 9, and the single most likely defect in this feature: two readers arriving at the exact
    /// instant of expiry must produce <b>one</b> fill, not two.
    /// </summary>
    /// <remarks>
    /// Forced rather than hoped for. The clock's one-shot hook fires inside the reader's own expiry
    /// check — that is, after it has read the expired entry and before it tries to swap it — and runs
    /// a second reader to completion right there. The second reader wins the swap; the first then
    /// finds its <c>TryUpdate</c> comparison stale, drops its own (never started) replacement and
    /// takes the winner's entry. A stock built on <c>AddOrUpdate</c> or on remove-then-add would pass
    /// every other test in this file and fail exactly here, with two fills and two different answers.
    /// </remarks>
    [Fact]
    public async Task TwoReadersAtTheExactInstantOfExpiry_CauseOneFill_AndShareItsAnswer()
    {
        var clock = new HandWoundTimeProvider();
        var store = new SevenTvLeaderboardStore<Answer>(clock);
        var fills = 0;
        var fill = Fill(() => ++fills, HitShelfLife);

        var stale = await store.GetOrFillAsync(TrendingDaily, fill);
        Assert.Equal(1, fills);

        // Not a tick past the shelf-life: exactly on it, which is the instant both readers see.
        clock.SetUtcNow(clock.Now + HitShelfLife);

        Task<Answer>? secondReader = null;
        clock.RunOnceOnNextRead(() => secondReader = store.GetOrFillAsync(TrendingDaily, fill));

        var firstAnswer = await store.GetOrFillAsync(TrendingDaily, fill);
        var secondAnswer = await secondReader!;

        Assert.Equal(2, fills); // the initial fill plus exactly one for the expiry, not two
        Assert.Same(firstAnswer, secondAnswer); // the loser of the swap took the winner's entry
        Assert.NotSame(stale, firstAnswer);
        Assert.Equal(1, store.Count);
    }

    /// <summary>
    /// AK 10, first half. A faulted fill — which can only come from the caller's backstop, since every
    /// expected outcome is a returned value — counts as expired the moment it exists and is replaced
    /// by the next reader. Replaced, not removed: the stock's size never moves.
    /// </summary>
    [Fact]
    public async Task AFaultedFill_IsExpiredAtOnce_AndReplacedRatherThanRemoved()
    {
        var clock = new HandWoundTimeProvider();
        var store = new SevenTvLeaderboardStore<Answer>(clock);
        await store.GetOrFillAsync(TopAllTime, Fill(() => 100, HitShelfLife));

        var attempts = 0;
        Task<SevenTvLeaderboardStoreFill<Answer>> Flaky(CancellationToken _) =>
            ++attempts == 1
                ? throw new InvalidOperationException("an outcome path the fill did not expect")
                : Task.FromResult(new SevenTvLeaderboardStoreFill<Answer>(new Answer(attempts), HitShelfLife));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetOrFillAsync(TrendingDaily, Flaky));
        Assert.Equal(2, store.Count);

        // No clock movement at all: the faulted entry is stale on arrival, not after a shelf-life.
        var recovered = await store.GetOrFillAsync(TrendingDaily, Flaky);
        Assert.Equal(2, attempts);
        Assert.Equal(2, store.Count);

        // And the replacement is stocked normally from here on.
        Assert.Same(recovered, await store.GetOrFillAsync(TrendingDaily, Flaky));
        Assert.Equal(2, attempts);
        Assert.Equal(2, store.Count);
    }

    /// <summary>
    /// AK 10's "never removed", shown where removal is actually observable: two readers arriving at
    /// the same faulted entry.
    /// </summary>
    /// <remarks>
    /// A stock that dropped the failed entry and re-added a fresh one would look identical to this
    /// one from the outside as long as readers come one at a time — the hole is only a few
    /// instructions wide, and the dictionary is back to size two by the time the call returns. Under
    /// the interleaving below it stops being invisible: the second reader's removal takes the *first*
    /// reader's freshly installed entry with it, because a removal is keyed on the key alone and
    /// cannot tell which entry it is dropping. Two upstream rounds, two different answers. The
    /// compare-and-swap has no such hole — it names the exact entry it expects to replace.
    /// </remarks>
    [Fact]
    public async Task TwoReadersAtAFaultedEntry_ShareOneReplacement_AndTheStockNeverShrinks()
    {
        var clock = new HandWoundTimeProvider();
        var store = new SevenTvLeaderboardStore<Answer>(clock);
        await store.GetOrFillAsync(TopAllTime, Fill(() => 100, HitShelfLife));

        var attempts = 0;
        Task<SevenTvLeaderboardStoreFill<Answer>> Flaky(CancellationToken _) =>
            ++attempts == 1
                ? throw new InvalidOperationException("an outcome path the fill did not expect")
                : Task.FromResult(new SevenTvLeaderboardStoreFill<Answer>(new Answer(attempts), HitShelfLife));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetOrFillAsync(TrendingDaily, Flaky));

        Task<Answer>? secondReader = null;
        clock.RunOnceOnNextRead(() => secondReader = store.GetOrFillAsync(TrendingDaily, Flaky));

        var firstAnswer = await store.GetOrFillAsync(TrendingDaily, Flaky);
        var secondAnswer = await secondReader!;

        Assert.Equal(2, attempts); // the failed fill plus exactly one replacement
        Assert.Same(firstAnswer, secondAnswer);
        Assert.Equal(2, store.Count);
    }

    /// <summary>
    /// AK 10, second half. A cancelled fill behaves exactly like a faulted one — the distinction
    /// matters to the caller that awaits it, not to the stock.
    /// </summary>
    [Fact]
    public async Task ACancelledFill_IsExpiredAtOnce_AndReplacedRatherThanRemoved()
    {
        var clock = new HandWoundTimeProvider();
        var store = new SevenTvLeaderboardStore<Answer>(clock);
        await store.GetOrFillAsync(TopAllTime, Fill(() => 100, HitShelfLife));

        var attempts = 0;
        Task<SevenTvLeaderboardStoreFill<Answer>> Flaky(CancellationToken _) =>
            ++attempts == 1
                ? Task.FromCanceled<SevenTvLeaderboardStoreFill<Answer>>(new CancellationToken(canceled: true))
                : Task.FromResult(new SevenTvLeaderboardStoreFill<Answer>(new Answer(attempts), HitShelfLife));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.GetOrFillAsync(TrendingDaily, Flaky));
        Assert.Equal(2, store.Count);

        var recovered = await store.GetOrFillAsync(TrendingDaily, Flaky);
        Assert.Equal(2, attempts);
        Assert.Equal(2, store.Count);
        Assert.Same(recovered, await store.GetOrFillAsync(TrendingDaily, Flaky));
        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// AK 11. The fill belongs to the stock, not to whoever asked first: a caller that gives up takes
    /// only its own wait with it. The fill runs on under <see cref="CancellationToken.None"/>, and its
    /// result still reaches everybody else — including readers who arrive after it finished.
    /// </summary>
    [Fact]
    public async Task ACallerThatGivesUp_DoesNotCancelTheFillForAnyoneElse()
    {
        var clock = new HandWoundTimeProvider();
        var store = new SevenTvLeaderboardStore<Answer>(clock);
        var gate = new TaskCompletionSource<SevenTvLeaderboardStoreFill<Answer>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tokensSeenByTheFill = new List<CancellationToken>();

        Task<SevenTvLeaderboardStoreFill<Answer>> GatedFill(CancellationToken cancellationToken)
        {
            tokensSeenByTheFill.Add(cancellationToken);
            return gate.Task;
        }

        using var abandoning = new CancellationTokenSource();
        var abandoned = store.GetOrFillAsync(TrendingDaily, GatedFill, abandoning.Token);
        var patient = store.GetOrFillAsync(TrendingDaily, GatedFill, CancellationToken.None);

        await abandoning.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.False(patient.IsCompleted);

        gate.SetResult(new SevenTvLeaderboardStoreFill<Answer>(new Answer(1), HitShelfLife));
        var answer = await patient;

        Assert.Single(tokensSeenByTheFill);
        Assert.False(tokensSeenByTheFill[0].CanBeCanceled); // CancellationToken.None, never a caller's
        Assert.Same(answer, await store.GetOrFillAsync(TrendingDaily, GatedFill));
        Assert.Single(tokensSeenByTheFill);
    }

    /// <summary>
    /// A running entry has no expiry at all, so no amount of elapsed time lets a second reader replace
    /// it mid-flight — which is also what makes the shelf-life count from the moment the fill
    /// finished rather than from the moment it started.
    /// </summary>
    [Fact]
    public async Task ARunningFillNeverExpires_AndItsShelfLifeStartsWhenItFinishes()
    {
        var clock = new HandWoundTimeProvider();
        var store = new SevenTvLeaderboardStore<Answer>(clock);
        var gate = new TaskCompletionSource<SevenTvLeaderboardStoreFill<Answer>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fills = 0;

        Task<SevenTvLeaderboardStoreFill<Answer>> GatedFill(CancellationToken _)
        {
            fills++;
            return gate.Task;
        }

        var early = store.GetOrFillAsync(TrendingDaily, GatedFill);
        Assert.False(early.IsCompleted);

        clock.Advance(TimeSpan.FromDays(7)); // far past any shelf-life this feature ever hands out
        var late = store.GetOrFillAsync(TrendingDaily, GatedFill);

        Assert.Equal(1, fills);
        Assert.False(late.IsCompleted);

        gate.SetResult(new SevenTvLeaderboardStoreFill<Answer>(new Answer(1), HitShelfLife));
        Assert.Same(await early, await late);

        // The shelf-life ran from completion, so the entry is fresh now and stale an hour from now.
        clock.Advance(HitShelfLife - TimeSpan.FromSeconds(1));
        Assert.Same(await early, await store.GetOrFillAsync(TrendingDaily, GatedFill));
        Assert.Equal(1, fills);
    }

    /// <summary>
    /// AK 8. An empty list is an answer, not a failure: it is stocked for the full hit shelf-life, so
    /// a sort key 7TV currently has nothing for cannot be turned into an upstream request per click.
    /// </summary>
    [Fact]
    public async Task AnEmptyListIsStockedLikeAHit()
    {
        var clock = new HandWoundTimeProvider();
        var store = new SevenTvLeaderboardStore<IReadOnlyList<string>>(clock);
        var fills = 0;

        Task<SevenTvLeaderboardStoreFill<IReadOnlyList<string>>> EmptyFill(CancellationToken _)
        {
            fills++;
            return Task.FromResult(
                new SevenTvLeaderboardStoreFill<IReadOnlyList<string>>(new List<string>(), HitShelfLife));
        }

        var first = await store.GetOrFillAsync(TrendingDaily, EmptyFill);
        clock.Advance(HitShelfLife - TimeSpan.FromSeconds(1));
        var second = await store.GetOrFillAsync(TrendingDaily, EmptyFill);

        Assert.Empty(first);
        Assert.Same(first, second);
        Assert.Equal(1, fills);

        clock.Advance(TimeSpan.FromSeconds(2));
        await store.GetOrFillAsync(TrendingDaily, EmptyFill);
        Assert.Equal(2, fills);
    }

    /// <summary>Keys are independent: one key's fill is never handed to another.</summary>
    [Fact]
    public async Task EachKeyIsFilledOnItsOwn()
    {
        var clock = new HandWoundTimeProvider();
        var store = new SevenTvLeaderboardStore<Answer>(clock);
        var fills = 0;
        var fill = Fill(() => ++fills, HitShelfLife);

        var trending = await store.GetOrFillAsync(TrendingDaily, fill);
        var allTime = await store.GetOrFillAsync(TopAllTime, fill);

        Assert.NotSame(trending, allTime);
        Assert.Equal(2, fills);
        Assert.Equal(2, store.Count);
        Assert.Same(trending, await store.GetOrFillAsync(TrendingDaily, fill));
        Assert.Equal(2, fills);
    }

    private static Func<CancellationToken, Task<SevenTvLeaderboardStoreFill<Answer>>> Fill(
        Func<int> nextFillNumber, TimeSpan timeToLive) =>
        _ => Task.FromResult(new SevenTvLeaderboardStoreFill<Answer>(new Answer(nextFillNumber()), timeToLive));

    // A distinct object per fill, so "did these two readers get the same fill?" is a reference
    // question rather than a value question.
    private sealed record Answer(int FillNumber);
}
