using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The promise this suite exists to keep (spec 2026-09-13, section 6): however often the leaderboard
/// is asked for, 7TV sees at most four requests an hour in normal operation and never more than ten
/// in any rolling hour of a process run — and every decision the circuit breaker hands out is
/// answered exactly once, per page, so a failure can never jam it past its own open duration.
/// </summary>
/// <remarks>
/// Container-free: the only collaborator that reaches outside the process is
/// <see cref="ISevenTvApiClient"/>, and it is substituted. Time is hand-wound throughout — a rolling
/// hour, a shelf-life and an open duration are all wall-clock facts, and none of them may be proven
/// by waiting.
/// </remarks>
public class SevenTvLeaderboardServiceTests
{
    private const SevenTvLeaderboardSort Trending = SevenTvLeaderboardSort.TrendingDaily;
    private const SevenTvLeaderboardSort AllTime = SevenTvLeaderboardSort.TopAllTime;

    private static readonly TimeSpan JustPastAFailureShelfLife = TimeSpan.FromSeconds(61);

    [Fact]
    public async Task TwoPages_AreAssembledInSevenTvOrder_AndDeduplicatedByEmoteId()
    {
        var harness = new Harness();
        harness.Answer(Trending, 1, OkPage(totalCount: 705, pageCount: 2, "a", "b"));
        // "b" slipped back across the page boundary between the two queries — 7TV offers neither
        // cursor nor snapshot, so the same emote can legitimately answer on both pages.
        harness.Answer(Trending, 2, OkPage(totalCount: 705, pageCount: 2, "b", "c"));

        var result = await harness.Service.GetLeaderboardAsync(Trending);

        Assert.Equal(SevenTvLeaderboardStatus.Ok, result.Status);
        var response = result.Response!;
        Assert.Equal(new[] { "a", "b", "c" }, response.Emotes.Select(emote => emote.SevenTvEmoteId));
        Assert.Equal("TRENDING_DAILY", response.SortBy);
        Assert.Equal(705, response.TotalCount);
        // A leaderboard hit has no per-set alias: the name shown is the emote's default name, not
        // the alias field the foreign-set preview fills.
        Assert.Equal(new[] { "default-a", "default-b", "default-c" }, response.Emotes.Select(emote => emote.Name));
        Assert.All(response.Emotes, emote => Assert.Equal(emote.DefaultName, emote.Name));
    }

    [Theory]
    [InlineData(705, true)]
    [InlineData(2, false)]
    public async Task Truncated_SaysWhetherSevenTvReportsMoreThanTheAssembledList(int totalCount, bool expected)
    {
        var harness = new Harness();
        harness.Answer(Trending, 1, OkPage(totalCount, pageCount: 1, "a", "b"));

        var result = await harness.Service.GetLeaderboardAsync(Trending);

        Assert.Equal(expected, result.Response!.Truncated);
    }

    [Fact]
    public async Task PageTwo_IsOnlyFetchedWhenSevenTvReportsMoreThanOnePage()
    {
        var harness = new Harness();
        harness.Answer(Trending, 1, OkPage(totalCount: 2, pageCount: 1, "a", "b"));

        var result = await harness.Service.GetLeaderboardAsync(Trending);

        Assert.Equal(SevenTvLeaderboardStatus.Ok, result.Status);
        Assert.Equal(1, harness.UpstreamRequests);
    }

