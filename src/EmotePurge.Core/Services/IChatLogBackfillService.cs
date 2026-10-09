using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>
/// The chat-log backfill (#346, spec section 3): fills a channel's usage statistics for the days before
/// we started counting it from a public chat-log archive, on a manager's explicit request. One service
/// for both sides — the Api enqueues, cancels and reads the status; the worker (the only caller of the
/// second region) claims runs, commits blocks and records transitions.
/// <para>
/// Every status transition is a conditional update on the expected status; a transition that finds the
/// row in another state changes nothing and reports the state it found. The worker never carries a
/// counter across calls: it continues from the last <see cref="ChatLogBackfillTransition"/> it received
/// (D38).
/// </para>
/// </summary>
public interface IChatLogBackfillService
{
    // ---- Api side ----

    /// <summary>
    /// The settings tab's read model (spec 5.1). <paramref name="todayUtc"/> is the UTC date on the Api
    /// at request time; the window options are computed from it. <c>null</c> = the channel is unknown.
    /// </summary>
    Task<ChatLogBackfillStatusDto?> GetStatusAsync(string channelName, DateOnly todayUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates, reads the chosen 7TV set and, under the channel and requester row locks, inserts the
    /// run with its matching snapshot (creating archived placeholder <c>Emote</c> rows for members the
    /// channel has no row for), audits it and nudges the worker. Nothing is written on any outcome but
    /// <see cref="ChatLogBackfillEnqueueStatus.Enqueued"/>.
    /// </summary>
    Task<ChatLogBackfillEnqueueResult> EnqueueAsync(
        string channelName, string emoteSetId, int months, DateOnly todayUtc, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>Cancels the channel's active (queued, running or paused) run; audited.</summary>
    Task<ChatLogBackfillCancelResult> CancelAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// The channel's import coverage for one scope (D34): <see cref="EmoteSetScope.Set"/> counts only the
    /// days imported against that set, <see cref="EmoteSetScope.AllSets"/> every covered day, and
    /// <see cref="EmoteSetScope.ActiveSet"/> resolves to the channel's current active set. An unknown
    /// channel reads as "nothing imported".
    /// </summary>
    Task<ChatLogBackfillCoverageDto> GetCoverageAsync(string channelId, EmoteSetScope scope, CancellationToken cancellationToken = default);

    // ---- Worker side (the worker is the only caller) ----

    /// <summary>Boot recovery: every <c>running</c> row back to <c>queued</c>, <c>WeeksDone</c> kept. Returns the count.</summary>
    Task<int> ResetInterruptedRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the head of the global queue (the lowest-id active run) and moves it to <c>running</c>, or
    /// returns <c>null</c> while the head is paused and not yet due (strict FIFO, D31) or nothing is
    /// queued. A head that fails the claim-time checks (channel no longer active, Twitch id unknown,
    /// channel excluded by this process's configuration, archive base URL different from this process's
    /// configuration) is failed with the matching error code, announced, and the next head is tried.
    /// </summary>
    Task<ChatLogBackfillClaim?> ClaimNextAsync(DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary><c>Status = running</c> — read before every archive request and by the status monitor (D39).</summary>
    Task<bool> IsRunActiveAsync(long runId, CancellationToken cancellationToken = default);

    /// <summary>The run's chosen set and its persisted matching snapshot, ordered by <c>EmoteId</c> (ordinal).</summary>
    Task<ChatLogBackfillSnapshot> GetSnapshotAsync(long runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces one block in one transaction (D2): deletes every imported row of the channel in
    /// <c>[blockFrom, blockToExclusive)</c> whatever its set, inserts <paramref name="rows"/> as imported
    /// rows of the run's set, marks the block's days covered and advances the run. Rolls everything back
    /// unless the run is still <c>running</c> when the progress update runs.
    /// </summary>
    Task<ChatLogBackfillBlockResult> ReplaceBlockAsync(
        long runId,
        DateOnly blockFrom,
        DateOnly blockToExclusive,
        IReadOnlyList<ChatLogBackfillAggregate> rows,
        long bytes,
        long messages,
        bool isLastBlock,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A 429: <c>running → paused</c> with the delay computed from the row's own <c>PauseCount</c>
    /// (<paramref name="retryAfter"/> wins when present, both capped), or <c>failed</c>/<c>rate_limited</c>
    /// when the row has already been paused <c>MaxConsecutivePauses</c> times on this block. Writes the
    /// provider cooldown in the same transaction (D31).
    /// </summary>
    Task<ChatLogBackfillTransition> PauseAsync(long runId, TimeSpan? retryAfter, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>The provider-wide cooldown (may lie in the past); read before every archive request and claim.</summary>
    Task<DateTime?> GetCooldownUntilAsync(CancellationToken cancellationToken = default);

    /// <summary>A failed transport attempt: <c>BlockAttempts + 1</c>, its bytes booked.</summary>
    Task<ChatLogBackfillTransition> RecordBlockAttemptAsync(long runId, long bytes, CancellationToken cancellationToken = default);

    /// <summary><c>running → failed</c> with the error code; books the failed attempt's bytes (D47).</summary>
    Task FailAsync(long runId, string errorCode, int? httpStatus, long bytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the backfill loop's session advisory lock on a connection of its own (D46) and keeps it
    /// for as long as this service instance lives: the worker resolves the instance from a scope that
    /// lives as long as its loop. <c>true</c> when this instance holds the lock (again).
    /// </summary>
    Task<bool> TryAcquireLoopLockAsync(CancellationToken cancellationToken = default);
}

/// <summary>Every way <see cref="IChatLogBackfillService.EnqueueAsync"/> can end (spec section 3).</summary>
public enum ChatLogBackfillEnqueueStatus
{
    Enqueued,
    NotFound,
    NotActive,
    ChannelExcluded,
    TwitchIdUnknown,

    /// <summary>The row read before the 7TV call is gone under the lock (purged, maybe re-created under the same login).</summary>
    ChannelGone,

    /// <summary>The row's Twitch id differs from the one read before the 7TV call (rename/merge handover).</summary>
    ChannelIdentityChanged,

    /// <summary>The requester's account was deleted while the request ran.</summary>
    RequesterGone,
    WindowEmpty,
    AlreadyActive,
    MonthsInvalid,
    SetNotMember,
    SetEmpty,
    SetTruncated,
    SevenTvUnavailable
}

/// <summary>
/// <see cref="Run"/> is non-null if and only if <see cref="Status"/> is
/// <see cref="ChatLogBackfillEnqueueStatus.Enqueued"/>; <see cref="RetryAfter"/> only ever accompanies
/// <see cref="ChatLogBackfillEnqueueStatus.SevenTvUnavailable"/>.
/// </summary>
public sealed class ChatLogBackfillEnqueueResult
{
    private ChatLogBackfillEnqueueResult(ChatLogBackfillEnqueueStatus status, ChatLogBackfillRunDto? run, TimeSpan? retryAfter)
    {
        Status = status;
        Run = run;
        RetryAfter = retryAfter;
    }

    public ChatLogBackfillEnqueueStatus Status { get; }

    public ChatLogBackfillRunDto? Run { get; }

    public TimeSpan? RetryAfter { get; }

    public static ChatLogBackfillEnqueueResult Enqueued(ChatLogBackfillRunDto run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new ChatLogBackfillEnqueueResult(ChatLogBackfillEnqueueStatus.Enqueued, run, null);
    }

    public static ChatLogBackfillEnqueueResult SevenTvUnavailable(TimeSpan? retryAfter) =>
        new(ChatLogBackfillEnqueueStatus.SevenTvUnavailable, null, retryAfter);

    public static ChatLogBackfillEnqueueResult Failed(ChatLogBackfillEnqueueStatus status)
    {
        if (status is ChatLogBackfillEnqueueStatus.Enqueued or ChatLogBackfillEnqueueStatus.SevenTvUnavailable || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Use Enqueued(run) or SevenTvUnavailable(retryAfter) for these.");
        }

        return new ChatLogBackfillEnqueueResult(status, null, null);
    }
}

public enum ChatLogBackfillCancelResult
{
    Cancelled,
    NoActiveRun,
    NotFound
}

/// <summary>The settings tab's read model (spec 5.1); serialised camelCase by the Api as is.</summary>
public sealed record ChatLogBackfillStatusDto(
    DateOnly CountingSince,
    ChatLogBackfillArchiveDto Archive,
    int RequestDelaySeconds,
    IReadOnlyList<ChatLogBackfillOptionDto> Options,
    string ActiveEmoteSetId,
    IReadOnlyList<ChatLogBackfillCoverageInterval> Coverage,
    ChatLogBackfillRunDto? ActiveRun,
    ChatLogBackfillRunDto? LastRun,
    DateOnly? ImportedFrom,
    DateOnly? ImportedTo,
    bool ImportedContiguous,
    DateTime? CooldownUntilUtc);

/// <summary>The archive the <em>next</em> run would read: the Api's configured base URL and its host.</summary>
public sealed record ChatLogBackfillArchiveDto(string Name, string Url);

/// <summary>
/// One window choice (spec 4.1). <see cref="Days"/> is never negative; an option without a single day
/// before the counting start is not <see cref="Available"/> and carries
/// <see cref="ChatLogBackfillWindow.NoDaysBeforeCountingReason"/>.
/// </summary>
public sealed record ChatLogBackfillOptionDto(
    int Months, DateOnly WindowFrom, DateOnly WindowTo, int Days, int Weeks, bool Available, string? Reason);

/// <summary>A run as the Api shows it. <see cref="Status"/> is the stored lowercase name.</summary>
public sealed record ChatLogBackfillRunDto(
    long Id,
    string Status,
    int RequestedMonths,
    DateOnly WindowFrom,
    DateOnly WindowTo,
    int WeeksDone,
    int WeeksTotal,
    int? QueuePosition,
    DateTime? PausedUntilUtc,
    DateTime RequestedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc,
    string RequestedByLogin,
    string EmoteSetId,
    string? EmoteSetName,
    int EmoteCount,
    string? ErrorCode,
    int? ErrorHttpStatus,
    long BytesReceived,
    long MessagesRead);

/// <summary>
/// A stretch of consecutive covered days <c>[From, To)</c> with one set id and one archive host.
/// <see cref="EmoteSetName"/> is filled only in the status read (from the newest still existing run
/// of the stretch), never in <see cref="ChatLogBackfillCoverageDto"/>.
/// </summary>
public sealed record ChatLogBackfillCoverageInterval(
    DateOnly From, DateOnly To, string EmoteSetId, string ArchiveHost, string? EmoteSetName = null);

/// <summary>
/// The one coverage rule (D34, D49). <see cref="ImportedFrom"/>/<see cref="ImportedTo"/> are the first
/// covered day and the last covered day + 1; <see cref="HasGaps"/> says not every day in between is
/// covered; <see cref="ContiguousFrom"/> is the start of the covered stretch that ends exactly at the
/// counting start (the channel's <c>CreatedAt</c> date), <c>null</c> when the day before it is not
/// covered. All null/false/empty when nothing is covered for the scope.
/// </summary>
public sealed record ChatLogBackfillCoverageDto(
    string? EmoteSetId,
    DateOnly? ImportedFrom,
    DateOnly? ImportedTo,
    bool HasGaps,
    DateOnly? ContiguousFrom,
    IReadOnlyList<ChatLogBackfillCoverageInterval> Intervals);

/// <summary>A claimed run, with the persisted counters it continues from (D38).</summary>
public sealed record ChatLogBackfillClaim(
    long RunId,
    string ChannelId,
    string ChannelName,
    string TwitchChannelId,
    DateOnly WindowFrom,
    DateOnly WindowTo,
    int WeeksDone,
    int WeeksTotal,
    int PauseCount,
    int BlockAttempts,
    string EmoteSetId);

/// <summary>
/// The row's state after a counter-changing call. A status other than <c>running</c> means the run was
/// cancelled, failed or purged meanwhile (a vanished row reads as <c>cancelled</c>) and the worker stops.
/// </summary>
public sealed record ChatLogBackfillTransition(
    ChatLogBackfillRunStatus Status, int PauseCount, int BlockAttempts, DateTime? PausedUntilUtc);

/// <summary>The persisted matching snapshot of a run (D10).</summary>
public sealed record ChatLogBackfillSnapshot(string EmoteSetId, IReadOnlyList<ChatLogBackfillSnapshotEmote> Emotes);

/// <summary>One snapshot member: our emote id, its alias in the chosen set, and its entry day into that set (null = no gate).</summary>
public sealed record ChatLogBackfillSnapshotEmote(string EmoteId, string Name, DateOnly? AddedToSetDay);

/// <summary>One counted cell of a block; the service stamps the run's set id and the archive source.</summary>
public sealed record ChatLogBackfillAggregate(string EmoteId, DateOnly Date, int UseCount, int BotUseCount, int SharedChatUseCount);

/// <summary>
/// What <see cref="IChatLogBackfillService.ReplaceBlockAsync"/> did (D48) — a closed set: the private
/// constructor admits only the four cases below.
/// </summary>
public abstract record ChatLogBackfillBlockResult
{
    private ChatLogBackfillBlockResult()
    {
    }

    /// <summary>The block is committed; the transition carries the reset counters (0, 0).</summary>
    public sealed record Committed(ChatLogBackfillTransition Transition) : ChatLogBackfillBlockResult;

    /// <summary>The run was no longer running at the progress update; everything rolled back.</summary>
    public sealed record RunNotActive : ChatLogBackfillBlockResult;

    /// <summary>A live row exists for an imported cell; everything rolled back.</summary>
    public sealed record LiveRowConflict : ChatLogBackfillBlockResult;

    /// <summary>The channel (and with it the run) vanished between claim and commit; everything rolled back.</summary>
    public sealed record ChannelGone : ChatLogBackfillBlockResult;
}

/// <summary>The window arithmetic of spec 4.1, shared by the status read and the enqueue.</summary>
public static class ChatLogBackfillWindow
{
    /// <summary>The block length (D5): a constant, not a key.</summary>
    public const int BlockDays = 7;

    /// <summary>The only reason an option is unavailable today.</summary>
    public const string NoDaysBeforeCountingReason = "no_days_before_counting";

    /// <summary>The window lengths a manager may choose (B2).</summary>
    public static readonly IReadOnlyList<int> AllowedMonths = [1, 3, 6];

    /// <summary>
    /// <c>[todayUtc − months (calendar), countingStart)</c> (D1). The counting start is the UTC date of
    /// the channel's <c>CreatedAt</c> and is never filled itself (B3).
    /// </summary>
    public static ChatLogBackfillOptionDto Option(int months, DateOnly todayUtc, DateOnly countingStart)
    {
        var windowFrom = todayUtc.AddMonths(-months);
        var days = Math.Max(0, countingStart.DayNumber - windowFrom.DayNumber);
        var available = days > 0;
        return new ChatLogBackfillOptionDto(
            months, windowFrom, countingStart, days, WeeksFor(days), available, available ? null : NoDaysBeforeCountingReason);
    }

    /// <summary><c>ceil(days / 7)</c>: the block planner's count, the last block shorter.</summary>
    public static int WeeksFor(int days) => (days + BlockDays - 1) / BlockDays;
}
