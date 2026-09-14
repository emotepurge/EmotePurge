namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// How a whole leaderboard fill ended — the input of the shelf-life rule
/// (<see cref="SevenTvLeaderboardTtlPolicy"/>), spec 2026-09-13, section 6.
/// </summary>
/// <remarks>
/// Deliberately the outcome of the <i>fill</i>, not of a single upstream page: one entry holds one
/// fill (page 1 and, when 7TV reports more, page 2), and a failure on either page decides the
/// shelf-life of the whole entry. Also deliberately distinct from the service's public result
/// status: an open breaker answers with the <i>status</i> of whatever opened it (rate limited or
/// unavailable), but its shelf-life comes from the breaker's remaining open time, not from the
/// table row that status would otherwise pick.
/// </remarks>
public enum SevenTvLeaderboardFillOutcomeKind
{
    /// <summary>Every page needed came back <c>Ok</c>. An empty list counts as a hit.</summary>
    Hit,

    /// <summary>A confirmed 7TV rate limit — HTTP 429, or HTTP 200 with <c>extensions.status: 429</c>.</summary>
    SevenTvRateLimited,

    /// <summary>Any other upstream failure: 5xx, timeout, unparseable body.</summary>
    SevenTvUnavailable,

    /// <summary>The breaker was open, so no upstream request was made at all.</summary>
    BreakerOpen,

    /// <summary>The window budget refused a page, so no upstream request was made at all.</summary>
    BudgetRefused
}

/// <param name="Kind">Which row of the shelf-life table applies.</param>
/// <param name="Hint">
/// The duration the outcome itself suggested, where it has one: 7TV's <c>Retry-After</c> for
/// <see cref="SevenTvLeaderboardFillOutcomeKind.SevenTvRateLimited"/> (absent when 7TV said
/// nothing), and the breaker's remaining open time for
/// <see cref="SevenTvLeaderboardFillOutcomeKind.BreakerOpen"/>. Ignored for the fixed rows.
/// </param>
public readonly record struct SevenTvLeaderboardFillOutcome(
    SevenTvLeaderboardFillOutcomeKind Kind, TimeSpan? Hint)
{
    /// <summary>Every page came back <c>Ok</c> — including a genuinely empty list.</summary>
    public static SevenTvLeaderboardFillOutcome Hit() =>
        new(SevenTvLeaderboardFillOutcomeKind.Hit, null);

    /// <param name="retryAfter">7TV's own hint, or <c>null</c> when it sent none.</param>
    public static SevenTvLeaderboardFillOutcome RateLimited(TimeSpan? retryAfter) =>
        new(SevenTvLeaderboardFillOutcomeKind.SevenTvRateLimited, retryAfter);

    /// <summary>5xx, timeout or unparseable body.</summary>
    public static SevenTvLeaderboardFillOutcome Unavailable() =>
        new(SevenTvLeaderboardFillOutcomeKind.SevenTvUnavailable, null);

    /// <param name="remainingOpenTime">What the breaker reported with its rejection.</param>
    public static SevenTvLeaderboardFillOutcome BreakerOpen(TimeSpan remainingOpenTime) =>
        new(SevenTvLeaderboardFillOutcomeKind.BreakerOpen, remainingOpenTime);

    /// <summary>The window budget refused page 1 or page 2.</summary>
    public static SevenTvLeaderboardFillOutcome BudgetRefused() =>
        new(SevenTvLeaderboardFillOutcomeKind.BudgetRefused, null);
}

/// <summary>
/// The shelf-life rule for one leaderboard stock entry (spec 2026-09-13, section 6, "Fehler werden
/// negativ gecacht, nicht entfernt") — outcome in, duration out, no clock, no state, no I/O. Same
/// shape as the pure policies in <c>EmotePurge.Worker</c>; the clock lives in
/// <see cref="SevenTvLeaderboardStore{TValue}"/>, which turns this duration into an expiry the
/// moment the fill finishes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failures get a shelf-life too, and that is the point.</b> Dropping a failed entry would let a
/// permanently failing sort key start a fresh fill on every single click; the window budget would
/// then be the only thing between a bad afternoon and ten upstream requests an hour. Each failure
/// row is therefore a floor on how often that key may try again.
/// </para>
/// <para>
/// <b>Why two clamps rather than trust.</b> Both hints come from outside this process — 7TV's
/// <c>Retry-After</c> and the breaker's own remaining time — and an unclamped hint is a way for the
/// outside to set our retry rate. The lower clamps keep a tiny or absent hint from turning into a
/// retry storm; the upper clamp of one hour keeps an implausible hint from freezing a sort key for
/// the rest of the process's life, and matches the hit shelf-life, which is the longest this
/// feature ever holds anything.
/// </para>
/// </remarks>
public static class SevenTvLeaderboardTtlPolicy
{
    /// <summary>Shelf-life of a complete fill, empty list included.</summary>
    public static readonly TimeSpan HitTimeToLive = TimeSpan.FromHours(1);

    /// <summary>Lower clamp for a rate limit, and the shelf-life when 7TV sent no hint at all.</summary>
    public static readonly TimeSpan RateLimitedMinimum = TimeSpan.FromSeconds(60);

    /// <summary>Upper clamp for a rate limit.</summary>
    public static readonly TimeSpan RateLimitedMaximum = TimeSpan.FromHours(1);

    /// <summary>Shelf-life of a 5xx, a timeout or an unparseable body.</summary>
    public static readonly TimeSpan UnavailableTimeToLive = TimeSpan.FromSeconds(60);

    /// <summary>Lower clamp for an open breaker — never zero, or the next reader retries at once.</summary>
    public static readonly TimeSpan BreakerOpenMinimum = TimeSpan.FromSeconds(1);

    /// <summary>Upper clamp for an open breaker.</summary>
    public static readonly TimeSpan BreakerOpenMaximum = TimeSpan.FromHours(1);

    /// <summary>Shelf-life of a budget refusal — short, because nothing upstream went wrong.</summary>
    public static readonly TimeSpan BudgetRefusedTimeToLive = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The shelf-life the stock entry produced by <paramref name="outcome"/> must carry.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The outcome carries a kind this rule does not know. Thrown rather than defaulted: a new
    /// outcome silently inheriting somebody else's shelf-life is exactly the kind of drift the
    /// window budget would have to pay for.
    /// </exception>
    public static TimeSpan TimeToLiveFor(SevenTvLeaderboardFillOutcome outcome) => outcome.Kind switch
    {
        SevenTvLeaderboardFillOutcomeKind.Hit => HitTimeToLive,
        SevenTvLeaderboardFillOutcomeKind.SevenTvRateLimited => outcome.Hint is { } retryAfter
            ? Clamp(retryAfter, RateLimitedMinimum, RateLimitedMaximum)
            : RateLimitedMinimum,
        SevenTvLeaderboardFillOutcomeKind.SevenTvUnavailable => UnavailableTimeToLive,
        SevenTvLeaderboardFillOutcomeKind.BreakerOpen => Clamp(
            outcome.Hint ?? TimeSpan.Zero, BreakerOpenMinimum, BreakerOpenMaximum),
        SevenTvLeaderboardFillOutcomeKind.BudgetRefused => BudgetRefusedTimeToLive,
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome.Kind, "Unknown leaderboard fill outcome.")
    };

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;
}