    [Fact]
    public async Task AFailureOnPageTwo_FailsTheWholeEntry_AndServesNoPartialList()
    {
        var harness = new Harness();
        harness.Answer(Trending, 1, OkPage(totalCount: 705, pageCount: 2, "a", "b"));
        harness.Answer(Trending, 2, FailedPage(SevenTvEmoteSearchLookupStatus.Unavailable));

        var result = await harness.Service.GetLeaderboardAsync(Trending);

        // Not a quietly truncated list of 250 — that is the mistake the foreign-channel preview's F3
        // already paid for once.
        Assert.Equal(SevenTvLeaderboardStatus.SevenTvUnavailable, result.Status);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task ARateLimitOnOneSort_StopsTheOtherSortWithoutUpstream_ForTheBreakersOwnOpenTime()
    {
        // The other half of AK 12 — the foreign-channel preview's breaker staying closed — is a
        // property of the registration, not of this class, and is proven where it lives:
        // SevenTvLeaderboardRegistrationTests. A second instance constructed here would only ever
        // assert that a breaker nobody touched is closed.
        var harness = new Harness();
        harness.Answer(
            Trending, 1, FailedPage(SevenTvEmoteSearchLookupStatus.RateLimited, retryAfter: TimeSpan.FromSeconds(300)));

        var first = await harness.Service.GetLeaderboardAsync(Trending);
        var second = await harness.Service.GetLeaderboardAsync(AllTime);

        Assert.Equal(SevenTvLeaderboardStatus.SevenTvRateLimited, first.Status);
        // The breaker's whole job here: spread across the two sort keys. The second sort answers the
        // way the first did, with no request of its own.
        Assert.Equal(SevenTvLeaderboardStatus.SevenTvRateLimited, second.Status);
        Assert.Equal(1, harness.UpstreamRequests);

        // Shelf-life of the rejected sort comes from the breaker's remaining open time, not from the
        // 60 s an ordinary failure would get. Identity is what tells those two apart: with a 60 s
        // shelf-life the entry would have been refilled by now — against the still-open breaker, so
        // with the same status and the same request count, but a different object.
        harness.Clock.Advance(TimeSpan.FromSeconds(250));
        Assert.Same(second, await harness.Service.GetLeaderboardAsync(AllTime));
        Assert.Equal(1, harness.UpstreamRequests);
    }

    [Fact]
    public async Task ABudgetRefusal_MakesNoUpstreamRequest_AndIsStockedForThirtySeconds()
    {
        // One permit per ten seconds, already spent: the budget refuses now and would grant again
        // eleven seconds from now — which is what makes the shelf-life visible rather than assumed.
        var harness = new Harness(maxRequests: 1, window: TimeSpan.FromSeconds(10));
        Assert.True(harness.Budget.TryCharge(out _));
        harness.Answer(Trending, 1, OkPage(totalCount: 1, pageCount: 1, "a"));

        var refused = await harness.Service.GetLeaderboardAsync(Trending);

        Assert.Equal(SevenTvLeaderboardStatus.BudgetRefused, refused.Status);
        Assert.Equal(0, harness.UpstreamRequests);
        // No Information line: there was no request to report. A refusal is Debug.
        Assert.Empty(harness.InformationLines);

        // The permit has aged out, so only the stocked refusal can be holding the fill back.
        harness.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(SevenTvLeaderboardStatus.BudgetRefused, (await harness.Service.GetLeaderboardAsync(Trending)).Status);
        Assert.Equal(0, harness.UpstreamRequests);

        harness.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(SevenTvLeaderboardStatus.Ok, (await harness.Service.GetLeaderboardAsync(Trending)).Status);
        Assert.Equal(1, harness.UpstreamRequests);
    }

    [Fact]
    public async Task BudgetRefusals_AreNeverReportedToTheBreaker()
    {
        var harness = new Harness(maxRequests: 1, window: TimeSpan.FromHours(1));
        Assert.True(harness.Budget.TryCharge(out _));

        // Five in a row — one more than the breaker's threshold for ordinary failures. If a refusal
        // counted as one, the breaker would be open by now.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(
                SevenTvLeaderboardStatus.BudgetRefused,
                (await harness.Service.GetLeaderboardAsync(Trending)).Status);
            harness.Clock.Advance(TimeSpan.FromSeconds(31));
        }

