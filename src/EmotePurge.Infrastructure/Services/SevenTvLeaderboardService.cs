using System.Diagnostics;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The 7TV leaderboard read path (spec 2026-09-13, section 6, T3): composes the in-process stock,
/// the rolling window budget, its own circuit breaker and the search client into the promise that
/// this feature calls 7TV at most ten times per rolling hour and process run — whatever users do,
/// and whatever fails.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two things carry the safety, not one.</b> The finite key space (two sorts, up to two pages
/// each, one hour shelf-life) holds the <i>expected</i> cost at four upstream requests an hour; the
/// window budget holds the <i>lid</i> at ten. A key space alone would cap successfully stocked
/// answers only: on a key that keeps failing, the fill would start again on every click. That is
/// also why a failure is stocked as a returned value with its own shelf-life rather than thrown
/// away.
/// </para>
/// <para>
/// <b>There is no unguarded face of this service.</b> Unlike the foreign-channel preview, whose
/// hardening is a decorator around a raw chain, the guards here are the implementation: no
/// <c>refresh</c> flag, no page parameter, no way for a caller to reach
/// <see cref="ISevenTvApiClient.SearchEmotesAsync"/> except through the stock.
/// </para>
/// <para>
/// <b>One fill, both pages, all or nothing.</b> A fill assembles the whole entry — page 1 and, when
/// 7TV reports more, page 2 — and a failure on either page decides the outcome and the shelf-life of
/// the entry as a whole; page 1 is discarded rather than served short. Serving a silently truncated
/// list is the mistake the foreign-channel preview's F3 already paid for once, and the next fill
/// re-fetching page 1 is what keeps two pages from two different fills ever appearing side by side.
/// </para>
/// <para>
/// <b>The breaker's job here is spread, not restart.</b> Per-key restart is what the stock's
/// shelf-life does. The breaker exists so that a rate limit hit while filling one sort stops the
/// other sort from walking into the same lockout — it answers with the status of whatever opened it
/// and the breaker's own remaining open time as shelf-life, without an upstream request. It is a
/// <i>separate instance</i> from the foreign-channel preview's (registered under a key): a
/// search-bucket lockout must not close a preview path that never touches that bucket, and vice
/// versa.
/// </para>
/// <para>
/// <b>Every decision the breaker allows is answered exactly once, per page.</b> A page that reaches
/// 7TV reports success or failure; a page the window budget refuses releases the probe without an
/// outcome (a refusal is congestion we inflicted on ourselves and says nothing about 7TV's health);
/// and the <c>finally</c> backstop covers anything unexpected that skipped both. The backstop sits
/// inside the per-page attempt, not around the whole fill, so an exception raised while fetching
/// page 2 cannot answer page 1's decision a second time nor leave page 2's unanswered — either
/// mistake would leave <c>_probeInFlight</c> stuck and jam the breaker past its own open duration.
/// </para>
/// <para>
/// <b>One log line per charged upstream request</b>, at Information, written here rather than in the
/// client (operator decision of 2026-09-14, refining E15): only this class knows the budget window,
/// and a second line per request would make the acceptance count on production ambiguous. Nothing
/// else in this class logs at Information — a refused permit and an open breaker made no request at
/// all and stay at Debug, the budget alarm is a Warning, and a recovered breaker needs no line of
/// its own because the very next request line shows the healthy outcome.
/// </para>
/// </remarks>
public sealed class SevenTvLeaderboardService(
    ISevenTvApiClient client,
    SevenTvLeaderboardStore<SevenTvLeaderboardResult> stock,
    SevenTvLeaderboardRequestBudget budget,
    ForeignSevenTvBreakerPolicy breaker,
    SevenTvLeaderboardBudgetAlarm alarm,
    ILogger<SevenTvLeaderboardService> logger) : ISevenTvLeaderboardService
{
    /// <summary>
    /// The server-side page ceiling (spec E5): at most two upstream pages of 250 make up one entry,
    /// so the whole ranking costs at most two requests per sort and the response stays one payload.
    /// </summary>
    public const int MaxUpstreamPages = 2;

    public Task<SevenTvLeaderboardResult> GetLeaderboardAsync(
        SevenTvLeaderboardSort sortBy, CancellationToken cancellationToken = default)
    {
        // The wire code is the stock key: it is the only vocabulary that ever reaches here (the
        // endpoint's filter parses it into the enum first), which is also what keeps the key space
        // at two — an unvalidated string must never be able to open a third entry.
        var key = sortBy.ToWireCode();

        // The caller's token cancels this caller's wait, never the shared fill: whoever arrived
        // first does not own the work everyone else is waiting on.
        //
        // The other half of that split is not enforced, only true today: this closure captures a
        // *scoped* service — and through it a transient 7TV client — into a task the singleton stock
        // keeps running past the end of the request scope that started it. It holds because nothing
        // in that graph objects to being used afterwards: the typed HttpClient comes from the client
        // factory and is not disposed with the scope, SevenTvApiClient is not IDisposable, and the
        // telemetry and logger below it are singletons. Break any of those three — make the client
        // IDisposable, or give it a scoped collaborator — and one browser navigating away turns the
        // shared fill into an ObjectDisposedException for every reader waiting on it, which is
        // exactly the damage the CancellationToken.None rule prevents on the token side. Then this
        // service has to stop capturing its own collaborators (an IServiceScopeFactory per fill, or
        // a singleton fill engine), not merely catch the exception.
        return stock.GetOrFillAsync(key, fillToken => FillAsync(sortBy, fillToken), cancellationToken);
    }

    private async Task<SevenTvLeaderboardStoreFill<SevenTvLeaderboardResult>> FillAsync(
        SevenTvLeaderboardSort sortBy, CancellationToken cancellationToken)
    {
        var rows = new List<ForeignEmoteRow>();
        var seenEmoteIds = new HashSet<string>(StringComparer.Ordinal);
        var totalCount = 0;
        var pageCount = 1;

        for (var page = 1; page <= MaxUpstreamPages; page++)
        {
            var attempt = await FetchPageAsync(sortBy, page, cancellationToken);
            if (attempt.Page is not { } fetched)
            {
                // One failed page fails the whole entry — no partial list is served, and none is
                // kept. Outcome and shelf-life are this failure's, not page 1's.
                return Stock(SevenTvLeaderboardResult.Failed(attempt.Status), attempt.Outcome);
            }

            if (page == 1)
            {
                // 7TV's own figures for the ranking as a whole, taken from the first page only: the
                // second page is a separate query seconds later and may already disagree.
                totalCount = fetched.TotalCount;
                pageCount = fetched.PageCount;
            }

            foreach (var item in fetched.Items)
            {
                // De-duplicated by 7TV id, first occurrence wins. The page boundary can shift
                // between the two queries — 7TV offers neither cursor nor snapshot — so an emote
                // can appear on both pages. It can equally slip through the boundary and appear on
                // neither; that one is accepted (a ranking that moves over hours), a duplicate row
                // in the grid is not.
                if (seenEmoteIds.Add(item.SevenTvEmoteId))
                {
                    rows.Add(new ForeignEmoteRow(
                        item.SevenTvEmoteId,
                        // A leaderboard hit has no per-set alias: its name is its default name.
                        item.DefaultName,
                        item.DefaultName,
                        item.ImageUrl,
                        item.TopAllTime,
                        item.Trending));
                }
            }

            if (page >= pageCount)
            {
                break;
            }
        }

        var response = new SevenTvLeaderboardResponse(
            sortBy.ToWireCode(), totalCount, totalCount > rows.Count, rows);

        // A hit, including a genuinely empty ranking: an empty list is an answer 7TV gave, and
        // re-asking for it every 60 s would spend the lid on nothing.
        return Stock(SevenTvLeaderboardResult.Ok(response), SevenTvLeaderboardFillOutcome.Hit());
    }

    /// <summary>
    /// One upstream page, with the three guards in front of it in their fixed order: breaker, then
    /// budget, then the request. Never throws for an outcome it expects — a rate limit is a value it
    /// returns, so that it can be stocked.
    /// </summary>
    private async Task<PageAttempt> FetchPageAsync(
        SevenTvLeaderboardSort sortBy, int page, CancellationToken cancellationToken)
    {
        var decision = breaker.TryAcquire();
        if (!decision.Allowed)
        {
            // Debug, not louder: this fires for every rejected request while the breaker is open,
            // and the event worth a real line — the opening itself — is logged once, below, on the
            // request that caused it.
            logger.LogDebug(
                "7TV leaderboard fill for sort {SortBy}, page {Page}: circuit breaker open, no upstream request (remaining open time {RemainingSeconds}s).",
                sortBy.ToWireCode(), page, Math.Ceiling(decision.RemainingOpenTime.TotalSeconds));

            // The status follows what opened the breaker (so the other sort answers the way the
            // sort that hit the lockout did); the shelf-life follows the remaining open time.
            return PageAttempt.Failed(
                decision.OpenedByRateLimit
                    ? SevenTvLeaderboardStatus.SevenTvRateLimited
                    : SevenTvLeaderboardStatus.SevenTvUnavailable,
                SevenTvLeaderboardFillOutcome.BreakerOpen(decision.RemainingOpenTime));
        }

        var breakerResolved = false;
        try
        {
            if (!budget.TryCharge(out var usedInWindow))
            {
                logger.LogDebug(
                    "7TV leaderboard fill for sort {SortBy}, page {Page}: window budget refused a permit ({MaxRequests} per rolling {WindowMinutes} min and process run), no upstream request.",
                    sortBy.ToWireCode(), page, budget.MaxRequests, budget.Window.TotalMinutes);

                // Nothing reached 7TV, so the breaker learns nothing — but the probe slot this
                // decision may have taken still has to be given back, or the breaker jams open.
                breaker.ReleaseProbeWithoutOutcome(decision.Generation);
                breakerResolved = true;
                return PageAttempt.Failed(
                    SevenTvLeaderboardStatus.BudgetRefused, SevenTvLeaderboardFillOutcome.BudgetRefused());
            }

            // Only ever fed granted permits — a refusal reports the window as full, and reporting a
            // request that never happened is not what this alarm is for.
            //
            // No test can fail on that today, and it is worth knowing why before trusting it: a
            // refusal reports exactly MaxRequests, and every path to a full window runs through a
            // granted permit that already tripped the latch, so a naive version that fed refusals in
            // would be silenced by the latch rather than by this gate — for every configuration, not
            // just this one. The moment the latch goes (a warning per request instead of once per
            // window, say), this line becomes the only thing standing between a full window and a
            // stream of false alarms, and it is untested. Whoever makes that change owes it a test.
            WarnIfBudgetRunningHot(usedInWindow);

            var result = await client.SearchEmotesAsync(sortBy, page, cancellationToken);
            LogUpstreamRequest(sortBy, page, result, usedInWindow);

            var transition = ApplyBreakerFeedback(result, decision.Generation);
            breakerResolved = true;
            LogBreakerTransition(sortBy, page, result.Status, transition);

            return result.Status switch
            {
                SevenTvEmoteSearchLookupStatus.Ok => PageAttempt.Succeeded(result.Page!),
                SevenTvEmoteSearchLookupStatus.RateLimited => PageAttempt.Failed(
                    SevenTvLeaderboardStatus.SevenTvRateLimited,
                    SevenTvLeaderboardFillOutcome.RateLimited(result.RetryAfter)),
                SevenTvEmoteSearchLookupStatus.Unavailable => PageAttempt.Failed(
                    SevenTvLeaderboardStatus.SevenTvUnavailable, SevenTvLeaderboardFillOutcome.Unavailable()),
                _ => throw new UnreachableException(
                    $"Unexpected {nameof(SevenTvEmoteSearchLookupStatus)} value: {result.Status}.")
            };
        }
        finally
        {
            if (!breakerResolved)
            {
                // Backstop for a genuinely unexpected exception (cancellation included) that skipped
                // every resolution path above — counted as a plain failure, never a rate limit:
                // whatever happened here is not something 7TV told us. Scoped to this page, so an
                // exception on page 2 neither answers page 1's decision twice nor leaves its own
                // unanswered. The exception itself keeps travelling: it faults the stock entry,
                // which the store treats as expired on arrival.
                breaker.RecordFailure(ForeignSevenTvBreakerOutcome.OtherFailure, null, decision.Generation);
            }
        }
    }

    /// <summary>
    /// The one line the production acceptance run counts (AK 31): exactly one per <i>charged</i>
    /// upstream request, written immediately after the client returns, whatever the outcome.
    /// </summary>
    /// <remarks>
    /// The rate-limit figures come from the client's result rather than from a second observation,
    /// which is why that result carries them in every outcome. They are <c>null</c> when 7TV sent
    /// no such header (normal for a validation rejection) or when no response came back at all —
    /// an empty value in this line is therefore information, not a defect.
    /// </remarks>
    private void LogUpstreamRequest(
        SevenTvLeaderboardSort sortBy, int page, SevenTvEmoteSearchPageResult result, int usedInWindow)
    {
        logger.LogInformation(
            "7TV leaderboard upstream request: sort {SortBy}, page {Page}, outcome {Outcome}, search bucket remaining {SearchRemaining}, reset {SearchReset}, requests used in window {UsedInWindow}.",
            sortBy.ToWireCode(), page, result.Status, result.RateLimitRemaining, result.RateLimitReset, usedInWindow);
    }

    /// <summary>
    /// The early warning, raised before the lid actually bites (spec section 6, AK 6): six is the
    /// expected four plus one failed fill cycle, so reaching it means something is retrying that
    /// should not be.
    /// </summary>
    private void WarnIfBudgetRunningHot(int usedInWindow)
    {
        if (!alarm.ShouldRaise(usedInWindow))
        {
            return;
        }

        logger.LogWarning(
            "7TV leaderboard window budget running hot: {UsedInWindow} upstream requests within the rolling {WindowMinutes} min window, alarm threshold {AlarmThreshold}, hard lid {MaxRequests}.",
            usedInWindow,
            budget.Window.TotalMinutes,
            SevenTvLeaderboardBudgetAlarm.Threshold,
            budget.MaxRequests);
    }

    /// <summary>
    /// Feeds one page's outcome back into the breaker — exactly once per allowed decision.
    /// <see cref="SevenTvEmoteSearchLookupStatus.Ok"/> required a real 7TV answer, so it is evidence
    /// the provider is healthy; the other two are the two triggers the breaker distinguishes (a
    /// confirmed 429 opens it at once and honors 7TV's own hint, anything else has to accumulate).
    /// </summary>
    private ForeignSevenTvBreakerTransition ApplyBreakerFeedback(
        SevenTvEmoteSearchPageResult result, long generation) => result.Status switch
        {
            SevenTvEmoteSearchLookupStatus.Ok => breaker.RecordSuccess(generation),
            SevenTvEmoteSearchLookupStatus.RateLimited => breaker.RecordFailure(
                ForeignSevenTvBreakerOutcome.RateLimited, result.RetryAfter, generation),
            SevenTvEmoteSearchLookupStatus.Unavailable => breaker.RecordFailure(
                ForeignSevenTvBreakerOutcome.OtherFailure, null, generation),
            _ => throw new ArgumentOutOfRangeException(
                nameof(result), result.Status, "Unknown SevenTvEmoteSearchLookupStatus.")
        };

    private void LogBreakerTransition(
        SevenTvLeaderboardSort sortBy, int page, SevenTvEmoteSearchLookupStatus status, ForeignSevenTvBreakerTransition transition)
    {
        switch (transition)
        {
            case ForeignSevenTvBreakerTransition.Opened:
                // Once per open event, never per rejected request: a transition only happens on the
                // request that causes it.
                logger.LogWarning(
                    "7TV leaderboard circuit breaker opened (triggered by sort {SortBy}, page {Page}, outcome {Outcome}).",
                    sortBy.ToWireCode(), page, status);
                break;
            case ForeignSevenTvBreakerTransition.Closed:
                // Deliberately Debug: recovery is already visible in this request's Information
                // line, which reports a healthy outcome, and Information is reserved for exactly one
                // line per charged request so the acceptance count stays unambiguous.
                logger.LogDebug("7TV leaderboard circuit breaker closed again.");
                break;
            case ForeignSevenTvBreakerTransition.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(transition), transition, "Unknown ForeignSevenTvBreakerTransition.");
        }
    }

    private static SevenTvLeaderboardStoreFill<SevenTvLeaderboardResult> Stock(
        SevenTvLeaderboardResult result, SevenTvLeaderboardFillOutcome outcome) =>
        new(result, SevenTvLeaderboardTtlPolicy.TimeToLiveFor(outcome));

    /// <param name="Page">Non-null if and only if this page came back <c>Ok</c>.</param>
    /// <param name="Status">The status the whole entry takes if this page failed.</param>
    /// <param name="Outcome">The shelf-life input the whole entry takes if this page failed.</param>
    private readonly record struct PageAttempt(
        SevenTvEmoteSearchPage? Page, SevenTvLeaderboardStatus Status, SevenTvLeaderboardFillOutcome Outcome)
    {
        public static PageAttempt Succeeded(SevenTvEmoteSearchPage page) =>
            new(page, SevenTvLeaderboardStatus.Ok, SevenTvLeaderboardFillOutcome.Hit());

        public static PageAttempt Failed(SevenTvLeaderboardStatus status, SevenTvLeaderboardFillOutcome outcome) =>
            new(null, status, outcome);
    }
}

