using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// The hardening decorator around the foreign-channel-import preview (spec 2026-09-09, T2/section 6):
/// wraps the raw <see cref="ForeignEmoteSetService"/> resolution chain with the 60 s cache (E3),
/// request coalescing, the 429-aware circuit breaker (E4) and the provider-wide budget (E5b) — in
/// that order, one process-wide instance of each collaborator shared across every request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order of guards.</b> The cache is checked first and, on a hit, short-circuits everything below
/// it — no coalescing key is even taken. A miss (or <c>refresh: true</c>, which always skips the
/// cache) enters the coalescer, so concurrent identical lookups share one execution of the breaker
/// and budget gate below rather than each consulting them separately. Only inside that shared
/// execution does the breaker decide whether to allow an attempt at all, and only if it does does the
/// budget gate wait for one of the two concurrency slots before the raw chain is actually invoked.
/// </para>
/// <para>
/// <b>The other half of the budget is not taken here.</b> This decorator admits whole lookups
/// (<see cref="ForeignEmoteSetProviderBudget.TryAcquireConcurrencySlotAsync"/>); the rolling
/// 60-requests-a-minute limit is charged one permit at a time by the steps that actually issue an
/// upstream request, because a single lookup issues up to twelve of them. Taking both here would make
/// one permit stand for a whole resolution — the shape Codex found, and the reason this feature could
/// have made up to 720 upstream requests a minute against a limit of 60.
/// </para>
/// <para>
/// <b>Every reachable path through <see cref="ExecuteGuardedAsync"/> resolves the breaker's probe
/// state exactly once</b> whenever <see cref="ForeignSevenTvBreakerPolicy.TryAcquire"/> returned
/// <c>Allowed: true</c> — via <see cref="ForeignSevenTvBreakerPolicy.RecordSuccess"/>,
/// <see cref="ForeignSevenTvBreakerPolicy.RecordFailure"/>, or, for an outcome that says nothing
/// about 7TV's health (a budget timeout, or a lookup that never reached 7TV in the first place — see
/// <see cref="ApplyBreakerFeedback"/>), <see cref="ForeignSevenTvBreakerPolicy.ReleaseProbeWithoutOutcome"/>.
/// The <c>finally</c> block is the backstop for anything unexpected that still manages to skip all of
/// those — without it, a single truly unforeseen exception during the probe's one allowed attempt
/// would leave the breaker stuck open forever, since no later caller is ever let through to prove it
/// recovered.
/// </para>
/// </remarks>
public sealed class HardenedForeignEmoteSetService(
    IForeignEmoteSetService inner,
    IForeignEmoteSetCache cache,
    ForeignEmoteSetRequestCoalescer coalescer,
    ForeignSevenTvBreakerPolicy breaker,
    ForeignEmoteSetProviderBudget budget,
    IRateLimitTelemetry telemetry,
    ILogger<HardenedForeignEmoteSetService> logger) : IForeignEmoteSetService
{
    /// <summary>
    /// How long a caller waits for the provider-wide budget (E5b) before giving up and answering
    /// "unavailable" instead of continuing to wait.
    /// </summary>
    /// <remarks>
    /// The spec leaves this value open deliberately (section 6, "offener Punkt"). Chosen at 5 s:
    /// generous relative to the measured typical cost of one foreign-set fetch (0.72 s for a
    /// 956-emote set against HandOfBlood's real set, section 6 of the spec) — several times over, so
    /// a passing burst of up to two other concurrent lookups clearing the concurrency gate ahead of a
    /// third caller resolves well inside the window in the overwhelmingly common case — while still
    /// staying short enough that opening the import dialog does not feel hung to someone who lands on
    /// the losing side of a genuine spike. It intentionally does *not* try to cover the pathological
    /// worst case (up to ten sequential paginated pages at the client's own 10 s HTTP timeout each);
    /// a wait that long would defeat the point of failing fast at all. If live use shows 5 s rejects
    /// too eagerly during normal traffic, or too rarely to matter, this is the one number to revisit
    /// — nothing else in this class assumes a particular value.
    /// </remarks>
    private static readonly TimeSpan BudgetWaitTimeout = TimeSpan.FromSeconds(5);

    public async Task<ForeignEmoteSetLookupResult> GetForeignEmoteSetAsync(
        string channelName, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        if (!refresh)
        {
            var cached = await cache.TryGetAsync(normalized, cancellationToken);
            telemetry.RecordCacheLookup(RateLimitCacheNames.ForeignEmoteSet, hit: cached is not null);
            if (cached is not null)
            {
                return ForeignEmoteSetLookupResult.Ok(cached);
            }
        }

        // Coalesced regardless of refresh: a plain cache-miss lookup and a forced refresh for the
        // same channel run the identical chain, so sharing whichever of the two happened to start
        // first is correct either way — not just permitted.
        //
        // The shared execution deliberately runs under CancellationToken.None instead of this
        // caller's token: whoever happens to arrive first is not the owner of the work every later
        // caller is waiting on, and one client aborting its request must not cancel the lookup for
        // the rest. This caller's own token is handed to the coalescer, which applies it to this
        // caller's wait alone. The abandoned work still completes and still fills the cache.
        return await coalescer.CoalesceAsync(
            normalized,
            () => ExecuteGuardedAsync(
                normalized,
                () => inner.GetForeignEmoteSetAsync(normalized, refresh: false, CancellationToken.None),
                emoteSet => cache.SetAsync(normalized, emoteSet, CancellationToken.None)),
            cancellationToken);
    }

    public async Task<ForeignEmoteSetLookupResult> GetForeignEmoteSetBySetIdAsync(
        string channelName, string emoteSetId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var normalizedChannel = ChannelName.Normalize(channelName);

        if (!refresh)
        {
            var cached = await cache.TryGetBySetIdAsync(emoteSetId, cancellationToken);
            telemetry.RecordCacheLookup(RateLimitCacheNames.ForeignEmoteSetBySetId, hit: cached is not null);
            if (cached is not null)
            {
                return EchoRouteChannel(ForeignEmoteSetLookupResult.Ok(cached), normalizedChannel);
            }
        }

        // Coalescing key "set:{id}" (spec E12) — deliberately its own namespace within the shared
        // coalescer, distinct from the bare normalized login GetForeignEmoteSetAsync above uses as
        // its key. A Twitch login can never contain a colon, so the two key spaces are disjoint by
        // construction: a set-ID lookup for X and a login lookup that happens to resolve to the same
        // account's active set never share one in-flight entry, and neither ever coalesces onto the
        // other's cache write (AK 26, spec 19's Prüfaufgabe).
        var coalesceKey = $"set:{emoteSetId}";
        var result = await coalescer.CoalesceAsync(
            coalesceKey,
            () => ExecuteGuardedAsync(
                emoteSetId,
                () => inner.GetForeignEmoteSetBySetIdAsync(normalizedChannel, emoteSetId, refresh: false, CancellationToken.None),
                emoteSet => cache.SetBySetIdAsync(emoteSetId, emoteSet, CancellationToken.None)),
            cancellationToken);

        return EchoRouteChannel(result, normalizedChannel);
    }

    /// <summary>
    /// The K3 source-set list (spec 2026-09-20, 6.3) — a direct pass-through to <c>inner</c>, with
    /// none of this decorator's own cache, coalescer or breaker wrapped around it. That is
    /// deliberate, not an oversight: <see cref="IForeignEmoteSetService.GetForeignEmoteSetListAsync"/>'s
    /// entire 7TV-facing half already runs behind <see cref="ISevenTvEmoteSetListService"/>'s own full
    /// guard chain (spec 6.1, "Härtung des Listen-Dienstes" — cache, single-flight, breaker under its
    /// own <c>EmoteSetList</c> operation, provider budget), which this class's collaborators know
    /// nothing about. Wrapping it a second time here would not harden it further: the cache holds the
    /// wrong payload shape for this method entirely, and layering this decorator's
    /// <see cref="ForeignSevenTvBreakerOperations.ForeignPreview"/> breaker on top would let an
    /// unrelated preview failure spuriously reject a healthy list call (and vice versa) — exactly the
    /// cross-contamination 6.1's per-operation breaker split exists to prevent.
    /// </summary>
    public Task<ForeignEmoteSetListLookupResult> GetForeignEmoteSetListAsync(
        string channelName, CancellationToken cancellationToken = default) =>
        inner.GetForeignEmoteSetListAsync(channelName, cancellationToken);

    /// <summary>
    /// Set-ID cache entries and coalesced executions are shared across every channel that happens to
    /// ask about the same set (cache key <c>7tvforeign:set:{setId}</c>, coalescing key
    /// <c>set:{setId}</c> — both deliberately channel-free, spec E12) — so a reused entry carries
    /// whichever caller's <see cref="ForeignEmoteSet.ChannelName"/> happened to populate it, not this
    /// caller's route channel. <see cref="IForeignEmoteSetService.GetForeignEmoteSetBySetIdAsync"/>'s
    /// contract is that <c>ChannelName</c> always echoes the current route (spec 6.4), so every reuse
    /// — a cache hit and a coalesced miss alike — corrects it here before the result leaves this
    /// method. A first, uncoalesced miss for a set nobody else is asking about needs no correction
    /// (the inner chain already echoed this same caller's channel), which is why this stays a no-op
    /// rather than an unconditional allocation.
    /// </summary>
    private static ForeignEmoteSetLookupResult EchoRouteChannel(ForeignEmoteSetLookupResult result, string normalizedChannel)
    {
        if (result.Status != ForeignEmoteSetLookupStatus.Ok || result.EmoteSet!.ChannelName == normalizedChannel)
        {
            return result;
        }

        return ForeignEmoteSetLookupResult.Ok(result.EmoteSet with { ChannelName = normalizedChannel });
    }

    /// <summary>
    /// The shared guarded execution behind both public methods: breaker, then the provider-wide
    /// concurrency budget, then the caller-supplied <paramref name="resolve"/> chain, then (on
    /// success) <paramref name="writeCache"/>. Both read modes share this — and, with it, the same
    /// <see cref="ForeignSevenTvBreakerOperations.ForeignPreview"/> operation and the same budget —
    /// because F6 (spec 2026-09-20) is explicit that hardening "wie heute" applies to the set-ID mode
    /// too: it is the same 7TV bucket and the same preview query shape, just without the two
    /// resolution calls in front of it.
    /// </summary>
    /// <param name="logIdentifier">
    /// What log lines below name — the normalized channel for the login mode, the set id for the
    /// set-ID mode (which has no identity to resolve and so nothing else to log).
    /// </param>
    private async Task<ForeignEmoteSetLookupResult> ExecuteGuardedAsync(
        string logIdentifier,
        Func<Task<ForeignEmoteSetLookupResult>> resolve,
        Func<ForeignEmoteSet, Task> writeCache)
    {
        var decision = breaker.TryAcquire(ForeignSevenTvBreakerOperations.ForeignPreview);
        if (!decision.Allowed)
        {
            // Deliberately not louder than Debug: this fires on every rejected request while the
            // breaker is open, and the one event worth a real log line — the breaker actually opening
            // — is logged exactly once, below, at the point the transition happens.
            logger.LogDebug(
                "Fremdkanal-Vorschau für {Identifier}: Circuit-Breaker offen, kein Upstream-Aufruf (verbleibende Offenzeit {RemainingSeconds}s).",
                logIdentifier, Math.Ceiling(decision.RemainingOpenTime.TotalSeconds));
            return ForeignEmoteSetLookupResult.Failed(
                decision.OpenedByRateLimit
                    ? ForeignEmoteSetLookupStatus.SevenTvRateLimited
                    : ForeignEmoteSetLookupStatus.SevenTvUnavailable);
        }

        var breakerResolved = false;
        IDisposable? permit = null;
        try
        {
            permit = await budget.TryAcquireConcurrencySlotAsync(BudgetWaitTimeout, CancellationToken.None);
            if (permit is null)
            {
                logger.LogWarning(
                    "Fremdkanal-Vorschau für {Identifier}: providerweites 7TV-Budget nach {TimeoutSeconds}s Wartezeit nicht verfügbar.",
                    logIdentifier, BudgetWaitTimeout.TotalSeconds);
                // Never reached the inner chain — nothing to tell the breaker about 7TV's health, but
                // the probe slot (if this was one) still needs releasing.
                breaker.ReleaseProbeWithoutOutcome(ForeignSevenTvBreakerOperations.ForeignPreview, decision.Generation);
                breakerResolved = true;
                return ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.SevenTvUnavailable);
            }

            var result = await resolve();
            LogBreakerTransition(logIdentifier, result.Status, ApplyBreakerFeedback(result, decision.Generation));
            breakerResolved = true;

            if (result.Status == ForeignEmoteSetLookupStatus.Ok)
            {
                await writeCache(result.EmoteSet!);
            }

            return result;
        }
        finally
        {
            if (!breakerResolved)
            {
                // Backstop for a genuinely unexpected exception (including cancellation) that skipped
                // every other resolution path above — treated as a plain failure, never a rate limit:
                // whatever happened here is not something 7TV told us.
                breaker.RecordFailure(ForeignSevenTvBreakerOperations.ForeignPreview, ForeignSevenTvBreakerOutcome.OtherFailure, null, decision.Generation);
            }

            permit?.Dispose();
        }
    }

    /// <summary>
    /// Feeds one lookup outcome back into the breaker. <see cref="ForeignEmoteSetLookupStatus.Ok"/>,
    /// <see cref="ForeignEmoteSetLookupStatus.NoSevenTvAccount"/> and
    /// <see cref="ForeignEmoteSetLookupStatus.NoActiveEmoteSet"/> all required a real 7TV answer to
    /// produce, so all three count as evidence 7TV is healthy — "account not found" and "no active
    /// set" are legitimate answers, not failures. <see cref="ForeignEmoteSetLookupStatus.ChannelNotOnTwitch"/>
    /// and <see cref="ForeignEmoteSetLookupStatus.TwitchUnavailable"/> never reach 7TV at all — the
    /// breaker exists to protect 7TV specifically, so these say nothing about it either way, and
    /// neither does <see cref="ForeignEmoteSetLookupStatus.ProviderBudgetExhausted"/>, which is our
    /// own throttle refusing a permit rather than anything 7TV said.
    /// </summary>
    private ForeignSevenTvBreakerTransition ApplyBreakerFeedback(ForeignEmoteSetLookupResult result, long generation) => result.Status switch
    {
        ForeignEmoteSetLookupStatus.Ok
            or ForeignEmoteSetLookupStatus.NoSevenTvAccount
            or ForeignEmoteSetLookupStatus.NoActiveEmoteSet => breaker.RecordSuccess(ForeignSevenTvBreakerOperations.ForeignPreview, generation),
        ForeignEmoteSetLookupStatus.SevenTvRateLimited =>
            breaker.RecordFailure(ForeignSevenTvBreakerOperations.ForeignPreview, ForeignSevenTvBreakerOutcome.RateLimited, result.RetryAfter, generation),
        ForeignEmoteSetLookupStatus.SevenTvUnavailable =>
            breaker.RecordFailure(ForeignSevenTvBreakerOperations.ForeignPreview, ForeignSevenTvBreakerOutcome.OtherFailure, null, generation),
        ForeignEmoteSetLookupStatus.ChannelNotOnTwitch
            or ForeignEmoteSetLookupStatus.TwitchUnavailable
            or ForeignEmoteSetLookupStatus.ProviderBudgetExhausted =>
            ReleaseProbeAsNoEvidence(generation),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.Status, "Unbekannter ForeignEmoteSetLookupStatus.")
    };

    private ForeignSevenTvBreakerTransition ReleaseProbeAsNoEvidence(long generation)
    {
        breaker.ReleaseProbeWithoutOutcome(ForeignSevenTvBreakerOperations.ForeignPreview, generation);
        return ForeignSevenTvBreakerTransition.None;
    }

    private void LogBreakerTransition(string logIdentifier, ForeignEmoteSetLookupStatus status, ForeignSevenTvBreakerTransition transition)
    {
        switch (transition)
        {
            case ForeignSevenTvBreakerTransition.Opened:
                // The one line this whole class exists to log exactly once per open event, not per
                // rejected request (spec section 6): a transition only ever happens on the call that
                // causes it, never on the many rejections that follow while it stays open.
                logger.LogWarning(
                    "7TV-Circuit-Breaker für Fremdkanal-Vorschauen geöffnet (ausgelöst durch {Identifier}, Status {Status}).",
                    logIdentifier, status);
                break;
            case ForeignSevenTvBreakerTransition.Closed:
                logger.LogInformation("7TV-Circuit-Breaker für Fremdkanal-Vorschauen wieder geschlossen.");
                break;
            case ForeignSevenTvBreakerTransition.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition), transition, "Unbekannter ForeignSevenTvBreakerTransition.");
        }
    }
}
