namespace EmotePurge.Worker.ChatLogBackfill;

/// <summary>
/// The worker's waiting decisions for a backfill run (spec 4.5), pure and clock-free: the caller
/// hands in the counters of the last transition it received and the current instant.
/// <para>
/// <b>Transport</b> (<c>TransportFailure</c> incl. 5xx, <c>BodyTimeout</c>): up to
/// <c>TransportRetries</c> attempts per block, attempt 1 included; the delay before attempt n
/// (n ≥ 2) is <c>30 s × 4^(n−2)</c> — 30 s before the 2nd, 120 s before the 3rd (D13) — capped at
/// <c>MaxRetryAfterSeconds</c>.
/// </para>
/// <para>
/// <b>429</b> is deliberately not decided here: the pause length and the <c>rate_limited</c>
/// threshold are computed inside <c>PauseAsync</c>'s conditional update from the row's persisted
/// <c>PauseCount</c> (D38), so a restart or a commit in between can never make the worker apply a
/// stale counter. The worker only waits out what the database wrote — the provider cooldown
/// (<see cref="CooldownWait"/>, D31) and, for the paused head, the next claim (D44).
/// </para>
/// </summary>
public static class ChatLogBackfillRetryPolicy
{
    /// <summary>The delay before the second transport attempt; each further attempt waits four times longer.</summary>
    public static readonly TimeSpan FirstTransportRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether another attempt on the same block is allowed after <paramref name="blockAttempts"/>
    /// failed ones (the persisted count the last <c>RecordBlockAttemptAsync</c> returned).
    /// </summary>
    public static bool ShouldRetryTransport(int blockAttempts, int transportRetries) => blockAttempts < transportRetries;

    /// <summary>
    /// The wait before the next attempt after <paramref name="blockAttempts"/> failed ones: attempt
    /// <c>n = blockAttempts + 1</c> waits <c>30 s × 4^(n−2)</c>, never longer than <paramref name="cap"/>
    /// (the worker passes <c>MaxRetryAfterSeconds</c>), so a raised <c>TransportRetries</c> cannot turn
    /// into hour-long waits.
    /// </summary>
    public static TimeSpan TransportRetryDelay(int blockAttempts, TimeSpan cap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockAttempts, 1);

        // In seconds and capped before a TimeSpan exists: 4^n overflows TimeSpan long before it overflows a double.
        var seconds = FirstTransportRetryDelay.TotalSeconds * Math.Pow(4, blockAttempts - 1);
        return seconds >= cap.TotalSeconds ? cap : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// How long the provider cooldown still holds at <paramref name="nowUtc"/>: zero when there is
    /// none or it has passed.
    /// </summary>
    public static TimeSpan CooldownWait(DateTime? cooldownUntilUtc, DateTime nowUtc) =>
        cooldownUntilUtc is { } until && until > nowUtc ? until - nowUtc : TimeSpan.Zero;

    /// <summary>
    /// The worker-side spacing (D7): how long to wait so that a request starts no earlier than
    /// <paramref name="spacing"/> after the previous one started. Zero before the first request.
    /// </summary>
    public static TimeSpan SpacingWait(DateTime? lastRequestStartedUtc, TimeSpan spacing, DateTime nowUtc)
    {
        if (lastRequestStartedUtc is not { } last)
        {
            return TimeSpan.Zero;
        }

        var due = last + spacing;
        return due > nowUtc ? due - nowUtc : TimeSpan.Zero;
    }
}
