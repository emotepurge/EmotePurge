namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>The outcome of one upstream attempt, as reported to <see cref="ForeignSevenTvBreakerPolicy"/>.</summary>
public enum ForeignSevenTvBreakerOutcome
{
    Success,

    /// <summary>A confirmed 7TV overload — HTTP 429, or HTTP 200 with <c>extensions.status: 429</c>.</summary>
    RateLimited,

    /// <summary>Any other upstream failure (timeout, 5xx, unparseable body, …).</summary>
    OtherFailure
}

/// <summary>Whether <see cref="ForeignSevenTvBreakerPolicy.TryAcquire"/> transitioned the breaker.</summary>
public enum ForeignSevenTvBreakerTransition
{
    None,
    Opened,
    Closed
}

/// <param name="Allowed">
/// <c>false</c> means: do not call 7TV at all. <c>true</c> while closed means normal traffic;
/// <c>true</c> while the open duration has elapsed means this is the single probe request — the
/// caller must report its outcome via <see cref="ForeignSevenTvBreakerPolicy.RecordSuccess"/> or
/// <see cref="ForeignSevenTvBreakerPolicy.RecordFailure"/>.
/// </param>
/// <param name="OpenedByRateLimit">
/// Only meaningful when <paramref name="Allowed"/> is <c>false</c>: lets the caller answer a
/// rejected request with the same error code a live rate limit would have produced, rather than a
/// generic "unavailable".
/// </param>
/// <param name="RemainingOpenTime">Only meaningful when <paramref name="Allowed"/> is <c>false</c>.</param>
/// <param name="Generation">
/// The breaker state this decision was made against. It must be handed back with the outcome — see
/// the "one incident, one generation" section on <see cref="ForeignSevenTvBreakerPolicy"/> for why a
/// report from an older generation is ignored instead of applied.
/// </param>
public readonly record struct ForeignSevenTvBreakerDecision(
    bool Allowed, bool OpenedByRateLimit, TimeSpan RemainingOpenTime, long Generation);

/// <summary>
/// The operations that share one <see cref="ForeignSevenTvBreakerPolicy"/> instance. Constants
/// rather than free-form strings: an operation nobody named here is an operation nobody can find
/// again, and a typo would silently open a third, private failure counter that no caller ever
/// consults a second time.
/// </summary>
public static class ForeignSevenTvBreakerOperations
{
    /// <summary>The foreign-channel-import preview (spec 2026-09-09).</summary>
    public const string ForeignPreview = "foreign-preview";

    /// <summary>The emote-set list of a 7TV account (spec 2026-09-20, 6.1).</summary>
    public const string EmoteSetList = "emote-set-list";

    /// <summary>
    /// The leaderboard import source (spec 2026-09-13). It runs on its own policy instance because
    /// its upstream bucket is a different one, so this name only ever shares a dictionary with
    /// itself — it exists because the operation parameter is mandatory, not because the leaderboard
    /// has anything to share.
    /// </summary>
    public const string Leaderboard = "leaderboard";
}

