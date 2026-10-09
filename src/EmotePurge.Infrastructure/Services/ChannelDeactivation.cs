using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;

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
        await StageAsync(db, emoteSetObservationService, channel, actor, reason, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
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

    /// <summary>The LEAVE command for a deactivation that is already committed.</summary>
    public static Task PublishLeaveAsync(IRedisPublisher redisPublisher, string channelName, CancellationToken cancellationToken) =>
        redisPublisher.PublishAsync(BotCommands.Channel, $"{BotCommands.LeavePrefix}{channelName}", cancellationToken);
}