        Assert.True(harness.Breaker.TryAcquire(ForeignSevenTvBreakerOperations.Leaderboard).Allowed);
    }

    [Fact]
    public async Task TwentyFillAttemptsWithinAnHour_ProduceExactlyTenUpstreamRequests()
    {
        var harness = new Harness();
        // Every fill fails and is stocked for 60 s, so winding the clock past that shelf-life makes
        // each attempt a fresh fill — the worst case the hard lid exists for.
        harness.Answer(Trending, 1, FailedPage(SevenTvEmoteSearchLookupStatus.Unavailable));

        var statuses = new List<SevenTvLeaderboardStatus>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (attempt > 0)
            {
                harness.Clock.Advance(JustPastAFailureShelfLife);
            }

            statuses.Add((await harness.Service.GetLeaderboardAsync(Trending)).Status);
        }

        Assert.Equal(10, harness.UpstreamRequests);
        Assert.Equal(10, harness.InformationLines.Count);
        Assert.All(statuses.Take(10), status => Assert.Equal(SevenTvLeaderboardStatus.SevenTvUnavailable, status));
        // The eleventh and everything after it: refused by the budget, with no request behind it.
        // That they keep answering "budget refused" rather than "breaker open" also proves each
        // refusal handed the probe slot back — a slot left in flight would reject the next attempt.
        Assert.All(statuses.Skip(10), status => Assert.Equal(SevenTvLeaderboardStatus.BudgetRefused, status));

        // And none of those ten refusals was reported as a failure: a breaker that had counted them
        // would have re-opened for another 60 s on the last one.
        Assert.True(harness.Breaker.TryAcquire(ForeignSevenTvBreakerOperations.Leaderboard).Allowed);
    }

    [Fact]
    public async Task AThousandCallsAcrossBothSortsWithinAnHour_ProduceFourUpstreamRequests()
    {
        var harness = new Harness();
        harness.Answer(Trending, 1, OkPage(totalCount: 705, pageCount: 2, "a"));
        harness.Answer(Trending, 2, OkPage(totalCount: 705, pageCount: 2, "b"));
        harness.Answer(AllTime, 1, OkPage(totalCount: 1371890, pageCount: 2, "c"));
        harness.Answer(AllTime, 2, OkPage(totalCount: 1371890, pageCount: 2, "d"));

        for (var round = 0; round < 500; round++)
        {
            Assert.Equal(SevenTvLeaderboardStatus.Ok, (await harness.Service.GetLeaderboardAsync(Trending)).Status);
            Assert.Equal(SevenTvLeaderboardStatus.Ok, (await harness.Service.GetLeaderboardAsync(AllTime)).Status);
            harness.Clock.Advance(TimeSpan.FromSeconds(3));
        }

        // Two sorts, two pages each: the expected cost of a warm hour, regardless of traffic.
        Assert.Equal(4, harness.UpstreamRequests);
    }

    [Fact]
    public async Task AColdStartOfBothSorts_SpendsExactlyFourPermits()
    {
        var harness = new Harness();
        harness.Answer(Trending, 1, OkPage(totalCount: 705, pageCount: 2, "a"));
        harness.Answer(Trending, 2, OkPage(totalCount: 705, pageCount: 2, "b"));
        harness.Answer(AllTime, 1, OkPage(totalCount: 1371890, pageCount: 2, "c"));
        harness.Answer(AllTime, 2, OkPage(totalCount: 1371890, pageCount: 2, "d"));

        await harness.Service.GetLeaderboardAsync(Trending);
        await harness.Service.GetLeaderboardAsync(AllTime);

        // The next permit is the fifth, so exactly four were spent — the figure the restart contract
        // adds up across process runs inside one rolling hour.
        Assert.True(harness.Budget.TryCharge(out var usedInWindow));
        Assert.Equal(5, usedInWindow);
    }

    [Fact]
    public async Task AfterAPageTwoFailure_TheNextFillRefetchesPageOne_AndStillNeverDuplicatesAnId()
    {
        var harness = new Harness();
        harness.Answer(
            Trending,
            1,
            OkPage(totalCount: 705, pageCount: 2, "a", "b"),
            // Second fill: the page boundary has moved — "c" climbed onto page 1 and still answers
            // on page 2 below.
            OkPage(totalCount: 705, pageCount: 2, "a", "c"));
        harness.Answer(
            Trending,
            2,
            FailedPage(SevenTvEmoteSearchLookupStatus.Unavailable),
            OkPage(totalCount: 705, pageCount: 2, "c", "d"));

        Assert.Equal(
            SevenTvLeaderboardStatus.SevenTvUnavailable,
            (await harness.Service.GetLeaderboardAsync(Trending)).Status);
        harness.Clock.Advance(JustPastAFailureShelfLife);
        var result = await harness.Service.GetLeaderboardAsync(Trending);

        // "b" is gone: the first fill's page 1 was discarded rather than kept and topped up.
        Assert.Equal(new[] { "a", "c", "d" }, result.Response!.Emotes.Select(emote => emote.SevenTvEmoteId));
        Assert.Equal(4, harness.UpstreamRequests);
    }

    [Fact]
    public async Task AfterTheShelfLifeExpires_TheNextFillNeverMixesPagesFromTwoFills()
    {
        var harness = new Harness();
        harness.Answer(
            Trending,
            1,
            OkPage(totalCount: 705, pageCount: 2, "a", "b"),
            OkPage(totalCount: 705, pageCount: 2, "a", "c"));
        harness.Answer(
            Trending,
            2,
            OkPage(totalCount: 705, pageCount: 2, "c", "d"),
            OkPage(totalCount: 705, pageCount: 2, "c", "e"));

        var first = await harness.Service.GetLeaderboardAsync(Trending);
        harness.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        var second = await harness.Service.GetLeaderboardAsync(Trending);

        Assert.Equal(new[] { "a", "b", "c", "d" }, first.Response!.Emotes.Select(emote => emote.SevenTvEmoteId));
        // Nothing from the first fill survives into the second, and "c" — which answered on both of
        // the second fill's pages — appears once.
        Assert.Equal(new[] { "a", "c", "e" }, second.Response!.Emotes.Select(emote => emote.SevenTvEmoteId));
    }

    [Fact]
    public async Task PageOneGranted_PageTwoRefused_FailsTheWholeEntry_AndLeavesPageOnesPermitSpent()
    {
        var harness = new Harness(maxRequests: 1, window: TimeSpan.FromHours(1));
        harness.Answer(Trending, 1, OkPage(totalCount: 705, pageCount: 2, "a", "b"));
        harness.Answer(Trending, 2, OkPage(totalCount: 705, pageCount: 2, "c"));

        var result = await harness.Service.GetLeaderboardAsync(Trending);

        Assert.Equal(SevenTvLeaderboardStatus.BudgetRefused, result.Status);
        Assert.Null(result.Response);
        Assert.Equal(1, harness.UpstreamRequests);
        // Page 1 really was charged: the window has nothing left to give.
        Assert.False(harness.Budget.TryCharge(out _));
    }

    [Fact]
    public async Task TwoCallsWithinTheShelfLife_ProduceOneSetOfUpstreamRequests()
    {
        var harness = new Harness();
        harness.Answer(Trending, 1, OkPage(totalCount: 705, pageCount: 2, "a"));
        harness.Answer(Trending, 2, OkPage(totalCount: 705, pageCount: 2, "b"));

        // There is no refresh flag and no page parameter on this contract, so "ask again" is the
        // strongest thing a client can do — and it reads the stock.
        var first = await harness.Service.GetLeaderboardAsync(Trending);
        harness.Clock.Advance(TimeSpan.FromMinutes(59));
        var second = await harness.Service.GetLeaderboardAsync(Trending);

        Assert.Equal(2, harness.UpstreamRequests);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task TheBudgetAlarm_IsRaisedOnceAtSix_BeforeTheLidBites()
    {
        var harness = new Harness();
        harness.Answer(Trending, 1, FailedPage(SevenTvEmoteSearchLookupStatus.Unavailable));

        await harness.FillRepeatedly(Trending, times: 5);
        Assert.Empty(harness.AlarmWarnings);

        await harness.FillRepeatedly(Trending, times: 1, windTheClockFirst: true);
        Assert.Single(harness.AlarmWarnings);

        // Four more requests — the alarm has already said what it has to say for this window.
        await harness.FillRepeatedly(Trending, times: 4, windTheClockFirst: true);
        Assert.Equal(10, harness.UpstreamRequests);
        Assert.Single(harness.AlarmWarnings);
    }

    [Fact]
    public async Task AnUnexpectedClientException_ReleasesTheProbeExactlyOnce_AndStocksNothing()
    {
        var harness = new Harness();
        // Open the breaker, then let its open duration elapse, so the fill below runs as the single
        // probe the breaker allows through — the state in which a missing report jams it forever.
        harness.Breaker.RecordFailure(
            ForeignSevenTvBreakerOperations.Leaderboard,
            ForeignSevenTvBreakerOutcome.RateLimited,
            TimeSpan.FromSeconds(60),
            harness.Breaker.TryAcquire(ForeignSevenTvBreakerOperations.Leaderboard).Generation);
        harness.Clock.Advance(JustPastAFailureShelfLife);
        harness.Throw(Trending, 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.GetLeaderboardAsync(Trending));

        Assert.Equal(1, harness.UpstreamRequests);
        // The probe failed, so the breaker is open again rather than stuck mid-probe.
        Assert.False(harness.Breaker.TryAcquire(ForeignSevenTvBreakerOperations.Leaderboard).Allowed);

        harness.Clock.Advance(JustPastAFailureShelfLife);
        harness.Answer(Trending, 1, OkPage(totalCount: 1, pageCount: 1, "a"));
        var result = await harness.Service.GetLeaderboardAsync(Trending);

        // Two things at once: the breaker let a fresh probe through (so the previous one had been
        // answered), and the faulted entry was replaced rather than served (so nothing was stocked).
        Assert.Equal(SevenTvLeaderboardStatus.Ok, result.Status);
        Assert.Equal(2, harness.UpstreamRequests);
    }

    [Fact]
    public async Task AnExceptionOnPageTwo_AnswersPageOnesDecisionOnce_AndPageTwosOnce()
    {
        var harness = new Harness();
        harness.Answer(Trending, 1, OkPage(totalCount: 705, pageCount: 2, "a"));
        harness.Throw(Trending, 2);

        // Two ordinary failures on the clock before the fill, so the breaker's streak is a number
        // this test can reason about afterwards.
        harness.RecordOrdinaryFailures(2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.GetLeaderboardAsync(Trending));

        // Page 1 succeeded and reset the streak to zero; page 2's exception then put it at one, once.
        // Three more failures leave it at four, below the threshold of five.
        harness.RecordOrdinaryFailures(3);
        Assert.True(harness.Breaker.TryAcquire(ForeignSevenTvBreakerOperations.Leaderboard).Allowed);

        // The fifth tips it over — which it could not do if page 1's success had gone unreported (the
        // streak would have opened the breaker one failure ago) or if page 2's exception had been
        // reported twice (likewise).
        harness.RecordOrdinaryFailures(1);
        Assert.False(harness.Breaker.TryAcquire(ForeignSevenTvBreakerOperations.Leaderboard).Allowed);
    }

    [Fact]
    public async Task EveryChargedRequestWritesExactlyOneLine_FailedOnesIncluded()
    {
        var harness = new Harness();
        harness.Answer(
            Trending,
            1,
            FailedPage(SevenTvEmoteSearchLookupStatus.Unavailable),
            OkPage(totalCount: 2, pageCount: 2, "a"),
            OkPage(totalCount: 2, pageCount: 2, "a"));
        harness.Answer(Trending, 2, OkPage(totalCount: 2, pageCount: 2, "b"));

        await harness.Service.GetLeaderboardAsync(Trending);
        harness.Clock.Advance(JustPastAFailureShelfLife);
        await harness.Service.GetLeaderboardAsync(Trending);

        // One failed request plus two pages of the successful fill — three requests, three lines.
        Assert.Equal(3, harness.UpstreamRequests);
        Assert.Equal(3, harness.InformationLines.Count);

        var failureLine = harness.InformationLines[0];
        Assert.Contains("TRENDING_DAILY", failureLine, StringComparison.Ordinal);
        Assert.Contains("page 1", failureLine, StringComparison.Ordinal);
        Assert.Contains("outcome Unavailable", failureLine, StringComparison.Ordinal);
        // The figure the production acceptance run reads off this line.
        Assert.Contains("used in window 1", failureLine, StringComparison.Ordinal);
        Assert.Contains("used in window 3", harness.InformationLines[2], StringComparison.Ordinal);
        // 7TV's own bucket sample, carried through from the client rather than logged twice.
        Assert.Contains("remaining 97", harness.InformationLines[1], StringComparison.Ordinal);
        Assert.Contains("reset 3583", harness.InformationLines[1], StringComparison.Ordinal);
    }

    private static SevenTvEmoteSearchPageResult OkPage(int totalCount, int pageCount, params string[] emoteIds) =>
        SevenTvEmoteSearchPageResult.Ok(
            new SevenTvEmoteSearchPage(totalCount, pageCount, [.. emoteIds.Select(BuildItem)]),
            rateLimitLimit: "100",
            rateLimitRemaining: "97",
            rateLimitReset: "3583");

    private static SevenTvEmoteSearchPageResult FailedPage(
        SevenTvEmoteSearchLookupStatus status, TimeSpan? retryAfter = null) =>
        SevenTvEmoteSearchPageResult.Failed(status, null, null, null, retryAfter);

    // Alias and default name differ on purpose: the leaderboard must show the default name, and a
    // row that copied the alias would pass unnoticed if the two were the same string.
    private static SevenTvEmoteSetPreviewItem BuildItem(string emoteId) =>
        new(emoteId, $"alias-{emoteId}", $"default-{emoteId}", $"https://cdn.7tv.app/emote/{emoteId}/2x.webp", 42, 7);

    private sealed class Harness
    {
        public Harness(int maxRequests = SevenTvLeaderboardRequestBudget.DefaultMaxRequests, TimeSpan? window = null)
        {
            Clock = new HandWoundTimeProvider();
            Client = Substitute.For<ISevenTvApiClient>();
            Breaker = new ForeignSevenTvBreakerPolicy(Clock);
            Budget = new SevenTvLeaderboardRequestBudget(
                maxRequests, window ?? SevenTvLeaderboardRequestBudget.DefaultWindow, Clock);
            Logger = new RecordingLogger<SevenTvLeaderboardService>();
            Service = new SevenTvLeaderboardService(
                Client,
                new SevenTvLeaderboardStore<SevenTvLeaderboardResult>(Clock),
                Budget,
                Breaker,
                new SevenTvLeaderboardBudgetAlarm(),
                Logger);
        }

        public HandWoundTimeProvider Clock { get; }

        public ISevenTvApiClient Client { get; }

        public ForeignSevenTvBreakerPolicy Breaker { get; }

        public SevenTvLeaderboardRequestBudget Budget { get; }

        public RecordingLogger<SevenTvLeaderboardService> Logger { get; }

        public SevenTvLeaderboardService Service { get; }

        public int UpstreamRequests => Client.ReceivedCalls().Count();

        public IReadOnlyList<string> InformationLines =>
            [.. Logger.Entries.Where(entry => entry.Level == LogLevel.Information).Select(entry => entry.Message)];

        public IReadOnlyList<string> AlarmWarnings =>
            [.. Logger.Entries
                .Where(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("running hot", StringComparison.Ordinal))
                .Select(entry => entry.Message)];

        public void Answer(SevenTvLeaderboardSort sortBy, int page, params SevenTvEmoteSearchPageResult[] results) =>
            Client.SearchEmotesAsync(sortBy, page, Arg.Any<CancellationToken>())
                .Returns(results[0], [.. results.Skip(1)]);

        public void Throw(SevenTvLeaderboardSort sortBy, int page) =>
            Client.SearchEmotesAsync(sortBy, page, Arg.Any<CancellationToken>())
                .Returns<SevenTvEmoteSearchPageResult>(_ => throw new InvalidOperationException("unexpected"));

        /// <summary>Runs whole fills back to back, winding past the failure shelf-life in between.</summary>
        public async Task FillRepeatedly(SevenTvLeaderboardSort sortBy, int times, bool windTheClockFirst = false)
        {
            for (var run = 0; run < times; run++)
            {
                if (windTheClockFirst || run > 0)
                {
                    Clock.Advance(JustPastAFailureShelfLife);
                }

                await Service.GetLeaderboardAsync(sortBy);
            }
        }

        /// <summary>Adds failures to the breaker's streak from outside, always at its current generation.</summary>
        public void RecordOrdinaryFailures(int count)
        {
            for (var failure = 0; failure < count; failure++)
            {
                Breaker.RecordFailure(
                    ForeignSevenTvBreakerOperations.Leaderboard,
                    ForeignSevenTvBreakerOutcome.OtherFailure,
                    null,
                    Breaker.TryAcquire(ForeignSevenTvBreakerOperations.Leaderboard).Generation);
            }
        }
    }
}