/// <summary>
/// Circuit breaker for the foreign-channel-import preview's 7TV calls (spec 2026-09-09, E4/AK 8) —
/// handwritten and pure, no Polly, same shape as <c>TwitchReconnectBackoffPolicy</c>,
/// <c>SevenTv.SevenTvBackoffPolicy</c> and <c>TwitchWatchdogPolicy</c> in <c>EmotePurge.Worker</c>: a
/// result in, a decision out, no I/O, no logging (that is the caller's job — "the opening is logged
/// once, not per request" only holds if logging lives outside a class every rejected request calls
/// into).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two triggers, not one (E4).</b> A single confirmed rate limit — <see cref="ForeignSevenTvBreakerOutcome.RateLimited"/> —
/// opens the breaker immediately, with no failure count to accumulate first. Every other upstream
/// failure only opens it after <see cref="FailureThreshold"/> consecutive occurrences. The first
/// draft of this feature's spec got this backwards: it let four more attempts through after an
/// unambiguous 429 and re-opened on a fixed 60 s clock regardless of what 7TV actually asked for —
/// against a search-bucket lockout that runs roughly an hour
/// (<c>x-ratelimit-search-reset: 3583</c>, measured live), that would have made the feature worse
/// than the workaround it replaces.
/// </para>
/// <para>
/// <b>The open duration is not fixed.</b> A caller-supplied <c>retryAfter</c> (7TV's own
/// <c>Retry-After</c> header, or the reset hint some GraphQL error payloads carry, when present)
/// overrides <see cref="DefaultOpenDuration"/> — honoring what the provider actually asked for beats
/// guessing.
/// </para>
/// <para>
/// <b>Exactly one probe.</b> Once the open duration has elapsed, <see cref="TryAcquire"/> allows
/// through exactly one caller (the "probe") and rejects every other concurrent one until that probe
/// reports back. A failed probe re-opens the breaker (from whatever failure it reported); a
/// successful one closes it and resets the failure streak to zero.
/// </para>
/// <para>
/// <b>One incident, one generation.</b> Every decision carries the state it was made against, and a
/// report is only applied if the breaker is still in that state. Without it, two lookups running side
/// by side could undo each other: the first is admitted while the breaker is closed, the second gets
/// a 429 and opens it for the hour 7TV asked for, and then the first — admitted before that ever
/// happened — finishes successfully and unconditionally closes the breaker again, throwing away the
/// <c>Retry-After</c> and resuming traffic straight into an active lockout. Only the probe of the
/// current generation can close an open breaker; a straggler from an older one is ignored outright,
/// which also stops it from extending an open window it knows nothing about.
/// </para>
/// <para>
/// Not clock-free like the Worker policies above, because unlike a reconnect loop's single caller
/// this is consulted by concurrently arriving HTTP requests over real wall-clock time — it holds a
/// <see cref="TimeProvider"/> internally instead, the same shape <c>RateLimitTelemetryStore</c>
/// already uses, and stays substitutable in tests through the same seam.
/// </para>
/// <para>
/// <b>What each signal locks (spec 2026-09-20, 6.1).</b> One instance serves more than one
/// operation, and the two signals do not reach equally far. A confirmed <c>RateLimited</c> — HTTP
/// 429 or HTTP 200 with <c>extensions.status: 429</c> — locks the <i>provider</i>, with 7TV's own
/// <c>Retry-After</c>: the bucket it describes is shared, so a 429 on one query says exactly as
/// much about the other. The consecutive-<c>OtherFailure</c> streak and the one half-open probe are
/// <i>per operation</i>, because a malformed or schema-drifted query of ours is indistinguishable
/// from a real outage (F17) and would otherwise pull a perfectly healthy second read path into 503
/// after five attempts — and then burn the one probe that path needed to prove itself. The earlier
/// reasoning ("both paths share the budget, so they share the breaker") confused the budget with
/// the failure counter; a second, keyed instance would have gone too far the other way and split
/// the one signal that has to stay shared.
/// </para>
/// </remarks>

public sealed class ForeignSevenTvBreakerPolicy(TimeProvider? timeProvider = null)
{
    /// <summary>Consecutive non-rate-limit failures required to open the breaker, per operation.</summary>
    public const int FailureThreshold = 5;

    /// <summary>Used when no <c>retryAfter</c> was supplied.</summary>
    public static readonly TimeSpan DefaultOpenDuration = TimeSpan.FromSeconds(60);

    // A probe slot nobody holds. Never equal to a real generation, which only ever counts upward
    // from zero.
    private const long NoProbe = long.MinValue;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();

    // One entry per operation identifier, created on first use. Bounded by the number of named
    // operations sharing this instance (two today), never by anything a request carries.
    private readonly Dictionary<string, OperationState> _operations = new(StringComparer.Ordinal);

    // Provider-wide, deliberately: 7TV limits the bucket, not the query. A confirmed 429 on one
    // operation says exactly as much about the other one.
    private bool _rateLimitOpen;
    private DateTimeOffset _rateLimitOpenUntil;

    // Bumped on every open and every close, never on a failure that merely accumulates. A report
    // carrying anything other than the current value describes a breaker state that no longer
    // exists. Shared across operations rather than kept per operation, and that is what makes the
    // probe slot self-releasing: a probe is held only for as long as the generation it was claimed
    // against is still current, so a transition anywhere hands every stale slot back instead of
    // leaving an operation jammed on a report that will never be applied.
    private long _generation;

