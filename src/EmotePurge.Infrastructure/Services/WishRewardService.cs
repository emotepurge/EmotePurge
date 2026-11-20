using System.Globalization;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Learning and keeping channel-point rewards (K6, plan #486 2.4 and 2.4a). See <see cref="IWishRewardService"/> for
/// the contract.
/// <para>
/// <b>Locks.</b> Every write opens a transaction and takes the channel row first
/// (<see cref="RowLocks.LockChannelsByIdsAsync"/>), like every other writer of channel-scoped rows: the capture sets a
/// candidate and creates wishes under the same lock, the identity merge holds it while it moves or drops the rows.
/// Only behind it does the write read the row's binding again, so an id the identity reconcile sets, changes or
/// clears meanwhile is the one the write decides on; the clock is read there too, so a write that waited past the
/// session's expiry treats it as expired (AK50). The learning row is never changed by key after a read; see
/// <see cref="WishRewardLearningStatements"/>. The read takes no lock: its one write, the cleanup of an expired
/// session, is conditional on that session.
/// </para>
/// </summary>
public sealed class WishRewardService(
    AppDbContext db,
    IWishChangedPublisher changedPublisher,
    TimeProvider timeProvider) : IWishRewardService
{
    private const string RewardTargetType = "wishReward";

    public async Task<WishRewardResult> GetAsync(
        WishCaptureAccess access, string callerTwitchUserId, bool isGlobalAdmin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(callerTwitchUserId);

        var now = Now();
        var channelId = access.ChannelId;
        var channel = await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new { c.TwitchChannelId })
            .FirstOrDefaultAsync(cancellationToken);
        if (channel is null)
        {
            return WishRewardResult.Of(WishRewardOutcome.ChannelNotFound);
        }

        if (channel.TwitchChannelId is not null && !IsConfirmedFor(access, channel.TwitchChannelId))
        {
            return WishRewardResult.Of(WishRewardOutcome.AccessDenied);
        }

        var settingsRow = await db.ChannelWishSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
        var settings = settingsRow is null ? WishService.EffectiveSettings.Defaults : WishService.ToSettings(settingsRow);
        var canEdit = WishService.CanChangeSettings(settings, channel.TwitchChannelId, callerTwitchUserId, isGlobalAdmin);
        var aliveAt = await db.WishCaptureState.AsNoTracking()
            .Where(s => s.Id == WishCaptureState.SingletonId)
            .Select(s => (DateTime?)s.LastAliveAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        // The second wall behind the filter (plan 2.4, Sol-P1-1): an unbound row gives out no K6 data, whatever the
        // tables hold. With the binding rule nothing can be captured or learned for it in the first place.
        if (channel.TwitchChannelId is null)
        {
            return new WishRewardResult(
                WishRewardOutcome.Success,
                Rewards: new WishRewardsDto(false, [], WishLimits.MaxRewardsPerChannel, null, [], canEdit, settings.PendingExpiryDays, aliveAt));
        }

        var rewards = await db.ChannelWishRewards.AsNoTracking()
            .Where(r => r.ChannelId == channelId)
            .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id)
            .Select(r => new WishRewardDto(r.Id, r.Label, r.DefaultDurationDays, r.IsActive, r.CreatedAtUtc, r.LastRedemptionSeenAtUtc))
            .ToListAsync(cancellationToken);

        var gapsFrom = now - WishLimits.CaptureGapDisplayWindow;
        var gaps = await db.WishCaptureGaps.AsNoTracking()
            .Where(g => g.ChannelId == channelId && g.ToUtc > gapsFrom)
            .OrderByDescending(g => g.FromUtc).ThenByDescending(g => g.Id)
            .Select(g => new WishCaptureGapDto(g.FromUtc, g.ToUtc, g.Reason))
            .ToListAsync(cancellationToken);

        var learning = await db.WishRewardLearnings.AsNoTracking().FirstOrDefaultAsync(l => l.ChannelId == channelId, cancellationToken);
        if (learning is not null && learning.ExpiresAtUtc <= now)
        {
            // Conditional on the session this read saw: a session started since then has another id and survives.
            await db.DeleteExpiredSessionAsync(channelId, learning.SessionId, now, cancellationToken);
            learning = null;
        }

        return new WishRewardResult(
            WishRewardOutcome.Success,
            Rewards: new WishRewardsDto(
                true, rewards, WishLimits.MaxRewardsPerChannel, learning is null ? null : ToDto(learning), gaps, canEdit,
                settings.PendingExpiryDays, aliveAt));
    }

    public async Task<WishRewardResult> StartLearningAsync(WishCaptureAccess access, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(actor);

        var channelId = access.ChannelId;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var gate = await LockAndCheckAsync(access, cancellationToken);
        if (gate.Refusal is { } refusal)
        {
            return WishRewardResult.Of(refusal);
        }

        var now = Now();

        if (await db.ChannelWishRewards.CountAsync(r => r.ChannelId == channelId, cancellationToken) >= WishLimits.MaxRewardsPerChannel)
        {
            return WishRewardResult.Of(WishRewardOutcome.RewardLimit);
        }

        // An expired row is replaced; an unexpired one is decided by the key conflict of the insert, not by a read.
        await db.DeleteExpiredLearningsAsync(now, cancellationToken, channelId);
        var sessionId = Guid.NewGuid();
        var expiresAt = now + WishLimits.LearningWindow;
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "WishRewardLearnings" ("ChannelId", "SessionId", "StartedAtUtc", "ExpiresAtUtc")
            VALUES ({channelId}, {sessionId}, {now}, {expiresAt})
            ON CONFLICT ("ChannelId") DO NOTHING
            """,
            cancellationToken);
        if (inserted == 0)
        {
            return WishRewardResult.Of(WishRewardOutcome.LearningActive);
        }

        await transaction.CommitAsync(cancellationToken);
        await changedPublisher.PublishAsync(gate.ChannelName, cancellationToken);
        return new WishRewardResult(WishRewardOutcome.Success, Learning: new WishRewardLearningDto(sessionId, now, expiresAt, null));
    }

    public async Task<WishRewardResult> ConfirmLearningAsync(
        WishCaptureAccess access, ConfirmWishRewardLearningRequest request, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        if (request.SessionId is not { } sessionId || sessionId == Guid.Empty
            || request.CandidateId is not { } candidateId || candidateId == Guid.Empty
            || NormalizeLabel(request.Label) is not { } label)
        {
            return WishRewardResult.Of(WishRewardOutcome.RequestInvalid);
        }

        if (request.DefaultDurationDays is { } requested && !IsDurationInRange(requested))
        {
            return WishRewardResult.Of(WishRewardOutcome.DurationOutOfRange);
        }

        var channelId = access.ChannelId;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var gate = await LockAndCheckAsync(access, cancellationToken);
        if (gate.Refusal is { } refusal)
        {
            return WishRewardResult.Of(refusal);
        }

        // The clock behind the lock: a write that waited past a deadline decides on the time it decides (AK50).
        var now = Now();

        // The candidate's tag, read only to build the reward. The delete below names session and candidate id; the
        // capture draws a new candidate id with every candidate and never changes a set one, so a hit deletes exactly
        // the candidate the manager saw, with exactly this tag (named as well, as a belt).
        var tag = await db.WishRewardLearnings.AsNoTracking()
            .Where(l => l.ChannelId == channelId && l.SessionId == sessionId && l.CandidateId == candidateId)
            .Select(l => l.CandidateRewardId)
            .FirstOrDefaultAsync(cancellationToken);
        var deleted = tag is null
            ? 0
            : await db.WishRewardLearnings
                .Where(l => l.ChannelId == channelId && l.SessionId == sessionId && l.CandidateId == candidateId
                    && l.CandidateRewardId == tag && l.ExpiresAtUtc > now)
                .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            var outcome = await DiagnoseMissedConfirmAsync(channelId, sessionId, now, cancellationToken);

            // Commits only the cleanup of an expired row, if there was one (AK50: the row is gone afterwards).
            await transaction.CommitAsync(cancellationToken);
            return WishRewardResult.Of(outcome);
        }

        if (await db.ChannelWishRewards.AnyAsync(r => r.ChannelId == channelId && r.RewardId == tag, cancellationToken))
        {
            return WishRewardResult.Of(WishRewardOutcome.AlreadyKnown);
        }

        if (await db.ChannelWishRewards.CountAsync(r => r.ChannelId == channelId, cancellationToken) >= WishLimits.MaxRewardsPerChannel)
        {
            return WishRewardResult.Of(WishRewardOutcome.RewardLimit);
        }

        var days = request.DefaultDurationDays ?? (await LoadSettingsAsync(channelId, cancellationToken)).DefaultDurationDays;
        var reward = new ChannelWishReward
        {
            ChannelId = channelId,
            RewardId = tag!,
            Label = label,
            DefaultDurationDays = days,
            IsActive = true,
            CreatedAtUtc = now
        };
        db.ChannelWishRewards.Add(reward);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Belt behind the check above: the unique (ChannelId, RewardId) index.
            return WishRewardResult.Of(WishRewardOutcome.AlreadyKnown);
        }

        AddRewardAudit(actor, AuditActions.WishRewardAdd, gate.ChannelName, reward.Id, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await changedPublisher.PublishAsync(gate.ChannelName, cancellationToken);
        return new WishRewardResult(WishRewardOutcome.Success, Reward: ToDto(reward));
    }

    public async Task<WishRewardResult> CancelLearningAsync(
        WishCaptureAccess access, Guid? sessionId, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(actor);

        if (sessionId is not { } session || session == Guid.Empty)
        {
            return WishRewardResult.Of(WishRewardOutcome.RequestInvalid);
        }

        var channelId = access.ChannelId;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var gate = await LockAndCheckAsync(access, cancellationToken);
        if (gate.Refusal is { } refusal)
        {
            return WishRewardResult.Of(refusal);
        }

        var now = Now();

        var deleted = await db.WishRewardLearnings
            .Where(l => l.ChannelId == channelId && l.SessionId == session)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            await changedPublisher.PublishAsync(gate.ChannelName, cancellationToken);
            return WishRewardResult.Of(WishRewardOutcome.Success);
        }

        var lying = await db.WishRewardLearnings.AsNoTracking()
            .Where(l => l.ChannelId == channelId)
            .Select(l => new { l.SessionId, l.ExpiresAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        if (lying is null)
        {
            // Idempotent: nothing to cancel any more.
            return WishRewardResult.Of(WishRewardOutcome.Success);
        }

        if (lying.ExpiresAtUtc <= now)
        {
            // An expired row of another session counts as absent; clearing it is the housekeeping's job brought forward.
            await db.DeleteExpiredSessionAsync(channelId, lying.SessionId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return WishRewardResult.Of(WishRewardOutcome.Success);
        }

        return WishRewardResult.Of(WishRewardOutcome.LearningStale);
    }

    public async Task<WishRewardResult> UpdateRewardAsync(
        WishCaptureAccess access, long rewardRowId, UpdateWishRewardRequest request, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        string? label = null;
        if (request.Label is not null && (label = NormalizeLabel(request.Label)) is null)
        {
            return WishRewardResult.Of(WishRewardOutcome.RequestInvalid);
        }

        if (request.DefaultDurationDays is { } requested && !IsDurationInRange(requested))
        {
            return WishRewardResult.Of(WishRewardOutcome.DurationOutOfRange);
        }

        var channelId = access.ChannelId;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var gate = await LockAndCheckAsync(access, cancellationToken);
        if (gate.Refusal is { } refusal)
        {
            return WishRewardResult.Of(refusal);
        }

        var now = Now();

        var reward = await db.ChannelWishRewards.FirstOrDefaultAsync(r => r.Id == rewardRowId && r.ChannelId == channelId, cancellationToken);
        if (reward is null)
        {
            return WishRewardResult.Of(WishRewardOutcome.RewardNotFound);
        }

        var changed = new List<string>(3);
        if (label is not null && !string.Equals(label, reward.Label, StringComparison.Ordinal))
        {
            reward.Label = label;
            changed.Add("label");
        }

        if (request.DefaultDurationDays is { } days && days != reward.DefaultDurationDays)
        {
            reward.DefaultDurationDays = days;
            changed.Add("defaultDurationDays");
        }

        if (request.IsActive is { } isActive && isActive != reward.IsActive)
        {
            reward.IsActive = isActive;
            changed.Add("isActive");
        }

        if (changed.Count == 0)
        {
            return new WishRewardResult(WishRewardOutcome.Success, Reward: ToDto(reward));
        }

        AddRewardAudit(actor, AuditActions.WishRewardChange, gate.ChannelName, reward.Id, now, changed);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await changedPublisher.PublishAsync(gate.ChannelName, cancellationToken);
        return new WishRewardResult(WishRewardOutcome.Success, Reward: ToDto(reward));
    }

    public async Task<WishRewardResult> DeleteRewardAsync(
        WishCaptureAccess access, long rewardRowId, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(actor);

        var channelId = access.ChannelId;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var gate = await LockAndCheckAsync(access, cancellationToken);
        if (gate.Refusal is { } refusal)
        {
            return WishRewardResult.Of(refusal);
        }

        var now = Now();

        // The wishes of the reward stay; the foreign key's SET NULL clears their RewardId.
        var deleted = await db.ChannelWishRewards
            .Where(r => r.Id == rewardRowId && r.ChannelId == channelId)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            return WishRewardResult.Of(WishRewardOutcome.RewardNotFound);
        }

        AddRewardAudit(actor, AuditActions.WishRewardRemove, gate.ChannelName, rewardRowId, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await changedPublisher.PublishAsync(gate.ChannelName, cancellationToken);
        return WishRewardResult.Of(WishRewardOutcome.Success);
    }

    // The channel row lock, and behind it the binding the write decides on (plan 2.4: "nach dem Erwerb der
    // Kanalsperre erneut gelesen"). A row merged or purged while this waited is gone.
    private async Task<Gate> LockAndCheckAsync(WishCaptureAccess access, CancellationToken cancellationToken)
    {
        var channelId = access.ChannelId;
        var locked = await db.LockChannelsByIdsAsync([channelId], cancellationToken);
        if (locked.Count == 0)
        {
            return new Gate(WishRewardOutcome.ChannelNotFound, string.Empty);
        }

        var channel = await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new { c.ChannelName, c.TwitchChannelId })
            .FirstAsync(cancellationToken);
        if (channel.TwitchChannelId is null)
        {
            return new Gate(WishRewardOutcome.ChannelUnbound, channel.ChannelName);
        }

        return IsConfirmedFor(access, channel.TwitchChannelId)
            ? new Gate(null, channel.ChannelName)
            : new Gate(WishRewardOutcome.AccessDenied, channel.ChannelName);
    }

    // What lies when the conditional delete of a confirm hit nothing (plan 2.4): absent, expired (deleted here) or
    // without a candidate is "no candidate"; another session or another candidate is "stale".
    private async Task<WishRewardOutcome> DiagnoseMissedConfirmAsync(
        string channelId, Guid sessionId, DateTime now, CancellationToken cancellationToken)
    {
        var lying = await db.WishRewardLearnings.AsNoTracking()
            .Where(l => l.ChannelId == channelId)
            .Select(l => new { l.SessionId, l.CandidateId, l.ExpiresAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        if (lying is null)
        {
            return WishRewardOutcome.NoCandidate;
        }

        if (lying.ExpiresAtUtc <= now)
        {
            await db.DeleteExpiredSessionAsync(channelId, lying.SessionId, now, cancellationToken);
            return WishRewardOutcome.NoCandidate;
        }

        if (lying.SessionId != sessionId)
        {
            return WishRewardOutcome.LearningStale;
        }

        return lying.CandidateId is null ? WishRewardOutcome.NoCandidate : WishRewardOutcome.LearningStale;
    }

    private async Task<WishService.EffectiveSettings> LoadSettingsAsync(string channelId, CancellationToken cancellationToken)
    {
        var row = await db.ChannelWishSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
        return row is null ? WishService.EffectiveSettings.Defaults : WishService.ToSettings(row);
    }

    // Details carry the row id and the names of changed fields only: no label, no tag, no text, no login (AK64).
    private void AddRewardAudit(
        AuditActor actor, string action, string channelName, long rewardRowId, DateTime now, IReadOnlyList<string>? changedFields = null)
    {
        object details = changedFields is null
            ? new { rewardRowId }
            : new { rewardRowId, fields = changedFields };
        db.AddAuditEntry(
            actor,
            action,
            channelName: channelName,
            targetType: RewardTargetType,
            targetId: rewardRowId.ToString(CultureInfo.InvariantCulture),
            details: details,
            occurredAtUtc: now);
    }

    // At database precision (timestamptz, microseconds), so an answer carries exactly what was stored.
    private DateTime Now()
    {
        var ticks = timeProvider.GetUtcNow().UtcTicks;
        return new DateTime(ticks - (ticks % (TimeSpan.TicksPerMillisecond / 1000)), DateTimeKind.Utc);
    }

    private static bool IsConfirmedFor(WishCaptureAccess access, string rowTwitchChannelId) =>
        access.IdConfirmed && string.Equals(access.TwitchChannelId, rowTwitchChannelId, StringComparison.Ordinal);

    // Trimmed, 1 to 50 code points (Postgres' varchar counts characters, not UTF-16 units), no control characters.
    private static string? NormalizeLabel(string? label)
    {
        var trimmed = label?.Trim();
        return trimmed is null
            || trimmed.EnumerateRunes().Count() is < 1 or > WishLimits.RewardLabelMaxLength
            || trimmed.Any(char.IsControl)
            ? null
            : trimmed;
    }

    private static bool IsDurationInRange(int days) => days is >= WishLimits.DurationDaysMin and <= WishLimits.DurationDaysMax;

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static WishRewardLearningDto ToDto(WishRewardLearning l) => new(
        l.SessionId,
        l.StartedAtUtc,
        l.ExpiresAtUtc,
        l.CandidateId is { } candidateId
            ? new WishRewardCandidateDto(candidateId, l.CandidateText ?? string.Empty, l.CandidateLogin ?? string.Empty, l.CandidateSeenAtUtc!.Value)
            : null);

    private static WishRewardDto ToDto(ChannelWishReward r) =>
        new(r.Id, r.Label, r.DefaultDurationDays, r.IsActive, r.CreatedAtUtc, r.LastRedemptionSeenAtUtc);

    // A refusal decided behind the channel lock, or none; the channel's name for the event and the audit entry.
    private sealed record Gate(WishRewardOutcome? Refusal, string ChannelName);
}