/// <summary>
/// The latch behind the "budget running hot" warning (spec section 6, AK 6): it fires once per
/// window, not once per request, so that a busy hour produces one line an operator will read rather
/// than five they will scroll past.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate object.</b> The latch has to outlive a single request — it counts per process
/// run, exactly like the budget it watches — while the service that consults it is scoped, like
/// every other read path in this codebase.
/// </para>
/// <para>
/// <b>Windows have no boundary to hook onto.</b> The budget's window is rolling, so there is no
/// tick at which to reset the latch. What resets it is the observation that carries the same
/// information: a granted permit reporting fewer than <see cref="Threshold"/> requests in the
/// window means the earlier burst has aged out, and the next burst is a new one worth reporting.
/// </para>
/// </remarks>
public sealed class SevenTvLeaderboardBudgetAlarm
{
    /// <summary>
    /// The expected four upstream requests an hour plus one failed fill cycle (two pages). Reaching
    /// it is the earliest point at which the traffic can no longer be explained by normal use.
    /// </summary>
    public const int Threshold = 6;

    private readonly Lock _gate = new();

    private bool _raised;

    /// <summary>
    /// Whether this observation is the one to warn about.
    /// </summary>
    /// <param name="usedInWindow">
    /// The count a <see cref="SevenTvLeaderboardRequestBudget.TryCharge"/> reported — <b>only ever
    /// from a granted permit</b>. A refusal reports the window as full, which clears the threshold
    /// by definition and would raise the alarm on requests that were never made, and would never
    /// let the latch reset.
    /// </param>
    public bool ShouldRaise(int usedInWindow)
    {
        lock (_gate)
        {
            if (usedInWindow < Threshold)
            {
                _raised = false;
                return false;
            }

            if (_raised)
            {
                return false;
            }

            _raised = true;
            return true;
        }
    }
}