    /// <summary>
    /// Asks whether an upstream call for <paramref name="operation"/> may be made right now. Every
    /// caller that receives <c>Allowed: true</c> for a would-be probe (breaker open, open duration
    /// elapsed) is obligated to call <see cref="RecordSuccess"/>, <see cref="RecordFailure"/> or
    /// <see cref="ReleaseProbeWithoutOutcome"/> exactly once with the outcome, <i>the same
    /// operation</i> <i>and the decision's generation</i> — otherwise the breaker holds that
    /// operation's probe slot until the next transition frees it.
    /// </summary>
    /// <param name="operation">
    /// One of <see cref="ForeignSevenTvBreakerOperations"/>. Mandatory on all four methods, and
    /// deliberately without a default: a caller that omitted it would silently file its failures in
    /// somebody else's streak, which is the exact confusion this parameter exists to end.
    /// </param>
    public ForeignSevenTvBreakerDecision TryAcquire(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        lock (_gate)
        {
            var state = StateOf(operation);
            if (!state.Open && !_rateLimitOpen)
            {
                return new ForeignSevenTvBreakerDecision(true, false, TimeSpan.Zero, _generation);
            }

            var now = _timeProvider.GetUtcNow();
            var remaining = TimeSpan.Zero;
            var openedByRateLimit = false;

            if (state.Open)
            {
                remaining = state.OpenUntil - now;
            }

            if (_rateLimitOpen)
            {
                // Whichever blocker lasts longer is the one the caller is told about: it decides
                // both how long to stay away and which error code the rejection maps to.
                var rateLimitRemaining = _rateLimitOpenUntil - now;
                if (!state.Open || rateLimitRemaining > remaining)
                {
                    remaining = rateLimitRemaining;
                    openedByRateLimit = true;
                }
            }

            if (remaining > TimeSpan.Zero)
            {
                return new ForeignSevenTvBreakerDecision(false, openedByRateLimit, remaining, _generation);
            }

            if (state.ProbeGeneration == _generation)
            {
                return new ForeignSevenTvBreakerDecision(false, openedByRateLimit, TimeSpan.Zero, _generation);
            }

            state.ProbeGeneration = _generation;
            return new ForeignSevenTvBreakerDecision(true, false, TimeSpan.Zero, _generation);
        }
    }

    /// <summary>
    /// A 7TV call succeeded: closes this operation's own open state, clears the provider-wide rate
    /// limit and resets this operation's failure streak — but only if <paramref name="generation"/>
    /// is still the current one. A success reported from an older generation is a straggler that
    /// started before the incident and proves nothing about it.
    /// </summary>
    /// <remarks>
    /// The rate limit is cleared whichever operation reports the success, and that is deliberate:
    /// the bucket it describes is one bucket, so a call that got through proves it is no longer
    /// closed, no matter who made it. The failure streak is the opposite — it describes one query's
    /// health and is reset only for the operation that reported.
    /// </remarks>
    public ForeignSevenTvBreakerTransition RecordSuccess(string operation, long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        lock (_gate)
        {
            if (generation != _generation)
            {
                return ForeignSevenTvBreakerTransition.None;
            }

            var state = StateOf(operation);
            var wasOpen = state.Open || _rateLimitOpen;
            state.Open = false;
            state.ProbeGeneration = NoProbe;
            state.ConsecutiveFailures = 0;
            _rateLimitOpen = false;
            if (wasOpen)
            {
                _generation++;
                return ForeignSevenTvBreakerTransition.Closed;
            }

            return ForeignSevenTvBreakerTransition.None;
        }
    }

