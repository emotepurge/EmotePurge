using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.ChatLogArchive;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The chat-log backfill's database side (#346, spec section 3). See <see cref="IChatLogBackfillService"/>
/// for the contract; the remarks here are about locks.
/// <para>
/// <b>Lock order.</b> Every path that touches a channel and one of its runs takes the channel row first:
/// the enqueue (<c>FOR NO KEY UPDATE</c> by id, then the requester's user row <c>FOR SHARE</c>, then its
/// inserts), the leave (<c>FOR NO KEY UPDATE</c>, then the run), and the block commit
/// (<c>FOR KEY SHARE</c> on the channel before any usage, coverage or run row). The block commit's
/// explicit <c>KEY SHARE</c> is what keeps it from deadlocking with a purge: without it, the usage
/// insert would hold <c>KEY SHARE</c> on emote rows before the coverage insert asked for the channel,
/// while the purge holds the channel <c>FOR UPDATE</c> and waits for those emote rows.
/// </para>
/// </summary>
public sealed class ChatLogBackfillService(
    AppDbContext db,
    IRedisPublisher redisPublisher,
    IExcludedChannelFilter excludedChannelFilter,
    ITrackedEmoteSetMembershipService membershipService,
    IForeignEmoteSetService foreignEmoteSetService,
    ChatLogBackfillOptions backfillOptions,
    ChatLogArchiveOptions archiveOptions,
    TimeProvider timeProvider,
    ILogger<ChatLogBackfillService> logger) : IChatLogBackfillService, IAsyncDisposable, IDisposable
{
    /// <summary>The partial unique index that allows one active run per channel (B10).</summary>
    public const string ActiveRunIndexName = "IX_ChatLogBackfillRuns_ChannelId_Active";

    /// <summary>Claim-time failure: the channel is no longer observed.</summary>
    public const string ChannelNotActiveErrorCode = "channel_not_active";

    /// <summary>Claim-time failure: the identity reconcile has not resolved the channel's Twitch id.</summary>
    public const string TwitchIdUnknownErrorCode = "twitch_id_unknown";

    /// <summary>Claim-time failure: the channel is on this process's excluded-channel list (D32).</summary>
    public const string ChannelExcludedErrorCode = "channel_excluded";

    /// <summary>Claim-time failure: the run was requested against another archive than this process reads (D45).</summary>
    public const string ArchiveMismatchErrorCode = "archive_mismatch";

    /// <summary>The 11th consecutive 429 on one block (§4.5).</summary>
    public const string RateLimitedErrorCode = "rate_limited";

    // pg_try_advisory_lock(hashtext(...)) key of the worker's single loop (D46).
    private const string LoopLockKey = "emotepurge:chatlog-backfill";

    // The audit target type the existing emote-set targets use.
    private const string EmoteSetTargetType = "emoteSet";

    // Attempts in total for the enqueue's final transaction on a deadlock (the broadcaster purge's policy).
    private const int EnqueueMaxAttempts = 3;

    // The base of the 429 back-off: 60 s * 2^PauseCount, capped by MaxRetryAfterSeconds (§4.5).
    private const int BasePauseSeconds = 60;

    private static readonly ChatLogBackfillRunStatus[] ActiveStatuses =
        [ChatLogBackfillRunStatus.Queued, ChatLogBackfillRunStatus.Running, ChatLogBackfillRunStatus.Paused];

    private static readonly ChatLogBackfillRunStatus[] TerminalStatuses =
        [ChatLogBackfillRunStatus.Completed, ChatLogBackfillRunStatus.Failed, ChatLogBackfillRunStatus.Cancelled];

    // The dedicated connection that holds the loop's advisory lock; the lock dies with it.
    private NpgsqlConnection? loopLockConnection;

    // ---------------------------------------------------------------- Api side

    public async Task<ChatLogBackfillStatusDto?> GetStatusAsync(
        string channelName, DateOnly todayUtc, CancellationToken cancellationToken = default)
    {
        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return null;
        }

        var countingStart = CountingStart(channel);
        var options = ChatLogBackfillWindow.AllowedMonths
            .Select(months => ChatLogBackfillWindow.Option(months, todayUtc, countingStart))
            .ToList();

        var activeRun = await db.ChatLogBackfillRuns.AsNoTracking()
            .Where(r => r.ChannelId == channel.Id && ActiveStatuses.Contains(r.Status))
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var lastRun = await db.ChatLogBackfillRuns.AsNoTracking()
            .Where(r => r.ChannelId == channel.Id && TerminalStatuses.Contains(r.Status))
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var days = await LoadCoverageDaysAsync(channel.Id, setId: null, cancellationToken);
        var allSets = Summarize(days, countingStart, emoteSetId: null);
        var intervals = await NameIntervalsAsync(allSets.Intervals, days, cancellationToken);

        var now = UtcNow();
        var cooldown = await GetCooldownUntilAsync(cancellationToken);

        return new ChatLogBackfillStatusDto(
            countingStart,
            new ChatLogBackfillArchiveDto(ArchiveHost(archiveOptions.BaseUrl), archiveOptions.BaseUrl),
            backfillOptions.RequestDelaySeconds,
            options,
            channel.ActiveEmoteSetId,
            intervals,
            activeRun is null ? null : await ToDtoAsync(activeRun, await CountSnapshotAsync(activeRun.Id, cancellationToken), cancellationToken),
            lastRun is null ? null : await ToDtoAsync(lastRun, await CountSnapshotAsync(lastRun.Id, cancellationToken), cancellationToken),
            allSets.ImportedFrom,
            allSets.ImportedTo,
            allSets.ImportedFrom is not null && !allSets.HasGaps,
            cooldown > now ? cooldown : null);
    }

    public async Task<ChatLogBackfillEnqueueResult> EnqueueAsync(
        string channelName, string emoteSetId, int months, DateOnly todayUtc, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrEmpty(emoteSetId);

        // Pre-checks, unlocked (spec section 3, in this order). The row read here is the one the
        // transaction below locks by id; its Twitch id is the identity the 7TV read is made for.
        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.NotFound);
        }

        if (!channel.IsBotActive)
        {
            return ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.NotActive);
        }

        if (channel.TwitchChannelId is not { } capturedTwitchChannelId)
        {
            return ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.TwitchIdUnknown);
        }

        if (excludedChannelFilter.IsExcluded(capturedTwitchChannelId))
        {
            // No channel name: the objection must not be tied to a channel in the logs (D32).
            logger.LogWarning("Chat-log backfill refused: the channel is on the excluded-channel list.");
            return ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.ChannelExcluded);
        }

        if (!ChatLogBackfillWindow.AllowedMonths.Contains(months))
        {
            return ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.MonthsInvalid);
        }

        var window = ChatLogBackfillWindow.Option(months, todayUtc, CountingStart(channel));
        if (!window.Available)
        {
            return ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.WindowEmpty);
        }

        if (await HasActiveRunAsync(channel.Id, cancellationToken))
        {
            return ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.AlreadyActive);
        }

        switch (await membershipService.CheckAsync(channel.ChannelName, emoteSetId, cancellationToken))
        {
            case TrackedEmoteSetMembership.Member:
                break;
            case TrackedEmoteSetMembership.NotMember:
                return ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.SetNotMember);
            case TrackedEmoteSetMembership.ChannelNotFound:
                return ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.NotFound);
            default:
                return ChatLogBackfillEnqueueResult.SevenTvUnavailable(null);
        }

        // The 7TV read, before any lock: it can wait for the provider budget and the network, and
        // nothing may hold a channel or user row meanwhile. The hardened service applies its cache,
        // the provider budget and the breaker.
        var lookup = await foreignEmoteSetService.GetForeignEmoteSetBySetIdAsync(
            channel.ChannelName, emoteSetId, refresh: false, cancellationToken);
        switch (lookup.Status)
        {
            case ForeignEmoteSetLookupStatus.Ok:
                break;
            case ForeignEmoteSetLookupStatus.NoActiveEmoteSet:
                // The set-id read's "7TV does not know this set": nothing the channel owns.
                return ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.SetNotMember);
            default:
                return ChatLogBackfillEnqueueResult.SevenTvUnavailable(lookup.RetryAfter);
        }

        var preview = lookup.EmoteSet!;
        if (preview.Truncated)
        {
            // Fail-closed: a partial name list would silently under-count.
            return ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.SetTruncated);
        }

        // D36: one entry per 7TV id, the last one winning — the reconcile's own rule (#341), so the
        // snapshot's alias is the name the sync would store, and the snapshot key cannot collide.
        var members = preview.Emotes
            .Reverse()
            .DistinctBy(e => e.SevenTvEmoteId, StringComparer.Ordinal)
            .Reverse()
            .ToList();
        if (members.Count == 0)
        {
            return ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.SetEmpty);
        }

        // The final transaction as one retry unit, like the broadcaster purge's: its placeholder upsert
        // takes the emote keys in SevenTvEmoteId order, but a 7TV dispatch inserting the same missing
        // emotes through EF takes them in Emote.Id (Guid) order, so with two or more shared keys each
        // can wait for the other (40P01), and the enqueue usually is the victim. A failed attempt has
        // rolled back on dispose; the next one starts from an empty change tracker and repeats every
        // check under the locks with the 7TV snapshot already read. The last deadlock propagates.
        (ChatLogBackfillEnqueueStatus Status, ChatLogBackfillRun? Run, string? ChannelName) outcome;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                outcome = await EnqueueUnderLocksAsync(
                    channel.Id, capturedTwitchChannelId, channel.ChannelName, emoteSetId, preview.EmoteSetName, months, window, members, actor,
                    cancellationToken);
                break;
            }
            catch (Exception ex) when (DatabaseErrors.IsDeadlock(ex) && attempt < EnqueueMaxAttempts)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning(
                    "Chat-log backfill enqueue hit a database deadlock (attempt {Attempt} of {MaxAttempts}); retrying.",
                    attempt, EnqueueMaxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(20, 150)), cancellationToken);
            }
        }

        if (outcome.Run is not { } run)
        {
            return ChatLogBackfillEnqueueResult.Failed(outcome.Status);
        }

        // The answer is built from what this call wrote, never by reading the row back: a purge
        // committing right after this commit cascades the run away, and the request must still report
        // the run it stored rather than fail after the fact. The queue position is a count and needs
        // no row of its own.
        var dto = await ToDtoAsync(run, run.Emotes.Count, cancellationToken);

        // Committed before announced (the order TriggerResyncAsync uses). Both messages are
        // accelerators only: the worker's idle poll finds the queued row anyway (D8) and the settings
        // page refetches on its own, so a Redis outage here must not turn a stored run into an error.
        await PublishSafelyAsync(BotCommands.Channel, $"{BotCommands.BackfillPrefix}{outcome.ChannelName}", cancellationToken);
        await PublishProgressAsync(outcome.ChannelName!, cancellationToken);

        return ChatLogBackfillEnqueueResult.Enqueued(dto);
    }

    public async Task<ChatLogBackfillCancelResult> CancelAsync(
        string channelName, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return ChatLogBackfillCancelResult.NotFound;
        }

        var now = UtcNow();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Conditional on the run still being active; the audit entry commits with it.
            var cancelled = await db.ChatLogBackfillRuns
                .Where(r => r.ChannelId == channel.Id && ActiveStatuses.Contains(r.Status))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(r => r.Status, ChatLogBackfillRunStatus.Cancelled)
                        .SetProperty(r => r.FinishedAtUtc, (DateTime?)now)
                        .SetProperty(r => r.PausedUntilUtc, (DateTime?)null),
                    cancellationToken);
            if (cancelled == 0)
            {
                return ChatLogBackfillCancelResult.NoActiveRun;
            }

            db.AddAuditEntry(actor, AuditActions.BackfillCancel, channelName: channel.ChannelName);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await PublishProgressAsync(channel.ChannelName, cancellationToken);
        return ChatLogBackfillCancelResult.Cancelled;
    }

    public async Task<ChatLogBackfillCoverageDto> GetCoverageAsync(
        string channelId, EmoteSetScope scope, CancellationToken cancellationToken = default)
    {
        var channel = await db.Channels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return Summarize([], default, scope.SetId);
        }

        string? setId = scope.IsAllSets ? null : scope.SetId ?? channel.ActiveEmoteSetId;
        if (setId is "")
        {
            // ActiveSet before the first sync: no set, so nothing can be imported against it.
            return Summarize([], default, setId);
        }

        var days = await LoadCoverageDaysAsync(channel.Id, setId, cancellationToken);
        return Summarize(days, CountingStart(channel), setId);
    }

    // ---------------------------------------------------------------- Worker side

    public Task<int> ResetInterruptedRunsAsync(CancellationToken cancellationToken = default) =>
        db.ChatLogBackfillRuns
            .Where(r => r.Status == ChatLogBackfillRunStatus.Running)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.Status, ChatLogBackfillRunStatus.Queued), cancellationToken);

    public async Task<ChatLogBackfillClaim?> ClaimNextAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        nowUtc = ToUtc(nowUtc);
        while (true)
        {
            // Strict FIFO (D31): the lowest-id active run is the head, whatever its state. A running
            // head can only be one this loop claimed and could not settle (the transition itself
            // failed); it is resumed rather than overtaken.
            var head = await db.ChatLogBackfillRuns.AsNoTracking()
                .Where(r => ActiveStatuses.Contains(r.Status))
                .OrderBy(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (head is null)
            {
                return null;
            }

            if (head.Status == ChatLogBackfillRunStatus.Paused && head.PausedUntilUtc > nowUtc)
            {
                return null;
            }

            var channel = await db.Channels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == head.ChannelId, cancellationToken);
            if (channel is null)
            {
                // Purged between the two reads; the cascade took the run along.
                continue;
            }

            var failure = ClaimFailure(head, channel);
            if (failure is not null)
            {
                var failed = await TransitionFromAsync(head, ChatLogBackfillRunStatus.Failed, failure, nowUtc, cancellationToken);
                if (failed)
                {
                    if (failure == ChannelExcludedErrorCode)
                    {
                        // No channel name, no run id that could be joined to one (D32).
                        logger.LogWarning("Chat-log backfill run failed at claim: the channel is on the excluded-channel list.");
                    }
                    else
                    {
                        logger.LogWarning("Chat-log backfill run {RunId} failed at claim: {ErrorCode}.", head.Id, failure);
                    }

                    await PublishProgressAsync(channel.ChannelName, cancellationToken);
                }

                continue;
            }

            if (!await TransitionFromAsync(head, ChatLogBackfillRunStatus.Running, errorCode: null, nowUtc, cancellationToken))
            {
                // Lost a race (a cancel or leave flipped it meanwhile): look at the new head.
                continue;
            }

            return new ChatLogBackfillClaim(
                head.Id, channel.Id, channel.ChannelName, channel.TwitchChannelId!, head.WindowFrom, head.WindowTo,
                head.WeeksDone, head.WeeksTotal, head.PauseCount, head.BlockAttempts, head.EmoteSetId);
        }
    }

    public Task<bool> IsRunActiveAsync(long runId, CancellationToken cancellationToken = default) =>
        db.ChatLogBackfillRuns.AnyAsync(r => r.Id == runId && r.Status == ChatLogBackfillRunStatus.Running, cancellationToken);

    public async Task<ChatLogBackfillSnapshot> GetSnapshotAsync(long runId, CancellationToken cancellationToken = default)
    {
        var emoteSetId = await db.ChatLogBackfillRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.EmoteSetId)
            .SingleOrDefaultAsync(cancellationToken);
        if (emoteSetId is null)
        {
            // The run is gone; an empty snapshot is the worker's snapshot_missing case.
            return new ChatLogBackfillSnapshot(string.Empty, []);
        }

        var emotes = await db.ChatLogBackfillRunEmotes.AsNoTracking()
            .Where(e => e.RunId == runId)
            .Select(e => new ChatLogBackfillSnapshotEmote(e.EmoteId, e.Name, e.AddedToSetDay))
            .ToListAsync(cancellationToken);

        // Ordinal in C#, not the database's collation (D11).
        return new ChatLogBackfillSnapshot(emoteSetId, emotes.OrderBy(e => e.EmoteId, StringComparer.Ordinal).ToList());
    }

    public async Task<ChatLogBackfillBlockResult> ReplaceBlockAsync(
        long runId,
        DateOnly blockFrom,
        DateOnly blockToExclusive,
        IReadOnlyList<ChatLogBackfillAggregate> rows,
        long bytes,
        long messages,
        bool isLastBlock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ValidateBlock(blockFrom, blockToExclusive, rows);

        var run = await db.ChatLogBackfillRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.ChannelId, r.EmoteSetId, r.ArchiveBaseUrl, r.WindowFrom, r.WindowTo, r.WeeksTotal })
            .SingleOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            // Only a purge deletes a run that is still being worked on (retention takes finished runs only).
            return new ChatLogBackfillBlockResult.ChannelGone();
        }

        // The block must be the run's own next block: aligned on the 7-day plan from WindowFrom, ending
        // where the plan ends it (7 days or at WindowTo), and isLastBlock true exactly for the last one.
        // The progress update below checks WeeksDone against the block index, so a block committed
        // twice, out of order or past the window writes nothing (RunNotActive: the conditional update
        // did not apply).
        if (!IsPlannedBlock(run.WindowFrom, run.WindowTo, run.WeeksTotal, blockFrom, blockToExclusive, isLastBlock, out var blockIndex))
        {
            logger.LogWarning(
                "Chat-log backfill run {RunId}: block {From:yyyy-MM-dd}..{To:yyyy-MM-dd} is not a block of its plan — nothing written.",
                runId, blockFrom, blockToExclusive);
            return new ChatLogBackfillBlockResult.RunNotActive();
        }

        var now = UtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The channel first (see the class remarks): a purge holding it FOR UPDATE makes this wait and
        // then find nothing; holding it here makes the purge wait for this commit.
        var channelHeld = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM "Channels" WHERE "Id" = {run.ChannelId} FOR KEY SHARE""")
            .ToListAsync(cancellationToken);
        if (channelHeld.Count == 0)
        {
            return new ChatLogBackfillBlockResult.ChannelGone();
        }

        // The flush's own rule (D25): an aggregate for an emote that no longer exists (hard-deleted
        // after the click — the snapshot has no FK) is dropped with a warning, never a failure.
        var emoteIds = rows.Select(r => r.EmoteId).Distinct(StringComparer.Ordinal).ToList();
        var existing = (await db.Emotes
                .Where(e => e.ChannelId == run.ChannelId && emoteIds.Contains(e.Id))
                .Select(e => e.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (existing.Count < emoteIds.Count)
        {
            logger.LogWarning(
                "Chat-log backfill run {RunId}: {Count} aggregates for emotes that no longer exist dropped.",
                runId, emoteIds.Count - existing.Count);
        }

        // Rows with all three counts at 0 are not written (the flush only writes days with usage).
        var kept = rows
            .Where(r => existing.Contains(r.EmoteId) && (r.UseCount > 0 || r.BotUseCount > 0 || r.SharedChatUseCount > 0))
            .ToList();

        // 1. Every imported row of the channel in the block, archived emotes and any set id included:
        // an imported day belongs to one set, the newest run's (OD-A). Live rows are never touched.
        await db.Database.ExecuteSqlAsync(
            $"""
             DELETE FROM "UsageStats"
             WHERE "Source" = {(int)UsageStatSource.ChatLogArchive}
               AND "Date" >= {blockFrom} AND "Date" < {blockToExclusive}
               AND "EmoteId" IN (SELECT "Id" FROM "Emotes" WHERE "ChannelId" = {run.ChannelId})
             """,
            cancellationToken);

        try
        {
            // 2. Plain insert, no ON CONFLICT: a unique violation can only be a live row on an imported
            // day — an invariant broken (D3), never a merge.
            if (kept.Count > 0)
            {
                await InsertAggregatesAsync(kept, run.EmoteSetId, cancellationToken);
            }

            // 3. The block's days are covered, whether or not anything matched (Fix 3, D45).
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO "ChatLogBackfillCoverage" ("ChannelId", "Day", "EmoteSetId", "ArchiveHost", "RunId", "CompletedAtUtc")
                 SELECT {run.ChannelId}, d::date, {run.EmoteSetId}, {ArchiveHost(run.ArchiveBaseUrl)}, {runId}, {now}
                 FROM generate_series({blockFrom}::timestamp, {blockToExclusive.AddDays(-1)}::timestamp, interval '1 day') AS d
                 ON CONFLICT ("ChannelId", "Day") DO UPDATE SET
                     "EmoteSetId" = EXCLUDED."EmoteSetId",
                     "ArchiveHost" = EXCLUDED."ArchiveHost",
                     "RunId" = EXCLUDED."RunId",
                     "CompletedAtUtc" = EXCLUDED."CompletedAtUtc"
                 """,
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            logger.LogWarning(
                "Chat-log backfill run {RunId}: a live usage row exists for an imported day — block rolled back.", runId);
            return new ChatLogBackfillBlockResult.LiveRowConflict();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return new ChatLogBackfillBlockResult.ChannelGone();
        }

        // 4. The progress update decides: a run cancelled (or failed) meanwhile rolls the block back.
        var advanced = await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE "ChatLogBackfillRuns" SET
                 "WeeksDone" = "WeeksDone" + 1,
                 "BytesReceived" = "BytesReceived" + {bytes},
                 "MessagesRead" = "MessagesRead" + {messages},
                 "PauseCount" = 0,
                 "BlockAttempts" = 0,
                 "Status" = CASE WHEN "WeeksDone" + 1 >= "WeeksTotal" THEN 'completed' ELSE 'running' END,
                 "FinishedAtUtc" = CASE WHEN "WeeksDone" + 1 >= "WeeksTotal" THEN {now} ELSE "FinishedAtUtc" END
             WHERE "Id" = {runId} AND "Status" = 'running' AND "WeeksDone" = {blockIndex}
             """,
            cancellationToken);
        if (advanced == 0)
        {
            return new ChatLogBackfillBlockResult.RunNotActive();
        }

        await transaction.CommitAsync(cancellationToken);
        return new ChatLogBackfillBlockResult.Committed(new ChatLogBackfillTransition(
            isLastBlock ? ChatLogBackfillRunStatus.Completed : ChatLogBackfillRunStatus.Running, 0, 0, null));
    }

    public async Task<ChatLogBackfillTransition> PauseAsync(
        long runId, TimeSpan? retryAfter, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        nowUtc = ToUtc(nowUtc);
        double? retryAfterSeconds = retryAfter is { } wait ? Math.Max(0, wait.TotalSeconds) : null;
        double maxSeconds = backfillOptions.MaxRetryAfterSeconds;
        var maxPauses = backfillOptions.MaxConsecutivePauses;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The delay is computed here, from the row's own PauseCount (D38): the caller passes no counter.
        // The 11th consecutive 429 (PauseCount already at the maximum) fails the run instead (§4.5).
        var updated = await db.Database.SqlQuery<TransitionRow>(
            $"""
             UPDATE "ChatLogBackfillRuns" SET
                 "Status" = CASE WHEN "PauseCount" >= {maxPauses} THEN 'failed' ELSE 'paused' END,
                 "PausedUntilUtc" = CASE WHEN "PauseCount" >= {maxPauses} THEN NULL
                     ELSE {nowUtc} + make_interval(secs => LEAST(COALESCE({retryAfterSeconds}, {BasePauseSeconds} * power(2, "PauseCount")), {maxSeconds})) END,
                 "PauseCount" = CASE WHEN "PauseCount" >= {maxPauses} THEN "PauseCount" ELSE "PauseCount" + 1 END,
                 "ErrorCode" = CASE WHEN "PauseCount" >= {maxPauses} THEN {RateLimitedErrorCode} ELSE "ErrorCode" END,
                 "ErrorHttpStatus" = CASE WHEN "PauseCount" >= {maxPauses} THEN 429 ELSE "ErrorHttpStatus" END,
                 "FinishedAtUtc" = CASE WHEN "PauseCount" >= {maxPauses} THEN {nowUtc} ELSE "FinishedAtUtc" END
             WHERE "Id" = {runId} AND "Status" = 'running'
             RETURNING "Status", "PauseCount", "BlockAttempts", "PausedUntilUtc"
             """)
            .ToListAsync(cancellationToken);

        var transition = updated.Count == 1 ? updated[0].ToTransition() : await ReadTransitionAsync(runId, cancellationToken);

        // The provider-wide cooldown (D31), in the same transaction. Written for every 429, also when
        // the run was cancelled meanwhile or this 429 failed it: the archive asked to wait either way.
        var cooldownUntil = transition.PausedUntilUtc is { } pausedUntil && updated.Count == 1
            ? pausedUntil
            : nowUtc.AddSeconds(Math.Min(retryAfterSeconds ?? BasePauseSeconds * Math.Pow(2, transition.PauseCount), maxSeconds));
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO "ChatLogBackfillProviderState" ("Id", "CooldownUntilUtc", "LastRateLimitedAtUtc", "UpdatedAtUtc")
             VALUES (1, {cooldownUntil}, {nowUtc}, {nowUtc})
             ON CONFLICT ("Id") DO UPDATE SET
                 "CooldownUntilUtc" = GREATEST(COALESCE("ChatLogBackfillProviderState"."CooldownUntilUtc", EXCLUDED."CooldownUntilUtc"), EXCLUDED."CooldownUntilUtc"),
                 "LastRateLimitedAtUtc" = EXCLUDED."LastRateLimitedAtUtc",
                 "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc"
             """,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return transition;
    }

    public Task<DateTime?> GetCooldownUntilAsync(CancellationToken cancellationToken = default) =>
        db.ChatLogBackfillProviderState.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.CooldownUntilUtc)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ChatLogBackfillTransition> RecordBlockAttemptAsync(
        long runId, long bytes, CancellationToken cancellationToken = default)
    {
        var updated = await db.Database.SqlQuery<TransitionRow>(
            $"""
             UPDATE "ChatLogBackfillRuns" SET
                 "BlockAttempts" = "BlockAttempts" + 1,
                 "BytesReceived" = "BytesReceived" + {bytes}
             WHERE "Id" = {runId} AND "Status" = 'running'
             RETURNING "Status", "PauseCount", "BlockAttempts", "PausedUntilUtc"
             """)
            .ToListAsync(cancellationToken);
        return updated.Count == 1 ? updated[0].ToTransition() : await ReadTransitionAsync(runId, cancellationToken);
    }

    public async Task FailAsync(long runId, string errorCode, int? httpStatus, long bytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        var now = UtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE "ChatLogBackfillRuns" SET
                 "Status" = 'failed',
                 "ErrorCode" = {errorCode},
                 "ErrorHttpStatus" = {httpStatus},
                 "FinishedAtUtc" = {now},
                 "PausedUntilUtc" = NULL,
                 "BytesReceived" = "BytesReceived" + {bytes}
             WHERE "Id" = {runId} AND "Status" = 'running'
             """,
            cancellationToken);
    }

    public async Task<bool> TryAcquireLoopLockAsync(CancellationToken cancellationToken = default)
    {
        if (await HoldsLoopLockAsync(cancellationToken))
        {
            return true;
        }

        // Unpooled: a pooled connection would go back to the pool instead of closing, and the lock
        // would outlive this instance's intent.
        var connectionString = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext(@key))", connection);
            command.Parameters.AddWithValue("key", LoopLockKey);
            var acquired = await command.ExecuteScalarAsync(cancellationToken) is true;
            if (acquired)
            {
                loopLockConnection = connection;
                return true;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return false;
    }

    public async Task<bool> HoldsLoopLockAsync(CancellationToken cancellationToken = default)
    {
        if (loopLockConnection is null)
        {
            return false;
        }

        try
        {
            // A session lock lives exactly as long as its connection; asking pg_locks on that very
            // connection proves both at once.
            await using var probe = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND granted)",
                loopLockConnection);
            if (await probe.ExecuteScalarAsync(cancellationToken) is true)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // Dead connection: the lock went with it.
        }

        // Never retaken here: a lost lock stays lost for this caller until it acquires again.
        await loopLockConnection.DisposeAsync();
        loopLockConnection = null;
        return false;
    }

    // Both disposals: a DI scope disposed synchronously throws for a service that is only
    // IAsyncDisposable, and an advisory lock left on an open connection would outlive its loop.
    public void Dispose()
    {
        loopLockConnection?.Dispose();
        loopLockConnection = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (loopLockConnection is not null)
        {
            await loopLockConnection.DisposeAsync();
            loopLockConnection = null;
        }
    }

    // ---------------------------------------------------------------- Enqueue internals

    /// <summary>
    /// The final transaction of the enqueue (D35, §3.1): locks, re-validation, placeholder rows, run,
    /// snapshot, audit — committed together or not at all.
    /// </summary>
    private async Task<(ChatLogBackfillEnqueueStatus Status, ChatLogBackfillRun? Run, string? ChannelName)> EnqueueUnderLocksAsync(
        string channelId,
        string capturedTwitchChannelId,
        string capturedChannelName,
        string emoteSetId,
        string? emoteSetName,
        int months,
        ChatLogBackfillOptionDto window,
        IReadOnlyList<ForeignEmoteRow> members,
        AuditActor actor,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // By the id read before the 7TV call, never by name: a purge and a fresh join under the same
        // login meanwhile is a different row (ChannelGone). FOR NO KEY UPDATE serializes with the leave
        // and every FOR UPDATE path without blocking foreign-key inserts (ChannelQueries).
        var channel = await db.LockChannelByIdAsync(channelId, cancellationToken);
        if (channel is null)
        {
            return (ChatLogBackfillEnqueueStatus.ChannelGone, null, null);
        }

        // The identity the 7TV read was made for: the same Twitch id under the same login. A rename or
        // a merge handover meanwhile changes one of them; the request named the old pair.
        if (!string.Equals(channel.TwitchChannelId, capturedTwitchChannelId, StringComparison.Ordinal)
            || !string.Equals(channel.ChannelName, capturedChannelName, StringComparison.Ordinal))
        {
            return (ChatLogBackfillEnqueueStatus.ChannelIdentityChanged, null, null);
        }

        // FOR SHARE: waits for a running account deletion (which takes FOR UPDATE) and blocks one
        // until this commit — so the run's requester snapshot is either pseudonymised by it or never
        // written. Taken after the channel row: the deletion locks no channel row, so there is no cycle.
        if (await db.LockUserAsync(actor.TwitchUserId, UserRowLock.ForShare, cancellationToken) is null)
        {
            return (ChatLogBackfillEnqueueStatus.RequesterGone, null, null);
        }

        if (!channel.IsBotActive)
        {
            return (ChatLogBackfillEnqueueStatus.NotActive, null, null);
        }

        if (excludedChannelFilter.IsExcluded(channel.TwitchChannelId))
        {
            logger.LogWarning("Chat-log backfill refused: the channel is on the excluded-channel list.");
            return (ChatLogBackfillEnqueueStatus.ChannelExcluded, null, null);
        }

        if (await HasActiveRunAsync(channel.Id, cancellationToken))
        {
            return (ChatLogBackfillEnqueueStatus.AlreadyActive, null, null);
        }

        var now = UtcNow();

        // §3.1 / D27: members without a row get an archived placeholder (IsPlaceholder, no archive
        // date, FirstSeenAt = the set entry). Existing rows, active or archived, stay exactly as they
        // are. The helper reports exactly the rows it created — CreatedRow (D41) must not claim a row a
        // concurrent writer inserted between any read and this statement.
        var created = await ArchivedEmoteRowUpsert.EnsureRowsReportingCreatedAsync(
            db,
            channel.Id,
            members.Select(m => new ArchivedEmoteRow(m.SevenTvEmoteId, m.Name, m.ImageUrl, m.AddedAt)).ToList(),
            now,
            cancellationToken);

        var sevenTvIds = members.Select(m => m.SevenTvEmoteId).ToList();
        var emoteIds = await db.Emotes.AsNoTracking()
            .Where(e => e.ChannelId == channel.Id && sevenTvIds.Contains(e.SevenTvEmoteId))
            .Select(e => new { e.Id, e.SevenTvEmoteId })
            .ToDictionaryAsync(e => e.SevenTvEmoteId, e => e.Id, StringComparer.Ordinal, cancellationToken);

        var run = new ChatLogBackfillRun
        {
            ChannelId = channel.Id,
            Status = ChatLogBackfillRunStatus.Queued,
            RequestedMonths = months,
            WindowFrom = window.WindowFrom,
            WindowTo = window.WindowTo,
            WeeksTotal = window.Weeks,
            EmoteSetId = emoteSetId,
            EmoteSetName = emoteSetName,
            ArchiveBaseUrl = archiveOptions.BaseUrl,
            RequestedByTwitchUserId = actor.TwitchUserId,
            RequestedByLogin = actor.Login,
            RequestedAtUtc = now,
        };

        // D10/D11/D25: the snapshot carries the alias in the chosen set (never Emote.Name) and the
        // set-entry day; written in EmoteId order.
        foreach (var member in members.OrderBy(m => emoteIds[m.SevenTvEmoteId], StringComparer.Ordinal))
        {
            run.Emotes.Add(new ChatLogBackfillRunEmote
            {
                EmoteId = emoteIds[member.SevenTvEmoteId],
                SevenTvEmoteId = member.SevenTvEmoteId,
                Name = member.Name,
                AddedToSetDay = member.AddedAt is { } addedAt ? DateOnly.FromDateTime(ToUtc(addedAt)) : null,
                CreatedRow = created.Contains(member.SevenTvEmoteId),
            });
        }

        db.ChatLogBackfillRuns.Add(run);
        db.AddAuditEntry(
            actor,
            AuditActions.BackfillRequest,
            channelName: channel.ChannelName,
            targetType: EmoteSetTargetType,
            targetId: emoteSetId,
            details: new Dictionary<string, object> { [AuditLogDetail.Kinds.BackfillMonths] = months });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ActiveRunIndexName,
        })
        {
            // The index is the last word (B10): a run that slipped past the check above.
            db.ChangeTracker.Clear();
            return (ChatLogBackfillEnqueueStatus.AlreadyActive, null, null);
        }

        await transaction.CommitAsync(cancellationToken);
        return (ChatLogBackfillEnqueueStatus.Enqueued, run, channel.ChannelName);
    }

    private Task<bool> HasActiveRunAsync(string channelId, CancellationToken cancellationToken) =>
        db.ChatLogBackfillRuns.AnyAsync(r => r.ChannelId == channelId && ActiveStatuses.Contains(r.Status), cancellationToken);

    // ---------------------------------------------------------------- Worker internals

    /// <summary>The claim-time checks with this process's configuration (D32, D45), or null.</summary>
    private string? ClaimFailure(ChatLogBackfillRun head, Channel channel)
    {
        if (!channel.IsBotActive)
        {
            return ChannelNotActiveErrorCode;
        }

        if (channel.TwitchChannelId is null)
        {
            return TwitchIdUnknownErrorCode;
        }

        if (excludedChannelFilter.IsExcluded(channel.TwitchChannelId))
        {
            return ChannelExcludedErrorCode;
        }

        return SameArchive(head.ArchiveBaseUrl, archiveOptions.BaseUrl) ? null : ArchiveMismatchErrorCode;
    }

    /// <summary>
    /// A conditional transition of a claimed head: to <c>running</c> (stamping the first start) or to
    /// <c>failed</c> with an error code. False when the row was no longer in the state it was read in.
    /// </summary>
    private async Task<bool> TransitionFromAsync(
        ChatLogBackfillRun head, ChatLogBackfillRunStatus to, string? errorCode, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var expected = head.Status;
        var query = db.ChatLogBackfillRuns.Where(r => r.Id == head.Id && r.Status == expected);
        var affected = to == ChatLogBackfillRunStatus.Running
            ? await query.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.Status, ChatLogBackfillRunStatus.Running)
                    .SetProperty(r => r.StartedAtUtc, r => r.StartedAtUtc ?? nowUtc)
                    .SetProperty(r => r.PausedUntilUtc, (DateTime?)null),
                cancellationToken)
            : await query.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.Status, to)
                    .SetProperty(r => r.ErrorCode, errorCode)
                    .SetProperty(r => r.FinishedAtUtc, (DateTime?)nowUtc)
                    .SetProperty(r => r.PausedUntilUtc, (DateTime?)null),
                cancellationToken);
        return affected == 1;
    }

    private async Task<ChatLogBackfillTransition> ReadTransitionAsync(long runId, CancellationToken cancellationToken)
    {
        var row = await db.ChatLogBackfillRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new ChatLogBackfillTransition(r.Status, r.PauseCount, r.BlockAttempts, r.PausedUntilUtc))
            .SingleOrDefaultAsync(cancellationToken);

        // A vanished row (purged) reads as cancelled: the worker only asks "still running?".
        return row ?? new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Cancelled, 0, 0, null);
    }

    private async Task InsertAggregatesAsync(
        IReadOnlyList<ChatLogBackfillAggregate> rows, string emoteSetId, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source")
            SELECT input."EmoteId", @emoteSetId, input."Date", input."UseCount", input."BotUseCount", input."SharedChatUseCount", @source
            FROM UNNEST(@emoteIds, @dates, @useCounts, @botUseCounts, @sharedChatUseCounts)
                AS input("EmoteId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
            """;

        await db.Database.ExecuteSqlRawAsync(
            sql,
            [
                new NpgsqlParameter("emoteSetId", NpgsqlDbType.Text) { Value = emoteSetId },
                new NpgsqlParameter("source", NpgsqlDbType.Integer) { Value = (int)UsageStatSource.ChatLogArchive },
                new NpgsqlParameter("emoteIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => r.EmoteId).ToArray() },
                new NpgsqlParameter("dates", NpgsqlDbType.Array | NpgsqlDbType.Date) { Value = rows.Select(r => r.Date).ToArray() },
                new NpgsqlParameter("useCounts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = rows.Select(r => r.UseCount).ToArray() },
                new NpgsqlParameter("botUseCounts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = rows.Select(r => r.BotUseCount).ToArray() },
                new NpgsqlParameter("sharedChatUseCounts", NpgsqlDbType.Array | NpgsqlDbType.Integer)
                {
                    Value = rows.Select(r => r.SharedChatUseCount).ToArray(),
                },
            ],
            cancellationToken);
    }

    // ---------------------------------------------------------------- Coverage

    private async Task<List<CoverageDay>> LoadCoverageDaysAsync(string channelId, string? setId, CancellationToken cancellationToken)
    {
        var query = db.ChatLogBackfillCoverage.AsNoTracking().Where(d => d.ChannelId == channelId);
        if (setId is not null)
        {
            query = query.Where(d => d.EmoteSetId == setId);
        }

        return await query
            .OrderBy(d => d.Day)
            .Select(d => new CoverageDay(d.Day, d.EmoteSetId, d.ArchiveHost, d.RunId))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The status read's intervals, each named after the newest still existing run of its days.</summary>
    private async Task<IReadOnlyList<ChatLogBackfillCoverageInterval>> NameIntervalsAsync(
        IReadOnlyList<ChatLogBackfillCoverageInterval> intervals, IReadOnlyList<CoverageDay> days, CancellationToken cancellationToken)
    {
        if (intervals.Count == 0)
        {
            return intervals;
        }

        var runIds = days.Where(d => d.RunId is not null).Select(d => d.RunId!.Value).Distinct().ToList();
        var names = await db.ChatLogBackfillRuns.AsNoTracking()
            .Where(r => runIds.Contains(r.Id) && r.EmoteSetName != null)
            .Select(r => new { r.Id, r.EmoteSetName })
            .ToDictionaryAsync(r => r.Id, r => r.EmoteSetName!, cancellationToken);

        return intervals
            .Select(interval =>
            {
                var newest = days
                    .Where(d => d.Day >= interval.From && d.Day < interval.To && d.RunId is { } id && names.ContainsKey(id))
                    .Select(d => d.RunId!.Value)
                    .DefaultIfEmpty()
                    .Max();
                return interval with { EmoteSetName = newest == 0 ? null : names[newest] };
            })
            .ToList();
    }

    // ---------------------------------------------------------------- Shared helpers

    private Task<int> CountSnapshotAsync(long runId, CancellationToken cancellationToken) =>
        db.ChatLogBackfillRunEmotes.CountAsync(e => e.RunId == runId, cancellationToken);

    private async Task<ChatLogBackfillRunDto> ToDtoAsync(ChatLogBackfillRun run, int emoteCount, CancellationToken cancellationToken)
    {
        // 1 = next to start (or the only run): every active run ahead of it in the global queue counts,
        // a paused head included (spec 5.1).
        int? queuePosition = run.Status == ChatLogBackfillRunStatus.Queued
            ? 1 + await db.ChatLogBackfillRuns.CountAsync(r => ActiveStatuses.Contains(r.Status) && r.Id < run.Id, cancellationToken)
            : null;

        return new ChatLogBackfillRunDto(
            run.Id,
            run.Status.ToString().ToLowerInvariant(),
            run.RequestedMonths,
            run.WindowFrom,
            run.WindowTo,
            run.WeeksDone,
            run.WeeksTotal,
            queuePosition,
            run.PausedUntilUtc,
            run.RequestedAtUtc,
            run.StartedAtUtc,
            run.FinishedAtUtc,
            run.RequestedByLogin,
            run.EmoteSetId,
            run.EmoteSetName,
            emoteCount,
            run.ErrorCode,
            run.ErrorHttpStatus,
            run.BytesReceived,
            run.MessagesRead);
    }

    private async Task PublishProgressAsync(string channelName, CancellationToken cancellationToken) =>
        await PublishSafelyAsync(
            LiveEvents.Channel, new LiveEvent(LiveEvents.BackfillProgress, ChannelName.Normalize(channelName)).Serialize(), cancellationToken);

    private async Task PublishSafelyAsync(string channel, string message, CancellationToken cancellationToken)
    {
        try
        {
            await redisPublisher.PublishAsync(channel, message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Chat-log backfill: publishing on {RedisChannel} failed; the row is committed and the next poll converges.", channel);
        }
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    // The planner's rule (§4.4, D5): block n covers [WindowFrom + 7n, min(WindowFrom + 7(n + 1), WindowTo)).
    private static bool IsPlannedBlock(
        DateOnly windowFrom, DateOnly windowTo, int weeksTotal, DateOnly blockFrom, DateOnly blockToExclusive, bool isLastBlock, out int blockIndex)
    {
        var offset = blockFrom.DayNumber - windowFrom.DayNumber;
        blockIndex = offset / ChatLogBackfillWindow.BlockDays;
        if (offset < 0 || offset % ChatLogBackfillWindow.BlockDays != 0 || blockIndex >= weeksTotal)
        {
            return false;
        }

        var plannedTo = blockFrom.AddDays(ChatLogBackfillWindow.BlockDays);
        if (plannedTo > windowTo)
        {
            plannedTo = windowTo;
        }

        return blockToExclusive == plannedTo && isLastBlock == (blockIndex + 1 == weeksTotal);
    }

    // A block the worker hands in must stay inside itself: a cell outside would escape the delete and
    // the coverage of this block (and one on or after the counting start would collide with live rows).
    private static void ValidateBlock(DateOnly blockFrom, DateOnly blockToExclusive, IReadOnlyList<ChatLogBackfillAggregate> rows)
    {
        if (blockFrom >= blockToExclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(blockToExclusive), "A block needs at least one day.");
        }

        var seen = new HashSet<(string, DateOnly)>();
        foreach (var row in rows)
        {
            if (row.Date < blockFrom || row.Date >= blockToExclusive)
            {
                throw new ArgumentOutOfRangeException(nameof(rows), $"Aggregate day {row.Date:yyyy-MM-dd} lies outside the block.");
            }

            if (!seen.Add((row.EmoteId, row.Date)))
            {
                throw new ArgumentException("One aggregate per emote and day.", nameof(rows));
            }
        }
    }

    /// <summary>
    /// D34/D49 over one scope's covered days (ascending, distinct by day). Intervals split at every gap
    /// and at every change of set or archive host; contiguity and gaps are about days only.
    /// </summary>
    private static ChatLogBackfillCoverageDto Summarize(IReadOnlyList<CoverageDay> days, DateOnly countingStart, string? emoteSetId)
    {
        if (days.Count == 0)
        {
            return new ChatLogBackfillCoverageDto(emoteSetId, null, null, false, null, []);
        }

        var intervals = new List<ChatLogBackfillCoverageInterval>();
        var start = days[0];
        var previous = days[0];
        foreach (var day in days.Skip(1))
        {
            if (day.Day != previous.Day.AddDays(1) || day.EmoteSetId != previous.EmoteSetId || day.ArchiveHost != previous.ArchiveHost)
            {
                intervals.Add(new ChatLogBackfillCoverageInterval(start.Day, previous.Day.AddDays(1), start.EmoteSetId, start.ArchiveHost));
                start = day;
            }

            previous = day;
        }

        intervals.Add(new ChatLogBackfillCoverageInterval(start.Day, previous.Day.AddDays(1), start.EmoteSetId, start.ArchiveHost));

        var importedFrom = days[0].Day;
        var importedTo = days[^1].Day.AddDays(1);
        var hasGaps = days.Count != importedTo.DayNumber - importedFrom.DayNumber;

        // The covered stretch ending exactly at the counting start; an older gap further back does not
        // matter (D49).
        DateOnly? contiguousFrom = null;
        var covered = days.Select(d => d.Day).ToHashSet();
        var cursor = countingStart.AddDays(-1);
        while (covered.Contains(cursor))
        {
            contiguousFrom = cursor;
            cursor = cursor.AddDays(-1);
        }

        return new ChatLogBackfillCoverageDto(emoteSetId, importedFrom, importedTo, hasGaps, contiguousFrom, intervals);
    }

    // The counting start: the UTC date of CreatedAt, never reset by a rejoin (B3).
    private static DateOnly CountingStart(Channel channel) => DateOnly.FromDateTime(ToUtc(channel.CreatedAt));

    private static string ArchiveHost(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl;

    private static bool SameArchive(string stored, string configured) =>
        Uri.TryCreate(stored, UriKind.Absolute, out var storedUri) && Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri)
            ? storedUri == configuredUri
            : string.Equals(stored, configured, StringComparison.Ordinal);

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private sealed record CoverageDay(DateOnly Day, string EmoteSetId, string ArchiveHost, long? RunId);

    // The RETURNING row of a counter-changing update; Status is the stored lowercase text.
    private sealed class TransitionRow
    {
        public string Status { get; set; } = string.Empty;

        public int PauseCount { get; set; }

        public int BlockAttempts { get; set; }

        public DateTime? PausedUntilUtc { get; set; }

        public ChatLogBackfillTransition ToTransition() =>
            new(Enum.Parse<ChatLogBackfillRunStatus>(Status, ignoreCase: true), PauseCount, BlockAttempts, PausedUntilUtc);
    }
}
