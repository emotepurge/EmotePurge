using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Why a channel stopped being observed, which decides how its <c>channel.leave</c> entry reads.
/// </summary>
internal enum ChannelDeactivationReason
{
    /// <summary>A manager's own leave: the entry names the channel and carries no details.</summary>
    Leave,

    /// <summary>
    /// The identity reconcile's objection gate (excluded-channel list): the entry carries no channel
    /// name, only <c>reason = "excluded"</c>. A <c>channel.leave</c> by the system actor is written
    /// for nothing else that hides its channel, so an entry naming it would tell every admin reading
    /// the audit log which channel the objection concerns — the very thing its log lines avoid.
    /// </summary>
    Excluded,

    /// <summary>
    /// The identity reconcile found the row's Twitch id locked by its broadcaster (#245): named, with
    /// <c>reason = "locked"</c>. Unlike an objection, the lock is not secret — the broadcaster set it
    /// themselves through the app.
    /// </summary>
    Locked,

    /// <summary>
    /// The identity reconcile found an old id-less row whose login Twitch no longer knows (#245, D2):
    /// named, with <c>reason = "loginUnresolvable"</c>.
    /// </summary>
    LoginUnresolvable,
}

/// <summary>
/// The write every "stop observing this channel, keep its history" path shares: close the open
/// emote-set observation interval, flip <c>IsBotActive</c> off, stamp the retention clock, record
/// why, and publish the LEAVE command the worker needs to actually part the chat. Factored out of <see cref="ChannelService.LeaveAsync"/>
/// so <see cref="ChannelIdentityService"/>'s own deactivations (the objection gate, issue #260; the
/// broadcaster lock and unresolvable logins, #245) reuse exactly the same write rather than a
/// hand-copied one.
/// <para>
/// Two halves since #245: <see cref="StageAsync"/> only stages, <see cref="PublishLeaveAsync"/> only
/// publishes, so a caller that runs its own transaction can commit between them — a LEAVE published
/// before the commit would part the chat for a deactivation that may still roll back.
/// <see cref="DeactivateAsync"/> composes them for the callers that open no transaction.
/// </para>
/// <para>
/// <b>Lock order:</b> the channel row first, then any <c>ChatLogBackfillRuns</c> row. <see cref="DeactivateAsync"/>
/// takes the channel lock itself before <see cref="StageAsync"/> cancels the run; a caller using
/// <see cref="StageAsync"/> directly must already hold the channel row <c>FOR UPDATE</c> in its
/// transaction.
/// </para>
/// <para>
/// A plain static helper, not a shared service, and deliberately not <c>IChannelService</c> injected
/// into <see cref="ChannelIdentityService"/>: <see cref="ChannelService"/> already depends on
/// <see cref="IChannelIdentityService"/> for <c>LookupByLoginAsync</c>, so the reverse dependency
/// would be circular.
/// </para>
/// </summary>
internal static class ChannelDeactivation
{
    private const string ExcludedReason = "excluded";
    private const string LockedReason = "locked";
    private const string LoginUnresolvableReason = "loginUnresolvable";

    /// <summary>The <c>ErrorCode</c> a deactivation leaves on the chat-log backfill run it cancels (D16).</summary>
    public const string BackfillChannelLeftErrorCode = "channel_left";

    /// <summary>
    /// Stage, save, publish — for a caller that opens no transaction of its own (the manager's leave,
    /// the objection gate's deactivation). Committed before published: the row is already the source
    /// of truth, and a Redis outage would only cost this acceleration — SevenTvPeriodicResyncWorker's
    /// prune step converges on its own. A failed publish is thrown, not swallowed; a caller that must
    /// not fail on it catches it itself.
    /// </summary>
    public static async Task DeactivateAsync(
        AppDbContext db,
        IRedisPublisher redisPublisher,
        IChannelEmoteSetObservationService emoteSetObservationService,
        Channel channel,
        AuditActor actor,
        ChannelDeactivationReason reason,
        CancellationToken cancellationToken)
    {
        // The backfill cancellation inside StageAsync is a conditional UPDATE that runs at once, while
        // the rest is only staged for the save below, so both happen in one transaction opened here:
        // the run is never cancelled for a deactivation that then fails to save. A caller that already
        // holds a transaction must not come through here - it would publish LEAVE before its own
        // commit - and uses StageAsync + PublishLeaveAsync instead.
        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(DeactivateAsync)} opens its own transaction; a caller with one must use {nameof(StageAsync)} and publish after its commit.");
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Lock order: the channel row before any backfill run row. This is the order every other
            // deactivation path (the identity reconcile's locked/unresolvable passes lock the channel
            // FOR UPDATE and then cancel the run) and the backfill's block commit (FK inserts lock the
            // channel before the run update) already follow; cancelling the run first here would invert
            // it and could deadlock (40P01). A raw statement rather than ChannelQueries.LockAsync,
            // because the entity may already be changed by the caller, which LockAsync refuses.
            await db.Database.ExecuteSqlAsync(
                $"""SELECT 1 FROM "Channels" WHERE "Id" = {channel.Id} FOR UPDATE""", cancellationToken);
            await StageAsync(db, emoteSetObservationService, channel, actor, reason, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await PublishLeaveAsync(redisPublisher, channel.ChannelName, cancellationToken);
    }

