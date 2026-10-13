namespace EmotePurge.Core.Services;

/// <summary>
/// Removes a channel's imported chat-log backfill data on a manager's confirmed request (#384): every
/// imported usage row of the channel (any set, any day), its whole import coverage, and every unsettled
/// seed slot that the row deletion left without rows. Live rows, runs, snapshots, placeholder emotes
/// and settled slots stay.
/// <para>
/// One transaction under the channel row lock, so it serializes with the backfill enqueue, the leave,
/// the REST reconcile and every <c>FOR UPDATE</c> path. Nothing is written on any outcome but
/// <see cref="ChatLogImportRemovalStatus.Removed"/>.
/// </para>
/// </summary>
public interface IChatLogImportRemovalService
{
    /// <summary>
    /// Removes the imported data of <paramref name="channelName"/> if the channel has no active run and
    /// its current coverage is, day by day, exactly <paramref name="expectedCoverage"/> — the intervals
    /// the confirmation showed. A channel with neither imported rows nor coverage answers
    /// <see cref="ChatLogImportRemovalStatus.NothingToRemove"/> whatever the expectation says, so a
    /// repeated request is not a conflict. Audited and announced only when something was removed.
    /// </summary>
    Task<ChatLogImportRemovalResult> RemoveImportedAsync(
        string channelName,
        IReadOnlyList<ExpectedCoverageInterval> expectedCoverage,
        AuditActor actor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One interval of the coverage a manager confirmed: the days <c>[From, ToExclusive)</c> imported against
/// <see cref="EmoteSetId"/> from <see cref="ArchiveHost"/> — the fields of
/// <see cref="ChatLogBackfillCoverageInterval"/> without the set's display name.
/// </summary>
public sealed record ExpectedCoverageInterval(DateOnly From, DateOnly ToExclusive, string EmoteSetId, string ArchiveHost);

/// <summary>Every way <see cref="IChatLogImportRemovalService.RemoveImportedAsync"/> can end.</summary>
public enum ChatLogImportRemovalStatus
{
    /// <summary>At least one imported row or coverage day was removed; committed and audited.</summary>
    Removed,

    /// <summary>The channel has neither imported rows nor coverage; nothing written, nothing audited.</summary>
    NothingToRemove,

    /// <summary>The channel has a queued, running or paused run; nothing written.</summary>
    RunActive,

    /// <summary>The current coverage differs from the confirmed one by at least one day; nothing written.</summary>
    ScopeChanged,

    /// <summary>No channel holds the name (also: purged while the request waited for its lock).</summary>
    NotFound
}

/// <summary>
/// The outcome of <see cref="IChatLogImportRemovalService.RemoveImportedAsync"/>. Only
/// <see cref="ChatLogImportRemovalStatus.Removed"/> carries counters other than zero.
/// </summary>
public sealed class ChatLogImportRemovalResult
{
    private ChatLogImportRemovalResult(ChatLogImportRemovalStatus status, int removedUsageRows, int removedCoverageDays, int removedSeeds)
    {
        Status = status;
        RemovedUsageRows = removedUsageRows;
        RemovedCoverageDays = removedCoverageDays;
        RemovedSeeds = removedSeeds;
    }

    public ChatLogImportRemovalStatus Status { get; }

    /// <summary>Imported <c>UsageStats</c> rows deleted.</summary>
    public int RemovedUsageRows { get; }

    /// <summary><c>ChatLogBackfillCoverage</c> rows (days) deleted.</summary>
    public int RemovedCoverageDays { get; }

    /// <summary>Unsettled seed slots deleted because the row deletion left them without rows (log only).</summary>
    public int RemovedSeeds { get; }

    public static ChatLogImportRemovalResult Removed(int removedUsageRows, int removedCoverageDays, int removedSeeds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(removedUsageRows);
        ArgumentOutOfRangeException.ThrowIfNegative(removedCoverageDays);
        ArgumentOutOfRangeException.ThrowIfNegative(removedSeeds);
        if (removedUsageRows + removedCoverageDays == 0)
        {
            throw new ArgumentException("A removal removes at least one row or coverage day; use NothingToRemove otherwise.");
        }

        return new ChatLogImportRemovalResult(ChatLogImportRemovalStatus.Removed, removedUsageRows, removedCoverageDays, removedSeeds);
    }

    /// <summary>Every outcome but <see cref="ChatLogImportRemovalStatus.Removed"/>, all counters zero.</summary>
    public static ChatLogImportRemovalResult WithoutCounts(ChatLogImportRemovalStatus status)
    {
        if (status == ChatLogImportRemovalStatus.Removed || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Use Removed(...) for a removal.");
        }

        return new ChatLogImportRemovalResult(status, 0, 0, 0);
    }
}
