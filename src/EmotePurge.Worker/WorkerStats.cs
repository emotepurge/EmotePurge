namespace EmotePurge.Worker;

/// <summary>
/// The worker's own flush bookkeeping, lifted out of <see cref="UsageFlushWorker"/> so the health
/// snapshot can report it. Worker-internal state with no external dependency, so it stays a concrete
/// singleton rather than getting a Core interface — same call as
/// <see cref="SevenTv.SevenTvSubscriptionRegistry"/>, which two hosted services likewise share by
/// concrete type.
/// <para>
/// Clock-free by design, like every policy in this project: the caller passes the timestamp in,
/// which keeps the class deterministically unit-testable without a clock abstraction.
/// </para>
/// </summary>
public sealed class WorkerStats
{
    // One lock rather than Interlocked per field: a success writes three fields that are read
    // together by the health publisher, and a torn read there would show a fresh timestamp next to
    // a stale row count.
    private readonly Lock _lock = new();

    private int _consecutiveFlushFailures;
    private DateTime? _lastFlushSuccessUtc;
    private int? _lastFlushRowCount;

    // Interlocked rather than the lock above: this is a per-message hot-path counter (one increment
    // per chat message with an indeterminate room, called from TwitchChatManager.OnMessageReceived),
    // a much higher frequency than the three flush fields, which are read/written together only
    // once per 30s flush. A single long needs no torn-read protection of its own.
    private long _indeterminateSharedChatMessages;

    private long _chatLines;

    // Same pattern as the counter above: one increment per matched line in
    // TwitchChatManager.OnMessageReceived (issue #114), no lock needed for a single long.
    private long _splicedIrcLines;

    // Cumulative since process start, never reset — deliberately not a Take...SinceLastFlush pair
    // like the two counters above: those are per-flush, this counts per process (Plan #68, Task 2).
    // Interlocked for the same reason as the hot-path counters above: a single long needs no
    // torn-read protection of its own, and there is no second field it must stay consistent with.
    //
    // Not on WorkerHealthSnapshot: that is an Api-side wired contract, and adding a field to it is a
    // second contract change on top of this one — deliberately declined here, the same call the
    // #114 plan made (concept 7.4). The reader is the rebuild loop's own closing log line in
    // TwitchConnectionWatchdog ("Rejoin abgeschlossen … seit Prozessstart: {Rebuilds} Wiederaufbauten,
    // {Failures} Fehlversuche"), and nowhere else.
    private long _twitchRebuildCount;
    private long _twitchRebuildAttemptFailureCount;

    // The reward capture's own counters (plan #486 2.1, 2.3): bumped from the chat handler and the observation
    // queue, read and zeroed together once a minute by the capture worker's log line. Interlocked for the same
    // reason as the hot-path counters above.
    private long _rewardObservationOverflows;
    private long _rewardObservationsRejectedAtShutdown;
    private long _splicedRewardRedemptions;
    private long _rewardCaptureFaults;
    private long _rewardObservationsAtEmptySnapshot;

    public int ConsecutiveFlushFailures
    {
        get
        {
            lock (_lock)
            {
                return _consecutiveFlushFailures;
            }
        }
    }

    public DateTime? LastFlushSuccessUtc
    {
        get
        {
            lock (_lock)
            {
                return _lastFlushSuccessUtc;
            }
        }
    }

    public int? LastFlushRowCount
    {
        get
        {
            lock (_lock)
            {
                return _lastFlushRowCount;
            }
        }
    }

    /// <summary>
    /// Total Twitch rebuild rounds started since process start (Plan #68, Task 2). Reading this does
    /// not reset it — unlike <see cref="TakeIndeterminateSharedChatMessagesSinceLastFlush"/>, there is
    /// no flush cycle this belongs to.
    /// </summary>
    public long TwitchRebuildCount => Interlocked.Read(ref _twitchRebuildCount);

    /// <summary>
    /// Total failed rebuild attempts since process start, within any number of rebuild rounds (Plan
    /// #68, Task 2). Same never-reset contract as <see cref="TwitchRebuildCount"/>.
    /// </summary>
    public long TwitchRebuildAttemptFailureCount => Interlocked.Read(ref _twitchRebuildAttemptFailureCount);

    /// <summary>Total accepted chat lines since process start; reading it does not reset the counter.</summary>
    public long TotalChatLines => Interlocked.Read(ref _chatLines);

    public void RecordChatLine() => Interlocked.Increment(ref _chatLines);

    /// <param name="rows">Number of emote counters written by the flush that just succeeded.</param>
    public void RecordFlushSuccess(int rows, DateTime nowUtc)
    {
        lock (_lock)
        {
            _consecutiveFlushFailures = 0;
            _lastFlushSuccessUtc = nowUtc;
            _lastFlushRowCount = rows;
        }
    }

    /// <summary>
    /// Returns the resulting streak length, so the caller can branch on it without a second read
    /// that another thread could have moved in between.
    /// </summary>
    public int RecordFlushFailure()
    {
        lock (_lock)
        {
            return ++_consecutiveFlushFailures;
        }
    }