    /// <summary>
    /// Stages the deactivation in the change tracker, without saving: the caller saves (and commits,
    /// if it opened a transaction) and only then publishes via <see cref="PublishLeaveAsync"/>.
    /// </summary>
    public static async Task StageAsync(
        AppDbContext db,
        IChannelEmoteSetObservationService emoteSetObservationService,
        Channel channel,
        AuditActor actor,
        ChannelDeactivationReason reason,
        CancellationToken cancellationToken)
    {
        // Closes the open observation interval, if any (spec 4.3) — tracked only, riding the caller's
        // SaveChangesAsync together with the deactivation and the audit entry, so "left" and
        // "stopped observing this set" land in the same commit. Here rather than at each caller:
        // every deactivation is a leave (it writes channel.leave), and an interval left open on an
        // inactive row would break the invariant ChannelEmoteSetObservationService.RecordObservedSetAsync
        // relies on — IsBotActive = false implies no open row, because both are written in one save.
        // (The converse does not hold: a freshly active channel that has not synced yet has no open
        // interval either.)
        await emoteSetObservationService.CloseOpenIntervalAsync(
            channel.Id, ChannelEmoteSetObservationClosedBy.Leave, cancellationToken);

        channel.IsBotActive = false;
        channel.DeactivatedAtUtc = DateTime.UtcNow;

        // A chat-log backfill run of a channel that is no longer observed cannot continue (the worker
        // would fail it as channel_not_active at the next claim), so it is cancelled here, with the
        // same commit as the deactivation (D16). Conditional on the run still being active: a run the
        // worker finished a moment ago stays what it is. A rejoin does not resume anything.
        await CancelActiveBackfillRunAsync(db, channel.Id, cancellationToken);

        switch (reason)
        {
            case ChannelDeactivationReason.Excluded:
                db.AddAuditEntry(actor, AuditActions.ChannelLeave, details: new { reason = ExcludedReason });
                break;
            case ChannelDeactivationReason.Locked:
                db.AddAuditEntry(
                    actor, AuditActions.ChannelLeave, channelName: channel.ChannelName, details: new { reason = LockedReason });
                break;
            case ChannelDeactivationReason.LoginUnresolvable:
                db.AddAuditEntry(
                    actor, AuditActions.ChannelLeave, channelName: channel.ChannelName, details: new { reason = LoginUnresolvableReason });
                break;
            case ChannelDeactivationReason.Leave:
                db.AddAuditEntry(actor, AuditActions.ChannelLeave, channelName: channel.ChannelName);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown deactivation reason.");
        }
    }

    private static async Task CancelActiveBackfillRunAsync(AppDbContext db, string channelId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        ChatLogBackfillRunStatus[] active = [ChatLogBackfillRunStatus.Queued, ChatLogBackfillRunStatus.Running, ChatLogBackfillRunStatus.Paused];
        await db.ChatLogBackfillRuns
            .Where(r => r.ChannelId == channelId && active.Contains(r.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.Status, ChatLogBackfillRunStatus.Cancelled)
                    .SetProperty(r => r.FinishedAtUtc, (DateTime?)now)
                    .SetProperty(r => r.PausedUntilUtc, (DateTime?)null)
                    .SetProperty(r => r.ErrorCode, BackfillChannelLeftErrorCode),
                cancellationToken);
    }

    /// <summary>The LEAVE command for a deactivation that is already committed.</summary>
    public static Task PublishLeaveAsync(IRedisPublisher redisPublisher, string channelName, CancellationToken cancellationToken) =>
        redisPublisher.PublishAsync(BotCommands.Channel, $"{BotCommands.LeavePrefix}{channelName}", cancellationToken);
}
