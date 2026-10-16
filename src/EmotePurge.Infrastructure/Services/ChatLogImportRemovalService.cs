using System.Diagnostics;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Removes a channel's imported backfill data (#384, spec 5.3). See <see cref="IChatLogImportRemovalService"/>
/// for the contract; the remarks here are about locks.
/// <para>
/// <b>Lock order</b> is the project's: the channel row <c>FOR NO KEY UPDATE</c> first (by name, the
/// leave's loader), then the emotes that carry imported rows <c>FOR NO KEY UPDATE</c> through
/// <see cref="RowLocks.LockEmotesByIdsAsync"/>, and only then any usage, slot or coverage row. The channel
/// lock serializes with the enqueue (the only writer that creates an active run) and the REST reconcile;
/// under it "no active run" stays true, and with it the coverage stays as read (only the block commit of
/// an active run writes coverage). The emote locks serialize with the settle and with the block commit of
/// a just cancelled run, which holds the same emotes <c>FOR SHARE</c> before it touches a usage row — so
/// neither can wait for this transaction while holding something it needs.
/// </para>
/// </summary>
public sealed class ChatLogImportRemovalService(
    AppDbContext db,
    IRedisPublisher redisPublisher,
    ILogger<ChatLogImportRemovalService> logger) : IChatLogImportRemovalService
{
    // Attempts in total on a deadlock (40P01): the enqueue's and the broadcaster purge's policy.
    private const int MaxAttempts = 3;

    private static readonly ChatLogBackfillRunStatus[] ActiveStatuses =
        [ChatLogBackfillRunStatus.Queued, ChatLogBackfillRunStatus.Running, ChatLogBackfillRunStatus.Paused];

    public async Task<ChatLogImportRemovalResult> RemoveImportedAsync(
        string channelName,
        IReadOnlyList<ExpectedCoverageInterval> expectedCoverage,
        AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(expectedCoverage);
        ArgumentNullException.ThrowIfNull(actor);

        var started = Stopwatch.GetTimestamp();

        // The whole transaction is the retry unit. A failed attempt has rolled back on dispose; the next
        // one starts from an empty change tracker and repeats every check under the locks. The last
        // deadlock propagates (a 500 for the Api, nothing written).
        Outcome outcome;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                outcome = await RemoveUnderLocksAsync(channelName, expectedCoverage, actor, cancellationToken);
                break;
            }
            catch (Exception ex) when (DatabaseErrors.IsDeadlock(ex) && attempt < MaxAttempts)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning(
                    "Removing imported backfill data hit a database deadlock (attempt {Attempt} of {MaxAttempts}); retrying.",
                    attempt, MaxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(20, 150)), cancellationToken);
            }
        }

        var result = outcome.Result;
        if (result.Status == ChatLogImportRemovalStatus.Removed)
        {
            // After the commit, for the name of the row that was locked, never the request's spelling.
            var normalized = ChannelName.Normalize(outcome.ChannelName!);
            await PublishSafelyAsync(new LiveEvent(LiveEvents.BackfillProgress, normalized), cancellationToken);
            await PublishSafelyAsync(new LiveEvent(LiveEvents.UsageFlushed, normalized), cancellationToken);
            logger.LogInformation(
                "Removed imported backfill data of channel {Channel}: {UsageRows} usage rows, {CoverageDays} coverage days, {Seeds} rowless seeds in {DurationMs} ms.",
                normalized,
                result.RemovedUsageRows,
                result.RemovedCoverageDays,
                result.RemovedSeeds,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        else if (result.Status == ChatLogImportRemovalStatus.ScopeChanged)
        {
            logger.LogInformation(
                "Removing imported backfill data of channel {Channel} refused: the confirmed coverage ({ExpectedDays} days) differs from the current one ({CurrentDays} days).",
                ChannelName.Normalize(outcome.ChannelName!),
                outcome.ExpectedDays,
                outcome.CurrentDays);
        }
        else
        {
            logger.LogDebug(
                "Removing imported backfill data of channel {Channel} ended as {Status}.",
                ChannelName.Normalize(channelName),
                result.Status);
        }

        return result;
    }

    /// <summary>One attempt: spec 5.3 steps 1–10, committed together or not at all.</summary>
    private async Task<Outcome> RemoveUnderLocksAsync(
        string channelName,
        IReadOnlyList<ExpectedCoverageInterval> expectedCoverage,
        AuditActor actor,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // 1. The channel row. Null also when a purge holding FOR UPDATE committed while this waited.
        var channel = await db.LoadChannelForNoKeyUpdateAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return Outcome.Of(ChatLogImportRemovalStatus.NotFound);
        }

        // 2. An active run: stable under the channel lock (only the enqueue creates one, under the same lock).
        if (await db.ChatLogBackfillRuns.AnyAsync(r => r.ChannelId == channel.Id && ActiveStatuses.Contains(r.Status), cancellationToken))
        {
            return Outcome.Of(ChatLogImportRemovalStatus.RunActive);
        }

        // 3. The emotes that carry imported rows. Stable without an active run: only a block commit writes
        // imported rows, and a settle moves them only between slots of the same emote.
        var emoteIds = await db.Emotes
            .Where(e => e.ChannelId == channel.Id
                && db.UsageStats.Any(u => u.EmoteId == e.Id && u.Source == UsageStatSource.ChatLogArchive))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
        var currentDays = await db.ChatLogBackfillCoverage
            .Where(d => d.ChannelId == channel.Id)
            .Select(d => new ImportScopeDay(d.Day, d.EmoteSetId, d.ArchiveHost))
            .ToListAsync(cancellationToken);

        // 4. Nothing there: idempotent whatever the expectation says (D9), checked before it.
        if (emoteIds.Count == 0 && currentDays.Count == 0)
        {
            return Outcome.Of(ChatLogImportRemovalStatus.NothingToRemove);
        }

        // 5. The confirmed coverage, compared day by day (D15).
        var comparison = ChatLogImportScope.Compare(
            expectedCoverage.Select(i => new ImportScopeInterval(i.From, i.ToExclusive, i.EmoteSetId, i.ArchiveHost)).ToList(),
            currentDays);
        if (!comparison.Matches)
        {
            return new Outcome(
                ChatLogImportRemovalResult.WithoutCounts(ChatLogImportRemovalStatus.ScopeChanged),
                channel.ChannelName,
                comparison.ExpectedDays,
                comparison.CurrentDays);
        }

        // 6. The emote rows, before any usage row is touched.
        await db.LockEmotesByIdsAsync(emoteIds, cancellationToken);

        // 7. The imported rows, counted per slot.
        var deletedPerSlot = await db.Database.SqlQueryRaw<SlotRows>(
            ChatLogImportRemovalSql.DeleteImportedUsageRowsSql,
            [
                new NpgsqlParameter("channelId", NpgsqlDbType.Text) { Value = channel.Id },
                new NpgsqlParameter("importedSource", NpgsqlDbType.Integer) { Value = (int)UsageStatSource.ChatLogArchive },
            ]).ToListAsync(cancellationToken);
        var removedUsageRows = deletedPerSlot.Sum(r => r.Rows);

        // 8. The seeds step 7 left rowless — a statement of its own, so it sees step 7's deletes.
        var candidates = deletedPerSlot
            .Where(r => r.SlotId is not null)
            .Select(r => r.SlotId!)
            .ToArray();
        var removedSeeds = candidates.Length == 0
            ? 0
            : await db.Database.ExecuteSqlRawAsync(
                ChatLogImportRemovalSql.DeleteRowlessCandidateSeedsSql,
                [new NpgsqlParameter("candidates", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = candidates }],
                cancellationToken);

        // 9. The coverage.
        var removedCoverageDays = await db.Database.ExecuteSqlRawAsync(
            ChatLogImportRemovalSql.DeleteCoverageSql,
            [new NpgsqlParameter("channelId", NpgsqlDbType.Text) { Value = channel.Id }],
            cancellationToken);

        if (removedUsageRows + removedCoverageDays == 0)
        {
            // Unreachable under the locks (step 3/4 found something); kept total rather than audit a no-op.
            return Outcome.Of(ChatLogImportRemovalStatus.NothingToRemove);
        }

        // 10. The audit entry commits with the deletes.
        db.AddAuditEntry(
            actor,
            AuditActions.BackfillRemoveImported,
            channelName: channel.ChannelName,
            details: new Dictionary<string, object> { [AuditLogDetail.Kinds.BackfillRemovedDays] = removedCoverageDays });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new Outcome(
            ChatLogImportRemovalResult.Removed(removedUsageRows, removedCoverageDays, removedSeeds),
            channel.ChannelName,
            comparison.ExpectedDays,
            comparison.CurrentDays);
    }

    private async Task PublishSafelyAsync(LiveEvent liveEvent, CancellationToken cancellationToken)
    {
        try
        {
            await redisPublisher.PublishAsync(LiveEvents.Channel, liveEvent.Serialize(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Publishing {EventType} after removing imported backfill data failed; the removal is committed and the next poll converges.",
                liveEvent.Type);
        }
    }

    private sealed record Outcome(ChatLogImportRemovalResult Result, string? ChannelName, long ExpectedDays, long CurrentDays)
    {
        public static Outcome Of(ChatLogImportRemovalStatus status) => new(ChatLogImportRemovalResult.WithoutCounts(status), null, 0, 0);
    }

    // One row of step 7's result. SlotId is nullable only defensively: after M2 every usage row has a slot.
    private sealed class SlotRows
    {
        public string? SlotId { get; set; }
        public int Rows { get; set; }
    }
}