    /// <summary>
    /// Called once per chat message whose room could not be determined (<c>MessageOrigin.Indeterminate</c>,
    /// #73). No logging here — a log line per message on this hot path would be its own incident;
    /// <see cref="UsageFlushWorker"/> logs the accumulated count once per flush instead.
    /// </summary>
    public void RecordIndeterminateSharedChatMessage() => Interlocked.Increment(ref _indeterminateSharedChatMessages);

    /// <summary>
    /// Reads and zeroes the count in one step, so two overlapping callers can never double-count or
    /// drop the difference between them.
    /// </summary>
    public long TakeIndeterminateSharedChatMessagesSinceLastFlush() =>
        Interlocked.Exchange(ref _indeterminateSharedChatMessages, 0);

    /// <summary>
    /// Called once per IRC line that <see cref="IrcLineSpliceRule.IsSpliced"/> flags as spliced
    /// (#114). No logging here — <see cref="TwitchChatManager"/> already warns once per line with
    /// the tag block; this counter only feeds the once-per-flush summary in
    /// <see cref="UsageFlushWorker"/>.
    /// </summary>
    public void RecordSplicedIrcLine() => Interlocked.Increment(ref _splicedIrcLines);

    /// <summary>
    /// Reads and zeroes the count in one step, so two overlapping callers can never double-count or
    /// drop the difference between them.
    /// </summary>
    public long TakeSplicedIrcLinesSinceLastFlush() =>
        Interlocked.Exchange(ref _splicedIrcLines, 0);

    /// <summary>
    /// Called once per rebuild round the reconnect loop in <see cref="TwitchConnectionWatchdog"/>
    /// starts (Plan #68, Task 2). Cumulative since process start — never reset.
    /// </summary>
    public void RecordTwitchRebuild() => Interlocked.Increment(ref _twitchRebuildCount);

    /// <summary>
    /// Called once per failed attempt within a rebuild round (Plan #68, Task 2). Cumulative since
    /// process start — never reset.
    /// </summary>
    public void RecordTwitchRebuildAttemptFailure() => Interlocked.Increment(ref _twitchRebuildAttemptFailureCount);

    /// <summary>A reward observation refused because the queue was full (plan #486 2.1).</summary>
    public void RecordRewardObservationOverflow() => Interlocked.Increment(ref _rewardObservationOverflows);

    /// <summary>A reward observation refused because the queue was already closed for the shutdown (Sol-R2-P2-8).</summary>
    public void RecordRewardObservationRejectedAtShutdown() =>
        Interlocked.Increment(ref _rewardObservationsRejectedAtShutdown);

    /// <summary>
    /// A line with the reward tag that the splice sentinel flagged and that was therefore not evaluated (plan W23).
    /// The same line also counts in <see cref="RecordSplicedIrcLine"/>.
    /// </summary>
    public void RecordSplicedRewardRedemption() => Interlocked.Increment(ref _splicedRewardRedemptions);

    /// <summary>An exception inside the capture branch of the chat handler, swallowed there (plan 2.1).</summary>
    public void RecordRewardCaptureFault() => Interlocked.Increment(ref _rewardCaptureFaults);

    /// <summary>
    /// A reward observation queued while the channel's emote snapshot was empty: the live proof that the capture
    /// sits before the cache check (AK67, Sol-R2-P2-9, live step A7).
    /// </summary>
    public void RecordRewardObservationAtEmptySnapshot() =>
        Interlocked.Increment(ref _rewardObservationsAtEmptySnapshot);

    /// <summary>
    /// Reads and zeroes the five reward capture counters, each in one step, for the capture worker's minute line.
    /// </summary>
    public RewardCaptureCounters TakeRewardCaptureCounters() => new(
        Overflowed: Interlocked.Exchange(ref _rewardObservationOverflows, 0),
        RejectedAtShutdown: Interlocked.Exchange(ref _rewardObservationsRejectedAtShutdown, 0),
        SplicedRedemptions: Interlocked.Exchange(ref _splicedRewardRedemptions, 0),
        CaptureFaults: Interlocked.Exchange(ref _rewardCaptureFaults, 0),
        ObservedAtEmptySnapshot: Interlocked.Exchange(ref _rewardObservationsAtEmptySnapshot, 0));
}

/// <summary>The reward capture counters since the last <see cref="WorkerStats.TakeRewardCaptureCounters"/>.</summary>
/// <param name="Overflowed">Observations refused at a full queue.</param>
/// <param name="RejectedAtShutdown">Observations refused at a closed queue.</param>
/// <param name="SplicedRedemptions">Reward lines not evaluated because they were spliced.</param>
/// <param name="CaptureFaults">Exceptions swallowed in the capture branch.</param>
/// <param name="ObservedAtEmptySnapshot">Observations queued while the channel's emote snapshot was empty.</param>
public readonly record struct RewardCaptureCounters(
    long Overflowed,
    long RejectedAtShutdown,
    long SplicedRedemptions,
    long CaptureFaults,
    long ObservedAtEmptySnapshot)
{
    /// <summary>Whether every counter is zero (the minute line is written only otherwise).</summary>
    public bool IsZero => Overflowed == 0 && RejectedAtShutdown == 0 && SplicedRedemptions == 0
        && CaptureFaults == 0 && ObservedAtEmptySnapshot == 0;
}
