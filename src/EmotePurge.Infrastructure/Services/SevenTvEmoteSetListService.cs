using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The emote-set list of a 7TV account behind the full guard chain of spec 2026-09-20, 6.1 — cache,
/// single-flight, circuit breaker, provider budget, request, in that order and with the same
/// collaborators the foreign-channel preview uses (<c>HardenedForeignEmoteSetService</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the whole chain and not just a cache.</b> A cache and a permit are not enough: without
/// coalescing, concurrent cold misses all reach v4 (a picker opened by ten people at once costs ten
/// times what it should, because a cache only fills once the first call has <i>finished</i>);
/// without a breaker, every call repeats the same failure; and a v4 outage on this path can drain
/// the <i>shared</i> 60-per-minute budget and push the foreign-channel preview into 503 with it.
/// </para>
/// <para>
/// <b>One instance of each collaborator, two of them shared with the preview.</b> The budget is the
/// same object in both of its faces (concurrency slot here, request permit inside the client), and
/// the breaker is the same instance — but consulted under this path's own operation name, so a
/// malformed list query cannot accumulate five failures against the preview's counter or take the
/// probe the preview needs (F17, AK 94). The coalescer is this path's own closed type: two paths
/// keying the same table would coalesce a list request onto a preview result.
/// </para>
/// <para>
/// <b>Negative outcomes are cached, not retried.</b> Each one gets its own short shelf-life in the
/// same key space (6.1, after the shape of <c>SevenTvLeaderboardTtlPolicy</c>). Without that, every
/// reopened dialog during an outage asks 7TV again — and the outage is exactly when the budget is
/// least able to afford it. <c>NoSevenTvAccount</c> is not a failure and is held like a hit.
/// </para>
/// <para>
/// <b>The shared work is nobody's request.</b> Like the preview and the leaderboard, the coalesced
/// execution runs under <see cref="CancellationToken.None"/> while each caller waits under its own
/// token: one browser navigating away must not cancel the lookup everyone else is waiting on.
/// </para>
/// </remarks>
public sealed class SevenTvEmoteSetListService(
    ISevenTvApiClient client,
    ISevenTvEmoteSetListCache cache,
    ForeignEmoteSetRequestCoalescer<EmoteSetListResult> coalescer,
    ForeignSevenTvBreakerPolicy breaker,
    ForeignEmoteSetProviderBudget budget,
    ILogger<SevenTvEmoteSetListService> logger) : ISevenTvEmoteSetListService
{
    /// <summary>7TV's own spelling of a personal set's <c>EmoteSetKind</c> (E7, measured 2026-09-20).</summary>
    private const string PersonalKind = "PERSONAL";

    /// <summary>Shelf-life of an answer — a hit, an empty list and <c>NoSevenTvAccount</c> alike (E12).</summary>
    public static readonly TimeSpan AnswerTimeToLive = TimeSpan.FromSeconds(60);

    /// <summary>Shelf-life of a transport failure, a 5xx or an unreadable body.</summary>
    public static readonly TimeSpan UnavailableTimeToLive = TimeSpan.FromSeconds(60);

    /// <summary>Lower clamp for a rate limit, and the shelf-life when 7TV sent no hint at all.</summary>
    public static readonly TimeSpan RateLimitedMinimum = TimeSpan.FromSeconds(60);

    /// <summary>Upper clamp for a rate limit — an implausible hint must not freeze an account for a day.</summary>
    public static readonly TimeSpan RateLimitedMaximum = TimeSpan.FromHours(1);

    /// <summary>Lower clamp for an open breaker — never zero, or the next caller retries at once.</summary>
    public static readonly TimeSpan BreakerOpenMinimum = TimeSpan.FromSeconds(1);

    /// <summary>Upper clamp for an open breaker.</summary>
    public static readonly TimeSpan BreakerOpenMaximum = TimeSpan.FromHours(1);

    /// <summary>Shelf-life of a refused permit — short, because nothing upstream went wrong.</summary>
    public static readonly TimeSpan BudgetRefusedTimeToLive = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a caller waits for one of the provider's concurrency slots before giving up. The
    /// same five seconds the preview decorator waits, and for the same reason — long enough that a
    /// passing burst resolves inside it, short enough that opening a dialog never feels hung.
    /// </summary>
    private static readonly TimeSpan BudgetWaitTimeout = TimeSpan.FromSeconds(5);

    public async Task<EmoteSetListResult> ListByTwitchIdAsync(
        string twitchChannelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(twitchChannelId);

        var cached = await cache.TryGetAsync(twitchChannelId, cancellationToken);
        if (cached is not null)
        {
            // A hit short-circuits everything below, including the coalescing key: a held negative
            // outcome is an answer too, and re-entering the chain for it is the retry storm the
            // shelf-life exists to prevent.
            return cached;
        }

        return await coalescer.CoalesceAsync(
            twitchChannelId,
            () => ExecuteGuardedAsync(twitchChannelId, CancellationToken.None),
            cancellationToken);
    }

    private async Task<EmoteSetListResult> ExecuteGuardedAsync(string twitchChannelId, CancellationToken cancellationToken)
    {
        var decision = breaker.TryAcquire(ForeignSevenTvBreakerOperations.EmoteSetList);
        if (!decision.Allowed)
        {
            // Debug, not louder: this fires on every rejected call while the breaker is open, and
            // the one event worth a real line — the opening — is logged once, below.
            logger.LogDebug(
                "7TV emote-set list for Twitch id {TwitchId}: circuit breaker open, no upstream request (remaining open time {RemainingSeconds}s).",
                twitchChannelId, Math.Ceiling(decision.RemainingOpenTime.TotalSeconds));

            return await HoldAsync(
                twitchChannelId,
                EmoteSetListResult.Failed(
                    decision.OpenedByRateLimit ? EmoteSetListStatus.RateLimited : EmoteSetListStatus.Unavailable),
                Clamp(decision.RemainingOpenTime, BreakerOpenMinimum, BreakerOpenMaximum),
                cancellationToken);
        }

        var breakerResolved = false;
        IDisposable? slot = null;
        try
        {
            slot = await budget.TryAcquireConcurrencySlotAsync(BudgetWaitTimeout, cancellationToken);
            if (slot is null)
            {
                logger.LogWarning(
                    "7TV emote-set list for Twitch id {TwitchId}: provider-wide budget unavailable after {TimeoutSeconds}s.",
                    twitchChannelId, BudgetWaitTimeout.TotalSeconds);

                // Nothing reached 7TV, so the breaker learns nothing about its health — but the
                // probe slot this decision may have taken still has to go back, or the breaker jams.
                breaker.ReleaseProbeWithoutOutcome(ForeignSevenTvBreakerOperations.EmoteSetList, decision.Generation);
                breakerResolved = true;
                return await HoldAsync(
                    twitchChannelId,
                    EmoteSetListResult.Failed(EmoteSetListStatus.BudgetExhausted),
                    BudgetRefusedTimeToLive,
                    cancellationToken);
            }

            var upstream = await client.GetEmoteSetListForTwitchUserAsync(twitchChannelId, cancellationToken);
            LogBreakerTransition(twitchChannelId, upstream.Status, ApplyBreakerFeedback(upstream, decision.Generation));
            breakerResolved = true;

            return await HoldAsync(twitchChannelId, Map(upstream), TimeToLiveFor(upstream), cancellationToken);
        }
        finally
        {
            if (!breakerResolved)
            {
                // Backstop for a genuinely unexpected exception (cancellation included) that skipped
                // every resolution path above — counted as a plain failure, never a rate limit:
                // whatever happened here is not something 7TV told us.
                breaker.RecordFailure(
                    ForeignSevenTvBreakerOperations.EmoteSetList,
                    ForeignSevenTvBreakerOutcome.OtherFailure,
                    null,
                    decision.Generation);
            }

            slot?.Dispose();
        }
    }

    private async Task<EmoteSetListResult> HoldAsync(
        string twitchChannelId, EmoteSetListResult result, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        await cache.SetAsync(twitchChannelId, result, timeToLive, cancellationToken);
        return result;
    }

    /// <summary>
    /// Feeds one outcome back into the breaker — exactly once per allowed decision.
    /// <c>Ok</c> and <c>NoSevenTvAccount</c> both required a real 7TV answer to produce, so both are
    /// evidence the provider is healthy; "this account does not exist" is an answer, not a failure.
    /// <c>BudgetExhausted</c> is our own throttle and says nothing either way, so it releases the
    /// probe instead of reporting health.
    /// </summary>
    private ForeignSevenTvBreakerTransition ApplyBreakerFeedback(
        SevenTvEmoteSetListResult upstream, long generation) => upstream.Status switch
        {
            SevenTvEmoteSetListLookupStatus.Ok or SevenTvEmoteSetListLookupStatus.NoSevenTvAccount =>
                breaker.RecordSuccess(ForeignSevenTvBreakerOperations.EmoteSetList, generation),
            SevenTvEmoteSetListLookupStatus.RateLimited => breaker.RecordFailure(
                ForeignSevenTvBreakerOperations.EmoteSetList,
                ForeignSevenTvBreakerOutcome.RateLimited,
                upstream.RetryAfter,
                generation),
            SevenTvEmoteSetListLookupStatus.Unavailable => breaker.RecordFailure(
                ForeignSevenTvBreakerOperations.EmoteSetList,
                ForeignSevenTvBreakerOutcome.OtherFailure,
                null,
                generation),
            SevenTvEmoteSetListLookupStatus.BudgetExhausted => ReleaseProbeAsNoEvidence(generation),
            _ => throw new ArgumentOutOfRangeException(
                nameof(upstream), upstream.Status, "Unknown SevenTvEmoteSetListLookupStatus.")
        };

    private ForeignSevenTvBreakerTransition ReleaseProbeAsNoEvidence(long generation)
    {
        breaker.ReleaseProbeWithoutOutcome(ForeignSevenTvBreakerOperations.EmoteSetList, generation);
        return ForeignSevenTvBreakerTransition.None;
    }

    private void LogBreakerTransition(
        string twitchChannelId, SevenTvEmoteSetListLookupStatus status, ForeignSevenTvBreakerTransition transition)
    {
        switch (transition)
        {
            case ForeignSevenTvBreakerTransition.Opened:
                logger.LogWarning(
                    "7TV circuit breaker opened for the emote-set list (triggered by Twitch id {TwitchId}, status {Status}).",
                    twitchChannelId, status);
                break;
            case ForeignSevenTvBreakerTransition.Closed:
                logger.LogInformation("7TV circuit breaker closed again for the emote-set list.");
                break;
            case ForeignSevenTvBreakerTransition.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(transition), transition, "Unknown ForeignSevenTvBreakerTransition.");
        }
    }

    private static EmoteSetListResult Map(SevenTvEmoteSetListResult upstream) => upstream.Status switch
    {
        SevenTvEmoteSetListLookupStatus.Ok => EmoteSetListResult.Ok(new EmoteSetList(
            upstream.Listing!.ActiveEmoteSetId,
            upstream.Listing.Sets.Select(ToSummary).ToList(),
            upstream.Listing.SevenTvUserId)),
        SevenTvEmoteSetListLookupStatus.NoSevenTvAccount => EmoteSetListResult.Failed(EmoteSetListStatus.NoSevenTvAccount),
        SevenTvEmoteSetListLookupStatus.RateLimited => EmoteSetListResult.Failed(EmoteSetListStatus.RateLimited),
        SevenTvEmoteSetListLookupStatus.Unavailable => EmoteSetListResult.Failed(EmoteSetListStatus.Unavailable),
        SevenTvEmoteSetListLookupStatus.BudgetExhausted => EmoteSetListResult.Failed(EmoteSetListStatus.BudgetExhausted),
        _ => throw new ArgumentOutOfRangeException(
            nameof(upstream), upstream.Status, "Unknown SevenTvEmoteSetListLookupStatus.")
    };

    private static EmoteSetSummary ToSummary(SevenTvEmoteSetListEntry entry) => new(
        entry.Id,
        entry.Name,
        entry.Capacity,
        entry.Kind,
        // The only place the string is compared with anything, and only to pick a label: every kind
        // other than NORMAL is equally unselectable (8.6), so an unknown fifth kind needs no branch
        // here — it simply is not personal, and is not selectable either.
        string.Equals(entry.Kind, PersonalKind, StringComparison.Ordinal),
        entry.OwnerDisplayName,
        entry.OwnerSevenTvUserId);

    private static TimeSpan TimeToLiveFor(SevenTvEmoteSetListResult upstream) => upstream.Status switch
    {
        // An answer, empty list and "no 7TV account" included: both cost a request to learn, and
        // both are stable for the minute this cache covers.
        SevenTvEmoteSetListLookupStatus.Ok or SevenTvEmoteSetListLookupStatus.NoSevenTvAccount => AnswerTimeToLive,
        SevenTvEmoteSetListLookupStatus.RateLimited => upstream.RetryAfter is { } retryAfter
            ? Clamp(retryAfter, RateLimitedMinimum, RateLimitedMaximum)
            : RateLimitedMinimum,
        SevenTvEmoteSetListLookupStatus.Unavailable => UnavailableTimeToLive,
        SevenTvEmoteSetListLookupStatus.BudgetExhausted => BudgetRefusedTimeToLive,
        _ => throw new ArgumentOutOfRangeException(
            nameof(upstream), upstream.Status, "Unknown SevenTvEmoteSetListLookupStatus.")
    };

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum)
    {
        if (value < minimum)
        {
            return minimum;
        }

        return value > maximum ? maximum : value;
    }
}
