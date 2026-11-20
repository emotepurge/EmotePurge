using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The worker's half of the K6 chat capture (plan #486 2.4 and 2.7). See <see cref="IWishCaptureService"/> for the
/// contract.
/// <para>
/// <b>Locks.</b> A write opens a transaction and takes the channel row first
/// (<see cref="RowLocks.LockChannelsByIdsAsync"/>), like the reward service, the reconcile and every wish command, so
/// the capture serializes with a confirm, a reward change, a channel merge and the identity reconcile of the same
/// channel. The pending expiry locks the channel before its conditional update takes the wish rows (channel before
/// wish, plan R7). Nothing here locks more than one channel at a time.
/// </para>
/// <para>
/// <b>Decided twice, written once.</b> The read without a lock only spares a transaction for what is certainly
/// dropped; every write is decided again on the state read behind the lock, and the candidate is set by the shared
/// conditional statement (<see cref="WishRewardLearningStatements.SetCandidateAsync"/>).
/// </para>
/// </summary>
public sealed class WishCaptureService(
    AppDbContext db,
    IWishChangedPublisher changedPublisher,
    IWishReconcileTrigger reconcileTrigger,
    WishOptions options,
    TimeProvider timeProvider,
    ILogger<WishCaptureService> logger) : IWishCaptureService
{
    private const string IrcMessageIndex = "IX_Wishes_ChannelId_IrcMessageId";
    private const int ExpiryBatchSize = 500;

    // How far a redemption may have been sent before the learning session started and still become its candidate
    // (plan 2.4 step 4): the manager clicks "start" and redeems at once, and Twitch's clock is not ours.
    private static readonly TimeSpan LearningLeadTolerance = TimeSpan.FromSeconds(10);

    private static readonly string ExpiredStatus =
        WishTransitions.Target(WishStatus.Pending, null, WishAction.Expire)
        ?? throw new InvalidOperationException("The transition table has no expiry for an open wish.");

    private static readonly string NewWishStatus =
        WishTransitions.TargetForNew(WishAction.Capture)
        ?? throw new InvalidOperationException("The transition table has no state for a captured wish.");

    public async Task<WishCaptureOutcome> ProcessAsync(RewardObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (Normalize(observation) is not { } input)
        {
            return Decided(null, WishCaptureOutcome.Discarded);
        }

        var now = Now();
        var seenAt = input.SentAtUtc < now ? input.SentAtUtc : now;

        // Step 1: the read without a lock. Only what is certainly dropped ends here, without a transaction and
        // without a row in any table (AK51).
        var channel = await db.Channels.AsNoTracking()
            .Where(c => c.TwitchChannelId == input.RoomId)
            .Select(c => new ChannelRow(c.Id, c.ChannelName, c.TwitchChannelId, c.IsBotActive && c.DeactivatedAtUtc == null))
            .FirstOrDefaultAsync(cancellationToken);
        if (channel is null)
        {
            return Decided(null, WishCaptureOutcome.ChannelUnbound);
        }

        if (!channel.Capturing)
        {
            return Decided(channel.Id, WishCaptureOutcome.Discarded);
        }

        var first = await ReadStateAsync(channel.Id, input.RewardId, cancellationToken);
        if (Decide(first, seenAt, now) == Step.Drop)
        {
            return Decided(channel.Id, WishCaptureOutcome.Discarded);
        }

        // Step 2: behind the channel lock, the binding and the state again; decided only on this second read.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var locked = await db.LockChannelsByIdsAsync([channel.Id], cancellationToken);
        if (locked.Count == 0)
        {
            // Merged into another row or purged while this waited.
            return Decided(channel.Id, WishCaptureOutcome.ChannelUnbound);
        }

        var bound = await db.Channels.AsNoTracking()
            .Where(c => c.Id == channel.Id)
            .Select(c => new ChannelRow(c.Id, c.ChannelName, c.TwitchChannelId, c.IsBotActive && c.DeactivatedAtUtc == null))
            .FirstAsync(cancellationToken);
        if (!string.Equals(bound.TwitchChannelId, input.RoomId, StringComparison.Ordinal))
        {
            return Decided(channel.Id, WishCaptureOutcome.ChannelUnbound);
        }

        if (!bound.Capturing)
        {
            return Decided(channel.Id, WishCaptureOutcome.Discarded);
        }

        // The decision and the writes below take the clock behind the lock: a line that waited past the session's
        // expiry is no candidate (AK50). The moment the line was seen stays the one from before the wait.
        now = Now();
        var state = await ReadStateAsync(channel.Id, input.RewardId, cancellationToken);
        var step = Decide(state, seenAt, now);
        if (step == Step.Drop)
        {
            return Decided(channel.Id, WishCaptureOutcome.Discarded);
        }

        // Step 3: the content-free receipt, in this transaction; every refusal below rolls it back with the rest.
        var receipted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "WishCaptureReceipts" ("ChannelId", "IrcMessageId", "SeenAtUtc")
            VALUES ({channel.Id}, {input.MessageId}, {now})
            ON CONFLICT ("ChannelId", "IrcMessageId") DO NOTHING
            """,
            cancellationToken);
        if (receipted == 0)
        {
            return Decided(channel.Id, WishCaptureOutcome.Duplicate);
        }

        // Steps 4 and 5.
        var outcome = step == Step.Candidate
            ? await StoreCandidateAsync(channel.Id, state.Learning!, input, seenAt, now, cancellationToken)
            : await CreateWishAsync(channel.Id, state.Reward!, input, seenAt, now, cancellationToken);
        if (outcome is not (WishCaptureOutcome.CandidateStored or WishCaptureOutcome.WishCreated))
        {
            return Decided(channel.Id, outcome);
        }

        await transaction.CommitAsync(cancellationToken);

        // Step 6: after the commit; neither can change the answer (the publisher and the trigger swallow failures).
        await changedPublisher.PublishAsync(bound.ChannelName, cancellationToken);
        if (outcome == WishCaptureOutcome.WishCreated)
        {
            await reconcileTrigger.TriggerAsync(channel.Id, cancellationToken);
        }

        return Decided(channel.Id, outcome);
    }

    public async Task<WishCaptureHousekeepingResult> RunHousekeepingAsync(
        bool includePendingExpiry, CancellationToken cancellationToken = default)
    {
        var now = Now();

        // (a) The condition is the expiry alone, so a session started at or after "now" never meets it (T4).
        var learnings = await db.DeleteExpiredLearningsAsync(now, cancellationToken);

        // (b) Receipts carry no content; they only have to outlive every way a buffered line can come back.
        var receiptCutoff = now - WishLimits.CaptureReceiptRetention;
        var receipts = await db.WishCaptureReceipts
            .Where(r => r.SeenAtUtc < receiptCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (!includePendingExpiry)
        {
            return new WishCaptureHousekeepingResult(learnings, receipts, 0, 0, 0);
        }

        // (c) The channels that can hold an expired open wish under the shortest allowed period; each decides with
        // its own period behind its lock.
        var earliestCutoff = now.AddDays(-WishLimits.PendingExpiryDaysMin);
        var channelIds = await db.Wishes.AsNoTracking()
            .Where(w => w.Status == WishStatus.Pending && w.StatusChangedAtUtc < earliestCutoff)
            .Select(w => w.ChannelId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);

        int expired = 0, channels = 0, failed = 0;
        foreach (var channelId in channelIds)
        {
            try
            {
                var count = await ExpireChannelAsync(channelId, now, cancellationToken);
                if (count > 0)
                {
                    expired += count;
                    channels++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Counted, not logged here: the worker throttles its warnings (plan 2.3), and one channel must not
                // hold back the others.
                failed++;
            }
        }

        return new WishCaptureHousekeepingResult(learnings, receipts, expired, channels, failed);
    }

    public async Task<IReadOnlyList<WishCaptureGapChannel>> ListGapChannelsAsync(CancellationToken cancellationToken = default) =>
        await db.Channels.AsNoTracking()
            .Where(c => c.IsBotActive && c.DeactivatedAtUtc == null && c.TwitchChannelId != null
                && db.ChannelWishRewards.Any(r => r.ChannelId == c.Id && r.IsActive))
            .OrderBy(c => c.ChannelName)
            .Select(c => new WishCaptureGapChannel(c.Id, c.TwitchChannelId!, c.ChannelName))
            .ToListAsync(cancellationToken);

    public Task<DateTime?> ReadLastAliveAsync(CancellationToken cancellationToken = default) =>
        db.WishCaptureState.AsNoTracking()
            .Where(s => s.Id == WishCaptureState.SingletonId)
            .Select(s => (DateTime?)s.LastAliveAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task WriteLastAliveAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        // The row appears with the first write (T1: no seed row; without one there was never a stamp).
        var stamp = ToDatabasePrecision(nowUtc);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "WishCaptureState" ("Id", "LastAliveAtUtc")
            VALUES ({WishCaptureState.SingletonId}, {stamp})
            ON CONFLICT ("Id") DO UPDATE SET "LastAliveAtUtc" = EXCLUDED."LastAliveAtUtc"
            """,
            cancellationToken);
    }

    public async Task<int> RecordGapsAsync(IReadOnlyCollection<WishCaptureGapRecord> gaps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gaps);

        foreach (var gap in gaps)
        {
            ArgumentNullException.ThrowIfNull(gap, nameof(gaps));
            if (!WishCaptureGapReason.All.Contains(gap.Reason, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Unknown capture gap reason '{gap.Reason}'.", nameof(gaps));
            }
        }

        var kept = gaps
            .Select(g => g with { FromUtc = ToDatabasePrecision(g.FromUtc), ToUtc = ToDatabasePrecision(g.ToUtc) })
            .Where(g => g.ToUtc - g.FromUtc >= WishLimits.MinCaptureGap)
            .ToList();
        if (kept.Count == 0)
        {
            return 0;
        }

        var channelIds = kept.Select(g => g.ChannelId).ToArray();
        var froms = kept.Select(g => g.FromUtc).ToArray();
        var tos = kept.Select(g => g.ToUtc).ToArray();
        var reasons = kept.Select(g => g.Reason).ToArray();

        // One statement: "has an active reward at the time of writing" is part of it, and a failure writes nothing.
        // Not composed with any LINQ operator, so EF sends it as it is.
        var written = await db.Database
            .SqlQuery<string>(
                $"""
                INSERT INTO "WishCaptureGaps" ("ChannelId", "FromUtc", "ToUtc", "Reason")
                SELECT g."ChannelId", g."FromUtc", g."ToUtc", g."Reason"
                FROM UNNEST({channelIds}, {froms}, {tos}, {reasons}) AS g("ChannelId", "FromUtc", "ToUtc", "Reason")
                WHERE EXISTS (SELECT 1 FROM "ChannelWishRewards" r WHERE r."ChannelId" = g."ChannelId" AND r."IsActive")
                RETURNING "ChannelId" AS "Value"
                """)
            .ToListAsync(cancellationToken);
        if (written.Count == 0)
        {
            return 0;
        }

        await NotifyGapChannelsAsync(written, cancellationToken);
        return written.Count;
    }

    // The insert has committed on its own, so from here on the batch counts as written: a failure of the name read
    // for wish.changed must not make the worker pass the same gaps again and store every interval twice. The settings
    // tab converges on its next fetch; the warning carries the error kind only (no message that may quote a key).
    private async Task NotifyGapChannelsAsync(IReadOnlyCollection<string> channelIds, CancellationToken cancellationToken)
    {
        try
        {
            var distinct = channelIds.Distinct(StringComparer.Ordinal).ToArray();
            var names = await db.Channels.AsNoTracking()
                .Where(c => distinct.Contains(c.Id))
                .OrderBy(c => c.ChannelName)
                .Select(c => c.ChannelName)
                .ToListAsync(cancellationToken);
            foreach (var name in names)
            {
                await changedPublisher.PublishAsync(name, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Wish capture stored {Count} capture gaps but could not announce them ({ExceptionType}, SQLSTATE {SqlState}); clients converge on their next fetch.",
                channelIds.Count, ex.GetType().Name, SqlStateOf(ex));
        }
    }

    // Step 4: the candidate of the session read behind the lock, by the shared conditional statement.
    private async Task<WishCaptureOutcome> StoreCandidateAsync(
        string channelId, LearningRow learning, Input input, DateTime seenAt, DateTime now, CancellationToken cancellationToken)
    {
        var hit = await db.SetCandidateAsync(
            channelId, learning.SessionId, Guid.NewGuid(), input.RewardId, input.Text, input.Login, seenAt, now, cancellationToken);
        return hit == 1 ? WishCaptureOutcome.CandidateStored : WishCaptureOutcome.Discarded;
    }

    // Step 5: the limits, then the open wish. A limit is a refusal with rollback, so a later retry of the same line is
    // decided again instead of counting as a duplicate of something that never became a wish.
    private async Task<WishCaptureOutcome> CreateWishAsync(
        string channelId, RewardRow reward, Input input, DateTime seenAt, DateTime now, CancellationToken cancellationToken)
    {
        var requesterOpen = await db.Wishes.CountAsync(
            w => w.ChannelId == channelId && w.Status == WishStatus.Pending && w.RequesterTwitchUserId == input.UserId,
            cancellationToken);
        if (requesterOpen >= options.Capture.MaxPendingPerRequester)
        {
            return WishCaptureOutcome.RequesterLimit;
        }

        var channelOpen = await db.Wishes.CountAsync(w => w.ChannelId == channelId && w.Status == WishStatus.Pending, cancellationToken);
        if (channelOpen >= WishLimits.MaxPendingPerChannel)
        {
            return WishCaptureOutcome.ChannelLimit;
        }

        var parsed = WishTextParser.Parse(input.Text);
        var wish = new Wish
        {
            ChannelId = channelId,
            // Only a link names the emote; everything else carries the placeholder until a match or an approval
            // fills all three fields in one update (plan 2.8a).
            SevenTvEmoteId = parsed.SevenTvEmoteId ?? string.Empty,
            EmoteName = string.Empty,
            Alias = string.Empty,
            Source = WishSource.Irc,
            Status = NewWishStatus,
            StatusReason = parsed.MatchKind == WishMatchKind.None ? WishStatusReason.Unparsed : null,
            StatusChangedAtUtc = now,
            RequesterTwitchUserId = input.UserId,
            RequesterLogin = input.Login,
            DurationDays = reward.DefaultDurationDays,
            // Twitch's time, capped at ours: the start of the match window (plan 2.5, R16).
            CreatedAtUtc = seenAt,
            RewardId = reward.Id,
            IrcMessageId = input.MessageId,
            RequestText = input.Text,
            RequestedName = parsed.RequestedName,
            MatchKind = parsed.MatchKind
        };
        db.Wishes.Add(wish);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsIrcMessageConflict(exception))
        {
            // Belt behind the receipt: the unique (ChannelId, IrcMessageId) index of the wishes.
            return WishCaptureOutcome.Duplicate;
        }
        finally
        {
            // The context may outlive this call; nothing it tracks is read again.
            db.Entry(wish).State = EntityState.Detached;
        }

        // "Last seen" never moves back, also not for a line drained late from the queue.
        await db.ChannelWishRewards
            .Where(r => r.Id == reward.Id && (r.LastRedemptionSeenAtUtc == null || r.LastRedemptionSeenAtUtc < seenAt))
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.LastRedemptionSeenAtUtc, seenAt), cancellationToken);
        return WishCaptureOutcome.WishCreated;
    }

    // One channel's open wishes past its period: behind the channel lock, in batches, by a conditional update that
    // re-checks state and age, so a wish a command or the reconcile moved meanwhile is left alone.
    private async Task<int> ExpireChannelAsync(string channelId, DateTime now, CancellationToken cancellationToken)
    {
        var total = 0;
        string? channelName = null;
        while (true)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var locked = await db.LockChannelsByIdsAsync([channelId], cancellationToken);
            if (locked.Count == 0)
            {
                break;
            }

            channelName ??= await db.Channels.AsNoTracking()
                .Where(c => c.Id == channelId)
                .Select(c => c.ChannelName)
                .FirstAsync(cancellationToken);
            var days = await db.ChannelWishSettings.AsNoTracking()
                .Where(s => s.ChannelId == channelId)
                .Select(s => (int?)s.PendingExpiryDays)
                .FirstOrDefaultAsync(cancellationToken) ?? WishLimits.DefaultPendingExpiryDays;
            var cutoff = now.AddDays(-days);

            var ids = await db.Wishes.AsNoTracking()
                .Where(w => w.ChannelId == channelId && w.Status == WishStatus.Pending && w.StatusChangedAtUtc < cutoff)
                .OrderBy(w => w.Id)
                .Select(w => w.Id)
                .Take(ExpiryBatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            var hit = await db.Wishes
                .Where(w => ids.Contains(w.Id) && w.ChannelId == channelId && w.Status == WishStatus.Pending && w.StatusChangedAtUtc < cutoff)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(w => w.Status, ExpiredStatus)
                        .SetProperty(w => w.StatusReason, (string?)null)
                        .SetProperty(w => w.StatusChangedAtUtc, now)
                        .SetProperty(w => w.EndedAtUtc, now),
                    cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            total += hit;
            if (ids.Count < ExpiryBatchSize)
            {
                break;
            }
        }

        if (total > 0 && channelName is not null)
        {
            await changedPublisher.PublishAsync(channelName, cancellationToken);
        }

        return total;
    }

    private async Task<StateRow> ReadStateAsync(string channelId, string rewardTag, CancellationToken cancellationToken)
    {
        var reward = await db.ChannelWishRewards.AsNoTracking()
            .Where(r => r.ChannelId == channelId && r.RewardId == rewardTag)
            .Select(r => new RewardRow(r.Id, r.IsActive, r.DefaultDurationDays))
            .FirstOrDefaultAsync(cancellationToken);
        if (reward is not null)
        {
            // A known reward is never a candidate, so the learning row does not matter.
            return new StateRow(reward, null);
        }

        var learning = await db.WishRewardLearnings.AsNoTracking()
            .Where(l => l.ChannelId == channelId)
            .Select(l => new LearningRow(l.SessionId, l.StartedAtUtc, l.ExpiresAtUtc, l.CandidateId != null))
            .FirstOrDefaultAsync(cancellationToken);
        return new StateRow(null, learning);
    }

    // The debug line names the channel and the outcome only: no text, no login, no user id, no room id (AK64).
    private WishCaptureOutcome Decided(string? channelId, WishCaptureOutcome outcome)
    {
        logger.LogDebug("Wish capture decided an observation for channel {ChannelId}: {Outcome}.", channelId ?? "(none)", outcome);
        return outcome;
    }

    // At database precision (timestamptz, microseconds), so what is compared is what is stored.
    private DateTime Now() => ToDatabasePrecision(timeProvider.GetUtcNow().UtcDateTime);

    // A known, active reward → a wish (also while a session runs); a known, paused one → drop. An unknown reward →
    // the candidate, if a session waits for one, has not expired and started at most the tolerance after the line
    // was sent; otherwise drop.
    private static Step Decide(StateRow state, DateTime seenAt, DateTime now)
    {
        if (state.Reward is { } reward)
        {
            return reward.IsActive ? Step.Wish : Step.Drop;
        }

        return state.Learning is { } learning
            && learning.ExpiresAtUtc > now
            && !learning.HasCandidate
            && seenAt >= learning.StartedAtUtc - LearningLeadTolerance
            ? Step.Candidate
            : Step.Drop;
    }

    // The shape checks the columns need (the worker's parser checks the tag forms already); the text is cut to the
    // column's length in code points, which is what Postgres' varchar counts. Null = malformed, dropped.
    private static Input? Normalize(RewardObservation o)
    {
        if (string.IsNullOrEmpty(o.RoomId)
            || string.IsNullOrEmpty(o.RewardId) || o.RewardId.Length > WishLimits.RewardIdMaxLength
            || string.IsNullOrEmpty(o.MessageId) || o.MessageId.Length > WishLimits.IrcMessageIdMaxLength
            || string.IsNullOrEmpty(o.UserId)
            || string.IsNullOrEmpty(o.Login) || o.Login.Length > WishLimits.RequesterLoginMaxLength)
        {
            return null;
        }

        var text = o.Text ?? string.Empty;
        if (text.Length > WishLimits.RequestTextMaxLength)
        {
            text = string.Concat(text.EnumerateRunes().Take(WishLimits.RequestTextMaxLength).Select(r => r.ToString()));
        }

        return new Input(o.RoomId, o.RewardId, o.MessageId, o.UserId, o.Login.ToLowerInvariant(), text, ToDatabasePrecision(o.SentAtUtc));
    }

    private static DateTime ToDatabasePrecision(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => value
        };
        return new DateTime(utc.Ticks - (utc.Ticks % (TimeSpan.TicksPerMillisecond / 1000)), DateTimeKind.Utc);
    }

    private static string? SqlStateOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.SqlState;
            }
        }

        return null;
    }

    private static bool IsIrcMessageConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && string.Equals(postgres.ConstraintName, IrcMessageIndex, StringComparison.Ordinal);

    private enum Step
    {
        Drop,
        Candidate,
        Wish,
    }

    private sealed record Input(string RoomId, string RewardId, string MessageId, string UserId, string Login, string Text, DateTime SentAtUtc);

    private sealed record ChannelRow(string Id, string ChannelName, string? TwitchChannelId, bool Capturing);

    private sealed record RewardRow(long Id, bool IsActive, int DefaultDurationDays);

    private sealed record LearningRow(Guid SessionId, DateTime StartedAtUtc, DateTime ExpiresAtUtc, bool HasCandidate);

    private sealed record StateRow(RewardRow? Reward, LearningRow? Learning);
}