    /// <summary>
    /// A 7TV call failed. <paramref name="outcome"/> decides which of the two E4 triggers applies —
    /// and, since the correction of 2026-09-20 (spec 6.1), how far the consequence reaches: a
    /// confirmed rate limit locks the whole provider, every other failure only ever accumulates
    /// against <paramref name="operation"/>. <paramref name="retryAfter"/> is only read when the
    /// breaker actually opens as a result. A failure from an older <paramref name="generation"/> is
    /// ignored for the same reason a stale success is: the incident it belongs to has already been
    /// acted on.
    /// </summary>
    public ForeignSevenTvBreakerTransition RecordFailure(
        string operation, ForeignSevenTvBreakerOutcome outcome, TimeSpan? retryAfter, long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (outcome == ForeignSevenTvBreakerOutcome.Success)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome), outcome, "RecordFailure() cannot carry a success — RecordSuccess() is for that.");
        }

        lock (_gate)
        {
            if (generation != _generation)
            {
                return ForeignSevenTvBreakerTransition.None;
            }

            var state = StateOf(operation);
            state.ProbeGeneration = NoProbe;

            if (outcome == ForeignSevenTvBreakerOutcome.RateLimited)
            {
                // No threshold to accumulate — one confirmed 429 is proof enough (E4a). Pinned at the
                // threshold rather than left untouched, so a rate limit immediately followed by an
                // ordinary failure (once the breaker reopens) does not need four more to reopen again.
                state.ConsecutiveFailures = FailureThreshold;
                return OpenRateLimit(state, retryAfter);
            }

            state.ConsecutiveFailures++;
            if (state.Open || _rateLimitOpen || state.ConsecutiveFailures >= FailureThreshold)
            {
                return OpenOperation(state, retryAfter);
            }

            return ForeignSevenTvBreakerTransition.None;
        }
    }

    /// <summary>
    /// Releases <paramref name="operation"/>'s probe slot without reporting a health outcome — for a
    /// caller that <see cref="TryAcquire"/> let through (open duration elapsed) but that never
    /// actually reached 7TV, such as the provider-wide budget
    /// (<see cref="ForeignEmoteSetProviderBudget"/>) refusing a permit first, or an upstream answer
    /// that never touched 7TV at all (the Twitch-side failures a foreign lookup can also end in).
    /// Idempotent and safe to call unconditionally: a no-op whenever the breaker has moved on to a
    /// newer generation, or the operation was not actually mid-probe.
    /// </summary>
    public void ReleaseProbeWithoutOutcome(string operation, long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        lock (_gate)
        {
            if (generation == _generation)
            {
                StateOf(operation).ProbeGeneration = NoProbe;
            }
        }
    }

    // A confirmed 429: the lock is the provider's, not the operation's. The operation's own open
    // state is left exactly as it was — this incident is not its doing.
    private ForeignSevenTvBreakerTransition OpenRateLimit(OperationState state, TimeSpan? retryAfter)
    {
        var wasOpen = state.Open || _rateLimitOpen;
        _rateLimitOpen = true;
        _rateLimitOpenUntil = _timeProvider.GetUtcNow() + Duration(retryAfter);
        _generation++;
        return wasOpen ? ForeignSevenTvBreakerTransition.None : ForeignSevenTvBreakerTransition.Opened;
    }

    // Five consecutive ordinary failures of one query, or a failed probe of one query: it says
    // nothing about the others, so it locks nothing but itself. The provider-wide rate limit is
    // deliberately left standing — an ordinary failure is not evidence that 7TV's bucket reopened.
    private ForeignSevenTvBreakerTransition OpenOperation(OperationState state, TimeSpan? retryAfter)
    {
        var wasOpen = state.Open || _rateLimitOpen;
        state.Open = true;
        state.OpenUntil = _timeProvider.GetUtcNow() + Duration(retryAfter);
        _generation++;
        return wasOpen ? ForeignSevenTvBreakerTransition.None : ForeignSevenTvBreakerTransition.Opened;
    }

    // Called under _gate only.
    private OperationState StateOf(string operation)
    {
        if (!_operations.TryGetValue(operation, out var state))
        {
            state = new OperationState();
            _operations[operation] = state;
        }

        return state;
    }

    private static TimeSpan Duration(TimeSpan? retryAfter) =>
        retryAfter is { } ra && ra > TimeSpan.Zero ? ra : DefaultOpenDuration;

    /// <summary>
    /// Everything the correction of 2026-09-20 made per-operation: the ordinary-failure streak and
    /// the one half-open probe. The rate-limit lock is not in here, on purpose.
    /// </summary>
    private sealed class OperationState
    {
        public bool Open;
        public DateTimeOffset OpenUntil;
        public int ConsecutiveFailures;

        // The generation this operation's probe was claimed against, or NoProbe. A probe counts as
        // in flight only while that generation is still current — see the _generation comment.
        public long ProbeGeneration = NoProbe;
    }
}
