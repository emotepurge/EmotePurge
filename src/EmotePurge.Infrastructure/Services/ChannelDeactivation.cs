using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The write every "stop observing this channel, keep its history" path shares: flip
/// <c>IsBotActive</c> off, stamp the retention clock, record why, and publish the LEAVE command the
/// worker needs to actually part the chat. Factored out of <see cref="ChannelService.LeaveAsync"/>
/// so <see cref="ChannelIdentityService"/>'s own objection-gate deactivation (issue #260, the second
/// Codex review of #252) can reuse exactly the same write rather than a hand-copied one.
/// <para>
/// A plain static helper, not a shared service, and deliberately not <c>IChannelService</c> injected
/// into <see cref="ChannelIdentityService"/>: <see cref="ChannelService"/> already depends on
/// <see cref="IChannelIdentityService"/> for <c>LookupByLoginAsync</c>, so the reverse dependency
/// would be circular.
/// </para>
/// </summary>
internal static class ChannelDeactivation
{
    // The reason detail of a channel.leave entry written by the objection gate — the only thing that
    // entry says, since it deliberately names no channel (see DeactivateAsync).
    private const string ExcludedReason = "excluded";

    /// <param name="forExclusion">
    /// True for the identity reconcile's objection-gate deactivation: the audit entry then carries
    /// no channel name, only <c>reason = "excluded"</c>. A <c>channel.leave</c> by the system actor
    /// is written for nothing else, so an entry naming the channel would tell every admin reading the
    /// audit log which channel the objection concerns — the very thing its log lines avoid.
    /// </param>
    public static async Task DeactivateAsync(
        AppDbContext db,
        IRedisPublisher redisPublisher,
        Channel channel,
        AuditActor actor,
        bool forExclusion,
        CancellationToken cancellationToken)
    {
        channel.IsBotActive = false;
        channel.DeactivatedAtUtc = DateTime.UtcNow;
        if (forExclusion)
        {
            db.AddAuditEntry(actor, AuditActions.ChannelLeave, details: new { reason = ExcludedReason });
        }
        else
        {
            db.AddAuditEntry(actor, AuditActions.ChannelLeave, channelName: channel.ChannelName);
        }

        await db.SaveChangesAsync(cancellationToken);
        // Committed before published, same reasoning as every other publish in this codebase after a
        // channel write: the row is already the source of truth, and a Redis outage here would only
        // cost this acceleration — SevenTvPeriodicResyncWorker's prune step converges on its own.
        await redisPublisher.PublishAsync(
            BotCommands.Channel, $"{BotCommands.LeavePrefix}{channel.ChannelName}", cancellationToken);
    }
}
