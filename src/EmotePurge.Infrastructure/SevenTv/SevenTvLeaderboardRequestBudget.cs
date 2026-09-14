namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// The hard ceiling on leaderboard traffic: at most <see cref="MaxRequests"/> upstream requests may
/// start within any rolling <see cref="Window"/>, per process run (spec 2026-09-13, section 6).
/// Charged once per upstream <i>page</i>, immediately before the request leaves the process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two things carry the safety, not one.</b> The finite key space (two sort keys, up to two
/// pages each, one hour shelf-life) holds the <i>expected</i> cost at four upstream requests an
/// hour; this budget holds the <i>lid</i> at ten, whatever users do and whatever fails. A key space
/// alone caps successfully cached answers only — on a key that keeps failing, the fill would start
/// again and again.
/// </para>
/// <para>
/// <b>The little sister of <see cref="ForeignEmoteSetProviderBudget"/>, not a rebuild of it</b>
/// (operator decision E10). The two limits differ in every dimension that matters: sixty a minute
/// versus ten an hour, a five-second wait versus no wait at all, one provider-wide concurrency gate
/// versus none. Folding the leaderboard's lid into the preview's budget would have coupled two
/// failure domains whose only shared property is the word "7TV" — a leaderboard lockout must not
/// slow the foreign-channel preview down, and vice versa.
/// </para>
/// <para>
/// <b>No waiting.</b> A refusal here is an answer, not a delay: the caller turns it into a
/// <c>BudgetRefused</c> entry with a thirty-second shelf-life and makes no upstream request. That is
/// also why a refusal must never be reported to the circuit breaker — it is congestion we inflicted
/// on ourselves, not evidence about 7TV's health.
/// </para>
/// <para>
/// <b>Rolling, not fixed</b> — same reasoning as the sibling budget: a fixed window would let ten
/// requests just before the boundary and ten just after pass as "ten an hour", which is twenty in
/// the rolling hour that straddles it. Bounded memory: the queue never holds more than
/// <see cref="MaxRequests"/> timestamps.
/// </para>
/// <para>
/// <b>In-process, per process run.</b> The budget starts empty after a restart, so runs inside the
/// same rolling hour add up (worst case <c>10 × runs</c>). Accepted, because restarts are deploys or
/// crashes and no user can cause one; a crash loop is an operational fault the container healthcheck
/// makes visible. A lid that survived restarts would mean persisted state — the Redis path the spec
/// reserves for the day a second API replica exists.
/// </para>
/// </remarks>
public sealed class SevenTvLeaderboardRequestBudget
{
    /// <summary>The lid the spec fixes: ten upstream requests per rolling window and process run.</summary>
    public const int DefaultMaxRequests = 10;

    /// <summary>The rolling window the spec fixes.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(60);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    // Timestamps of the permits granted within the current rolling window, oldest first.
    private readonly Queue<DateTimeOffset> _granted = new();

    /// <param name="maxRequests">How many requests may start within one <paramref name="window"/>.</param>
    /// <param name="window">The length of the rolling window.</param>
    /// <param name="timeProvider">Substituted in tests; the system clock otherwise.</param>
    public SevenTvLeaderboardRequestBudget(int maxRequests, TimeSpan window, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRequests);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        MaxRequests = maxRequests;
        Window = window;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>How many requests may start within one <see cref="Window"/>.</summary>
    public int MaxRequests { get; }

    /// <summary>The length of the rolling window.</summary>
    public TimeSpan Window { get; }

    /// <summary>
    /// Takes one permit for one upstream request, without waiting. <c>false</c> means the caller must
    /// not make the request at all.
    /// </summary>
    /// <param name="usedInWindow">
    /// How many permits the window holds once this call is done — including the one just granted, so
    /// the sixth grant reports six. Unchanged by a refusal, which reports the window as full
    /// (<see cref="MaxRequests"/>). This is the number the early-warning alarm watches and the number
    /// the per-request log line carries.
    /// </param>
    /// <returns><c>true</c> if the request may be made.</returns>
    public bool TryCharge(out int usedInWindow)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            Trim(now);

            if (_granted.Count >= MaxRequests)
            {
                // A refusal consumes nothing: it neither enqueues a timestamp nor moves the window,
                // so a refused caller cannot push the next legitimate request out of its slot.
                usedInWindow = _granted.Count;
                return false;
            }

            _granted.Enqueue(now);
            usedInWindow = _granted.Count;
            return true;
        }
    }

    // Drops every permit that has aged out of the rolling window. Called under _gate only.
    private void Trim(DateTimeOffset now)
    {
        while (_granted.Count > 0 && now - _granted.Peek() >= Window)
        {
            _granted.Dequeue();
        }
    }
}
