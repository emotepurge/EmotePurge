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
/// The breaker state this decision was made against: the value of the policy's transition clock at
/// admission, which fixes both the provider epoch and the operation's epoch the call saw. It must be
/// handed back with the outcome — see the "one incident, one generation" and "two epochs" sections
/// on <see cref="ForeignSevenTvBreakerPolicy"/> for why a report is checked against it, and against
/// which epoch, instead of applied unconditionally. Opaque to callers: pass it back, never compare it.
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
    /// The one budgeted owner lookup the set-centric import's owner check falls back to when the
    /// set is in none of the checked accounts' lists (spec 2026-09-20, section 32). Its own failure
    /// counter and probe, like the list: a broken owner query must not take the list down with it.
    /// </summary>
    public const string EmoteSetOwner = "emote-set-owner";

    /// <summary>
    /// The guarded editor-grant refresh behind the same owner check (spec 2026-09-20, section 32,
    /// second review round) — the identity and <c>editor_of</c> requests a report makes when the
    /// grant cache has nothing. Its own streak and probe, for the same reason as the owner lookup;
    /// the authorization path's unguarded grant lookup never reports here.
    /// </summary>
    public const string EditorGrants = "editor-grants";

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
/// <b>Two epochs, not one (correction of 2026-09-21, spec section 32).</b> "The state a report was
/// made against" has two independent parts, and each gets its own epoch. The <i>provider epoch</i>
/// moves only when the provider-wide rate-limit lock opens or closes; an <i>operation's epoch</i>
/// moves only when that operation's own breaker opens or closes. Both are stamps from one
/// monotonic transition clock, so a decision carries a single value — the clock at admission — and
/// "has this epoch moved since I was admitted?" is simply "is its stamp newer than my admission?".
/// A report is checked against the epoch of the state it wants to change: opening or clearing the
/// rate-limit lock needs a current provider epoch and nothing else, so an operation-local
/// transition on one path can no longer discard the confirmed 429 another path brings back (the
/// single shared generation of 2026-09-20 did exactly that). The operation's own effects — its
/// failure streak, its open state, its probe slot — need its own epoch <i>and</i> the provider
/// epoch to be current, because what an outcome means for the operation depends on the whole state
/// it was admitted under: whether it was a probe, and whether an ordinary failure belonged to a
/// rate-limit incident that has since been acted on. A late success on one path therefore still
/// cannot clear a lock another path caught — the provider epoch has moved — and no path's reports
/// or transitions ever touch another path's streak or probe. The probe slot is held only while
/// neither of the two epochs its operation sees has moved since the claim, so any transition that
/// makes the probe's own report stale also hands its slot back.
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

    // A probe slot nobody holds. Below every real stamp, which only ever counts upward from zero.
    private const long NoProbe = long.MinValue;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();

    // One entry per operation identifier, created on first use. Bounded by the number of named
    // operations sharing this instance (four today), never by anything a request carries.
    private readonly Dictionary<string, OperationState> _operations = new(StringComparer.Ordinal);

    // Provider-wide, deliberately: 7TV limits the bucket, not the query. A confirmed 429 on one
    // operation says exactly as much about the other one.
    private bool _rateLimitOpen;
    private DateTimeOffset _rateLimitOpenUntil;

    // The transition clock: advanced by one on every open and every close, of the provider lock or
    // of any operation, never on a failure that merely accumulates. It only hands out stamps — a
    // decision carries its current value, and each epoch below is the stamp of the last transition
    // of its own state. A report whose admission predates an epoch's stamp describes a version of
    // that state that no longer exists.
    private long _clock;

    // The provider epoch: the stamp of the last time the rate-limit lock opened or closed. Moves on
    // nothing else — an operation opening or closing its own breaker leaves it where it is.
    private long _providerEpoch;

    /// <summary>
    /// Asks whether an upstream call for <paramref name="operation"/> may be made right now. Every
    /// caller that receives <c>Allowed: true</c> for a would-be probe (breaker open, open duration
    /// elapsed) is obligated to call <see cref="RecordSuccess"/>, <see cref="RecordFailure"/> or
    /// <see cref="ReleaseProbeWithoutOutcome"/> exactly once with the outcome, <i>the same
    /// operation</i> <i>and the decision's generation</i> — otherwise the breaker holds that
    /// operation's probe slot until the next transition of that operation or of the provider-wide
    /// rate-limit lock frees it.
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
                return new ForeignSevenTvBreakerDecision(true, false, TimeSpan.Zero, _clock);
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
                return new ForeignSevenTvBreakerDecision(false, openedByRateLimit, remaining, _clock);
            }

            if (ProbeHeld(state))
            {
                return new ForeignSevenTvBreakerDecision(false, openedByRateLimit, TimeSpan.Zero, _clock);
            }

            state.ProbeClaimedAt = _clock;
            return new ForeignSevenTvBreakerDecision(true, false, TimeSpan.Zero, _clock);
        }
    }

    /// <summary>
    /// A 7TV call succeeded: closes this operation's own open state and resets its failure streak
    /// if neither its epoch nor the provider epoch has moved since <paramref name="generation"/>,
    /// and clears the provider-wide rate limit if the provider epoch has not. A success admitted
    /// before the incident it would end is a straggler and proves nothing about it.
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
            var state = StateOf(operation);
            var wasOpen = IsBlocked(state);
            var providerCurrent = ProviderEpochCurrent(generation);

            if (providerCurrent && OperationEpochCurrent(state, generation))
            {
                ReleaseProbe(state, generation);
                state.ConsecutiveFailures = 0;
                if (state.Open)
                {
                    state.Open = false;
                    state.Epoch = Tick();
                }
            }

            if (providerCurrent && _rateLimitOpen)
            {
                _rateLimitOpen = false;
                _providerEpoch = Tick();
            }

            return wasOpen && !IsBlocked(state)
                ? ForeignSevenTvBreakerTransition.Closed
                : ForeignSevenTvBreakerTransition.None;
        }
    }

    /// <summary>
    /// A 7TV call failed. <paramref name="outcome"/> decides which of the two E4 triggers applies —
    /// and, since the correction of 2026-09-20 (spec 6.1), how far the consequence reaches: a
    /// confirmed rate limit locks the whole provider, every other failure only ever accumulates
    /// against <paramref name="operation"/>. <paramref name="retryAfter"/> is only read when the
    /// breaker actually opens as a result. Each consequence is checked against the epoch of the
    /// state it changes: the rate-limit lock only needs the provider epoch to be current, the
    /// operation's streak and open state need its own epoch and the provider epoch. A failure that
    /// fails its check is ignored for the same reason a stale success is: the incident it belongs to
    /// has already been acted on.
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
            var state = StateOf(operation);
            var providerCurrent = ProviderEpochCurrent(generation);
            var operationCurrent = providerCurrent && OperationEpochCurrent(state, generation);

            if (outcome == ForeignSevenTvBreakerOutcome.RateLimited)
            {
                if (operationCurrent)
                {
                    // No threshold to accumulate — one confirmed 429 is proof enough (E4a). Pinned at
                    // the threshold rather than left untouched, so a rate limit immediately followed
                    // by an ordinary failure (once the breaker reopens) does not need four more to
                    // reopen again.
                    ReleaseProbe(state, generation);
                    state.ConsecutiveFailures = FailureThreshold;
                }

                // Checked against the provider epoch alone: an operation opening or closing its own
                // breaker in the meantime — this one's or another's — says nothing about 7TV's
                // bucket, and must not throw away the Retry-After this 429 brought back.
                return providerCurrent ? OpenRateLimit(state, retryAfter) : ForeignSevenTvBreakerTransition.None;
            }

            if (!operationCurrent)
            {
                return ForeignSevenTvBreakerTransition.None;
            }

            ReleaseProbe(state, generation);
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
    /// Idempotent and safe to call unconditionally: a no-op whenever either epoch the operation sees
    /// has moved on since <paramref name="generation"/> (the slot went back with that transition),
    /// the slot was claimed after <paramref name="generation"/> by somebody else, or the operation
    /// was not actually mid-probe.
    /// </summary>
    public void ReleaseProbeWithoutOutcome(string operation, long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        lock (_gate)
        {
            var state = StateOf(operation);
            if (ProviderEpochCurrent(generation) && OperationEpochCurrent(state, generation))
            {
                ReleaseProbe(state, generation);
            }
        }
    }

    // A confirmed 429: the lock is the provider's, not the operation's. The operation's own open
    // state is left exactly as it was — this incident is not its doing.
    private ForeignSevenTvBreakerTransition OpenRateLimit(OperationState state, TimeSpan? retryAfter)
    {
        var wasOpen = IsBlocked(state);
        _rateLimitOpen = true;
        _rateLimitOpenUntil = _timeProvider.GetUtcNow() + Duration(retryAfter);
        _providerEpoch = Tick();
        return wasOpen ? ForeignSevenTvBreakerTransition.None : ForeignSevenTvBreakerTransition.Opened;
    }

    // Five consecutive ordinary failures of one query, or a failed probe of one query: it says
    // nothing about the others, so it locks nothing but itself. The provider-wide rate limit is
    // deliberately left standing — an ordinary failure is not evidence that 7TV's bucket reopened.
    // Only this operation's epoch moves: the provider's does not, so a 429 another operation still
    // has in flight keeps its right to lock the provider.
    private ForeignSevenTvBreakerTransition OpenOperation(OperationState state, TimeSpan? retryAfter)
    {
        var wasOpen = IsBlocked(state);
        state.Open = true;
        state.OpenUntil = _timeProvider.GetUtcNow() + Duration(retryAfter);
        state.Epoch = Tick();
        return wasOpen ? ForeignSevenTvBreakerTransition.None : ForeignSevenTvBreakerTransition.Opened;
    }

    // Called under _gate only. A new stamp for a transition that is about to happen.
    private long Tick() => ++_clock;

    // Called under _gate only. Has the rate-limit lock opened or closed since the admission?
    private bool ProviderEpochCurrent(long admittedAt) => _providerEpoch <= admittedAt;

    // Called under _gate only.
    private bool IsBlocked(OperationState state) => state.Open || _rateLimitOpen;

    // Called under _gate only. The probe counts as in flight only while neither epoch the operation
    // sees has moved since the claim — a transition in either one makes the probe's own report stale,
    // so it must hand the slot back too, or the operation would wait for a report that can no longer
    // be applied. A transition of another operation moves neither, and leaves the slot alone.
    private bool ProbeHeld(OperationState state) =>
        state.ProbeClaimedAt >= Math.Max(state.Epoch, _providerEpoch);

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

    private static bool OperationEpochCurrent(OperationState state, long admittedAt) => state.Epoch <= admittedAt;

    // Frees the slot only for its own claimant (or a report admitted after the claim, which can
    // only exist once the claim is stale anyway) — never for a straggler admitted before the claim,
    // or two callers could be in flight as "the one probe".
    private static void ReleaseProbe(OperationState state, long admittedAt)
    {
        if (admittedAt >= state.ProbeClaimedAt)
        {
            state.ProbeClaimedAt = NoProbe;
        }
    }

    private static TimeSpan Duration(TimeSpan? retryAfter) =>
        retryAfter is { } ra && ra > TimeSpan.Zero ? ra : DefaultOpenDuration;

    /// <summary>
    /// Everything the correction of 2026-09-20 made per-operation: the ordinary-failure streak and
    /// the one half-open probe — and, since 2026-09-21, the operation's own epoch. The rate-limit
    /// lock and its epoch are not in here, on purpose.
    /// </summary>
    private sealed class OperationState
    {
        public bool Open;
        public DateTimeOffset OpenUntil;
        public int ConsecutiveFailures;

        // The operation epoch: the stamp of the last time this operation's own breaker opened or
        // closed. Moves on nothing else — neither the provider lock nor another operation.
        public long Epoch;

        // The transition clock at which this operation's probe was claimed, or NoProbe. A probe
        // counts as in flight only while it is not older than either epoch — see ProbeHeld.
        public long ProbeClaimedAt = NoProbe;
    }
}
