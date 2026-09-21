using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// <see cref="IGuardedSevenTvEditorGrantsService"/>: the grant cache first, then a hold of earlier
/// failures, then the provider guards of spec 6.1 in the list service's order — breaker (own
/// operation), concurrency slot, and inside the client one permit per upstream request.
/// </summary>
/// <remarks>
/// <para>
/// <b>A hit costs nothing.</b> The picker (<c>/me/emote-set-targets</c>) fills the grant cache
/// minutes before the report, so the ordinary report never reaches this class's guarded half.
/// </para>
/// <para>
/// <b>A success is shared.</b> It is written to the same <c>7tveditor:</c> entry, in the same shape
/// and with the same TTL, as <see cref="SevenTvEditorService"/> writes — the next reader on any path
/// profits from it. A failure is not: it goes into a hold of its own
/// (<see cref="ISevenTvEditorGrantsHoldCache"/>), which only this class reads.
/// </para>
/// <para>
/// <b>Failures are held, not retried</b>, with the shelf-lives of spec 6.1: 60 s for an unavailable
/// answer, at least 60 s or 7TV's own <c>Retry-After</c> for a confirmed rate limit, 30 s for a
/// refused permit or slot and for an open breaker. A report inside that window sends nothing.
/// <c>NoSevenTvAccount</c> is an answer and is held like one, for 60 s.
/// </para>
/// <para>
/// <b>Checked twice.</b> Both caches are asked again once the slot is held: reports that queued
/// behind a slow first attempt find its answer instead of repeating it. That is what bounds a burst
/// of concurrent reports to the two slots, where a sequence of them is already bounded by the hold.
/// </para>
/// </remarks>
public sealed class GuardedSevenTvEditorGrantsService(
    ISevenTvApiClient client,
    IModRoleCache grantsCache,
    ISevenTvEditorGrantsHoldCache holds,
    ForeignSevenTvBreakerPolicy breaker,
    ForeignEmoteSetProviderBudget budget,
    IRateLimitTelemetry telemetry,
    ILogger<GuardedSevenTvEditorGrantsService> logger) : IGuardedSevenTvEditorGrantsService
{
    /// <summary>Shelf-life of <c>NoSevenTvAccount</c> — an answer, held like the lists hold one.</summary>
    public static readonly TimeSpan AnswerTimeToLive = TimeSpan.FromSeconds(60);

    /// <summary>Shelf-life of a transport failure, a 5xx or an unusable body.</summary>
    public static readonly TimeSpan UnavailableTimeToLive = TimeSpan.FromSeconds(60);

    /// <summary>Lower clamp for a rate limit, and the shelf-life when 7TV sent no hint at all.</summary>
    public static readonly TimeSpan RateLimitedMinimum = TimeSpan.FromSeconds(60);

    /// <summary>Upper clamp for a rate limit — an implausible hint must not freeze a user for a day.</summary>
    public static readonly TimeSpan RateLimitedMaximum = TimeSpan.FromHours(1);

    /// <summary>Shelf-life of a refused permit or slot, and of an open breaker — nothing upstream went wrong.</summary>
    public static readonly TimeSpan GuardRefusedTimeToLive = TimeSpan.FromSeconds(30);

    /// <summary>The same concurrency-slot wait the list, the preview and the owner lookup use.</summary>
    private static readonly TimeSpan BudgetWaitTimeout = TimeSpan.FromSeconds(5);

    public async Task<SevenTvEditorGrantsLookupResult> GetEditorGrantsAsync(
        string twitchUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(twitchUserId);

        var cached = await grantsCache.TryGetSevenTvEditorGrantsAsync(twitchUserId, cancellationToken);
        telemetry.RecordCacheLookup(RateLimitCacheNames.SevenTvGrants, cached is not null);
        if (cached is not null)
        {
            return SevenTvEditorGrantsLookupResult.Ok(cached);
        }

        if (await holds.TryGetAsync(twitchUserId, cancellationToken) is { } held)
        {
            return SevenTvEditorGrantsLookupResult.Failed(held);
        }

        return await RefreshGuardedAsync(twitchUserId, cancellationToken);
    }

    private async Task<SevenTvEditorGrantsLookupResult> RefreshGuardedAsync(string twitchUserId, CancellationToken cancellationToken)
    {
        var decision = breaker.TryAcquire(ForeignSevenTvBreakerOperations.EditorGrants);
        if (!decision.Allowed)
        {
            logger.LogDebug(
                "7TV editor grants of {UserId}: circuit breaker open, no upstream request.", twitchUserId);
            return await HoldAsync(twitchUserId, SevenTvLookupStatus.Unavailable, GuardRefusedTimeToLive, cancellationToken);
        }

        var breakerResolved = false;
        IDisposable? slot = null;
        try
        {
            slot = await budget.TryAcquireConcurrencySlotAsync(BudgetWaitTimeout, cancellationToken);
            if (slot is null)
            {
                logger.LogWarning(
                    "7TV editor grants of {UserId}: provider-wide budget unavailable after {TimeoutSeconds}s.",
                    twitchUserId, BudgetWaitTimeout.TotalSeconds);
                breaker.ReleaseProbeWithoutOutcome(ForeignSevenTvBreakerOperations.EditorGrants, decision.Generation);
                breakerResolved = true;
                return await HoldAsync(twitchUserId, SevenTvLookupStatus.Unavailable, GuardRefusedTimeToLive, cancellationToken);
            }

            if (await AnsweredMeanwhileAsync(twitchUserId, cancellationToken) is { } meanwhile)
            {
                breaker.ReleaseProbeWithoutOutcome(ForeignSevenTvBreakerOperations.EditorGrants, decision.Generation);
                breakerResolved = true;
                return meanwhile;
            }

            var upstream = await client.LookUpEditorGrantsAsync(twitchUserId, cancellationToken);
            LogBreakerTransition(twitchUserId, upstream.Status, ApplyBreakerFeedback(upstream, decision.Generation));
            breakerResolved = true;

            return await ConcludeAsync(twitchUserId, upstream, cancellationToken);
        }
        finally
        {
            if (!breakerResolved)
            {
                // Backstop for an unexpected exception (cancellation included) — a plain failure,
                // never a rate limit: nothing here is something 7TV told us.
                breaker.RecordFailure(
                    ForeignSevenTvBreakerOperations.EditorGrants,
                    ForeignSevenTvBreakerOutcome.OtherFailure,
                    null,
                    decision.Generation);
            }

            slot?.Dispose();
        }
    }

    // The second look, once the slot is held: a report that queued behind another one for the same
    // user takes that one's answer, success or held failure, instead of asking 7TV again.
    private async Task<SevenTvEditorGrantsLookupResult?> AnsweredMeanwhileAsync(string twitchUserId, CancellationToken cancellationToken)
    {
        if (await grantsCache.TryGetSevenTvEditorGrantsAsync(twitchUserId, cancellationToken) is { } cached)
        {
            return SevenTvEditorGrantsLookupResult.Ok(cached);
        }

        return await holds.TryGetAsync(twitchUserId, cancellationToken) is { } held
            ? SevenTvEditorGrantsLookupResult.Failed(held)
            : null;
    }

    private async Task<SevenTvEditorGrantsLookupResult> ConcludeAsync(
        string twitchUserId, SevenTvEditorGrantsLookup upstream, CancellationToken cancellationToken)
    {
        switch (upstream.Status)
        {
            case SevenTvEditorGrantsLookupStatus.Ok:
                var grants = SevenTvEditorService.BuildGrants(upstream.Grants!);
                await grantsCache.SetSevenTvEditorGrantsAsync(twitchUserId, grants, cancellationToken);
                return SevenTvEditorGrantsLookupResult.Ok(grants);
            case SevenTvEditorGrantsLookupStatus.NoSevenTvAccount:
                return await HoldAsync(twitchUserId, SevenTvLookupStatus.NoSevenTvAccount, AnswerTimeToLive, cancellationToken);
            case SevenTvEditorGrantsLookupStatus.RateLimited:
                return await HoldAsync(
                    twitchUserId,
                    SevenTvLookupStatus.Unavailable,
                    upstream.RetryAfter is { } retryAfter ? Clamp(retryAfter, RateLimitedMinimum, RateLimitedMaximum) : RateLimitedMinimum,
                    cancellationToken);
            case SevenTvEditorGrantsLookupStatus.Unavailable:
                return await HoldAsync(twitchUserId, SevenTvLookupStatus.Unavailable, UnavailableTimeToLive, cancellationToken);
            case SevenTvEditorGrantsLookupStatus.BudgetExhausted:
                return await HoldAsync(twitchUserId, SevenTvLookupStatus.Unavailable, GuardRefusedTimeToLive, cancellationToken);
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(upstream), upstream.Status, "Unknown SevenTvEditorGrantsLookupStatus.");
        }
    }

    private async Task<SevenTvEditorGrantsLookupResult> HoldAsync(
        string twitchUserId, SevenTvLookupStatus status, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        await holds.SetAsync(twitchUserId, status, timeToLive, cancellationToken);
        return SevenTvEditorGrantsLookupResult.Failed(status);
    }

    /// <summary>
    /// Exactly one outcome per allowed decision. <c>Ok</c> and <c>NoSevenTvAccount</c> both needed a
    /// real 7TV answer, so both are health evidence; <c>BudgetExhausted</c> is our own throttle and
    /// releases the probe instead — even when the identity request before it went through.
    /// </summary>
    private ForeignSevenTvBreakerTransition ApplyBreakerFeedback(SevenTvEditorGrantsLookup upstream, long generation)
    {
        switch (upstream.Status)
        {
            case SevenTvEditorGrantsLookupStatus.Ok:
            case SevenTvEditorGrantsLookupStatus.NoSevenTvAccount:
                return breaker.RecordSuccess(ForeignSevenTvBreakerOperations.EditorGrants, generation);
            case SevenTvEditorGrantsLookupStatus.RateLimited:
                return breaker.RecordFailure(
                    ForeignSevenTvBreakerOperations.EditorGrants,
                    ForeignSevenTvBreakerOutcome.RateLimited,
                    upstream.RetryAfter,
                    generation);
            case SevenTvEditorGrantsLookupStatus.Unavailable:
                return breaker.RecordFailure(
                    ForeignSevenTvBreakerOperations.EditorGrants,
                    ForeignSevenTvBreakerOutcome.OtherFailure,
                    null,
                    generation);
            case SevenTvEditorGrantsLookupStatus.BudgetExhausted:
                breaker.ReleaseProbeWithoutOutcome(ForeignSevenTvBreakerOperations.EditorGrants, generation);
                return ForeignSevenTvBreakerTransition.None;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(upstream), upstream.Status, "Unknown SevenTvEditorGrantsLookupStatus.");
        }
    }

    private void LogBreakerTransition(
        string twitchUserId, SevenTvEditorGrantsLookupStatus status, ForeignSevenTvBreakerTransition transition)
    {
        switch (transition)
        {
            case ForeignSevenTvBreakerTransition.Opened:
                logger.LogWarning(
                    "7TV circuit breaker opened for the guarded editor-grant refresh (triggered by {UserId}, status {Status}).",
                    twitchUserId, status);
                break;
            case ForeignSevenTvBreakerTransition.Closed:
                logger.LogInformation("7TV circuit breaker closed again for the guarded editor-grant refresh.");
                break;
            case ForeignSevenTvBreakerTransition.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(transition), transition, "Unknown ForeignSevenTvBreakerTransition.");
        }
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum)
    {
        if (value < minimum)
        {
            return minimum;
        }

        return value > maximum ? maximum : value;
    }
}
