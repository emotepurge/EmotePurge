using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

public enum ChannelResyncResult
{
    Triggered,
    NotFound,
    NotActive,
}

/// <summary>What <see cref="IChannelService.PurgeIfInactiveSinceAsync"/> did.</summary>
public enum ChannelRetentionPurgeResult
{
    /// <summary>The row and, by cascade, its whole history are gone; a <c>channel.purge</c> entry was written.</summary>
    Purged,

    /// <summary>No row under this name (any more) — nothing written, not even an audit entry.</summary>
    NotFound,

    /// <summary>
    /// The row exists but is not due: active again, deactivated at or after the cutoff, or never
    /// stamped (<c>DeactivatedAtUtc</c> null). Checked under the row lock; nothing written.
    /// </summary>
    StillActive,
}

public enum ChannelJoinStatus
{
    Joined,

    // Twitch was reachable and answered that no account holds this login. Deliberately distinct from
    // "we could not ask": only a definite answer may reject a join, because the alternative — a
    // typo'd login quietly becoming a permanent, never-syncing row — is what this status exists to
    // prevent, and an outage is not evidence of a typo.
    ChannelNotOnTwitch,

    // The join would activate a channel (a brand-new row, or reactivating one that was left) while
    // the configured cap on simultaneously active channels (Channels:MaxActiveChannels) is already
    // reached, and the caller is not a global admin. Never returned for a channel that is already
    // active — see ChannelService.JoinAsync's idempotency comment.
    CapacityReached,

    // The broadcaster behind this Twitch id objected to processing (GDPR Art. 21, issue #252)
    // and is on the configured block list (Channels:ExcludedChannelIds). Unlike CapacityReached,
    // global admins are NOT exempt — the only way to undo this is for the operator to remove the id
    // from the list. Matched on the immutable Twitch id, resolved before any row is written; never
    // returned for a login Twitch could not resolve at all (that stays ChannelNotOnTwitch).
    ChannelExcluded,
}

/// <summary>
/// <see cref="Channel"/> is non-null if and only if <see cref="Status"/> is
/// <see cref="ChannelJoinStatus.Joined"/>, and the two factories below are the only way to build one
/// at all — so that invariant cannot be broken at a call site, not even by accident.
/// <para>
/// A sealed class with a private constructor rather than a record, because a record cannot keep that
/// promise: its positional constructor is public, and <c>with</c> stays open on top of it, so
/// <c>Failed(ChannelJoinStatus.Joined)</c> or <c>result with { Channel = null }</c> would hand the
/// join endpoint a "joined" result with nothing to dereference. Value equality — the one thing that
/// would argue for a record — is of no use here: the payload is a mutable EF entity that compares by
/// reference anyway, so record equality would only look like a guarantee it never gave.
/// </para>
/// </summary>
public sealed class ChannelJoinResult
{
    private ChannelJoinResult(ChannelJoinStatus status, Channel? channel)
    {
        Status = status;
        Channel = channel;
    }

    public ChannelJoinStatus Status { get; }

    /// <summary>Non-null if and only if <see cref="Status"/> is <see cref="ChannelJoinStatus.Joined"/>.</summary>
    public Channel? Channel { get; }

    public static ChannelJoinResult Joined(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new ChannelJoinResult(ChannelJoinStatus.Joined, channel);
    }

    /// <summary>
    /// Builds a rejected join. Rejects a success status outright: a caller that passes one is asking
    /// for the exact value this type exists to make impossible, and failing loudly at the source
    /// beats a NullReferenceException in the endpoint. A future success status has to be added to the
    /// guard below — and to <see cref="Joined"/>'s side of the type — in the same commit that adds it
    /// to the enum.
    /// </summary>
    public static ChannelJoinResult Failed(ChannelJoinStatus status)
    {
        if (status == ChannelJoinStatus.Joined)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "ChannelJoinResult.Failed() kann keinen Erfolgsstatus tragen — für Joined ist ChannelJoinResult.Joined(channel) zuständig.");
        }

        // Keeps an undefined cast like (ChannelJoinStatus)7 out of the type, which is what the join
        // endpoint's switch would otherwise fall through unmatched.
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), status, "Unbekannter ChannelJoinStatus.");
        }

        return new ChannelJoinResult(status, null);
    }
}

public interface IChannelService
{
    // All three write methods take the acting user: each writes its own AuditLogEntry into the same
    // transaction as the change itself (see the implementations). The actor is a required parameter
    // rather than an optional one so a new call site cannot silently produce unattributed history.
    //
    // Runs in one transaction that locks every existing row it may activate (SELECT ... FOR UPDATE),
    // so it serialises with PurgeIfInactiveSinceAsync; the Twitch lookup happens before it, the Redis
    // publishes after the commit.
    //
    // Resolves the channel's Twitch identity before it writes anything (IChannelIdentityService):
    // the immutable Twitch id is what a channel *is*, and asking for it at the one moment a human is
    // waiting for an answer is what lets a join reject a login Twitch does not know, follow a rename
    // onto the existing row, and stamp the id onto a row that is being created anyway.
    //
    // isGlobalAdmin exempts the caller from the active-channel cap (Channels:MaxActiveChannels,
    // ChannelJoinStatus.CapacityReached) — passed in rather than re-derived here because the caller
    // already resolved it from the request's claims (IChannelAccessService.IsGlobalAdmin), and this
    // service has no ClaimsPrincipal to work from. Defaults to false so every existing caller keeps
    // being subject to the cap unless it explicitly says otherwise.
    Task<ChannelJoinResult> JoinAsync(string channelName, AuditActor actor, bool isGlobalAdmin = false, CancellationToken cancellationToken = default);

    // Deactivates the bot for this channel and keeps the row and all its history. Reversible via
    // JoinAsync. See PurgeAsync for the irreversible variant.
    Task<bool> LeaveAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default);

    // Irreversibly deletes the channel row and, by cascade, its emotes, usage statistics, vote
    // sessions and votes. Admin-only by design — see the endpoint. The audit entry deliberately
    // outlives the channel (AuditLogEntry.ChannelName is a snapshot, not an FK).
    Task<bool> PurgeAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default);

    // The retention job's purge: deletes the channel like PurgeAsync, but only if it is *still* due —
    // inactive, and deactivated before deactivatedBeforeUtc (a UTC cutoff, RetentionPolicy's 180 days).
    // The condition is checked under a row lock (SELECT ... FOR UPDATE) that JoinAsync and the identity
    // merge take as well, so a join racing the purge either lands first (the purge then sees an active
    // row: StillActive) or waits and finds no row, creating a fresh one — never a 500, never a purged
    // channel that was just reactivated. Audited as channel.purge with { reason: "retention" }. No
    // LEAVE is published: the worker is not in an inactive channel.
    Task<ChannelRetentionPurgeResult> PurgeIfInactiveSinceAsync(
        string channelName, DateTime deactivatedBeforeUtc, AuditActor actor, CancellationToken cancellationToken = default);

    Task<Channel?> GetByNameAsync(string channelName, CancellationToken cancellationToken = default);

    // Spec 2026-09-20, 6.2: the target picker's "is this account one of our tracked channels?"
    // question, asked by Twitch id rather than by name — a 7TV editor grant and the picker's own
    // account both carry an id, never a channel name to look up by. IsBotActive = true only: a
    // channel we once tracked and then left is, for this endpoint's purpose, exactly as untracked as
    // one we never joined — its ActiveEmoteSetId is stale, not a live observed state (E21).
    Task<Channel?> GetActiveByTwitchChannelIdAsync(string twitchChannelId, CancellationToken cancellationToken = default);

    // The normalized names of every channel the bot is currently meant to be in — the worker's
    // boot recovery and its periodic 7TV resync both start from this list. Exists as a service
    // method rather than as the identical inline query both hosted services used to carry, because
    // "which channels are active?" is a domain question and because the direct AppDbContext access
    // it replaced was the one place in the repo that stepped around the layering rule.
    //
    // "Meant to be in" excludes a row whose stored Twitch id is on the excluded-channel list
    // (Channels:ExcludedChannelIds), even while the row itself is still active: this list is the
    // worker's whole roster source (boot recovery, periodic resync and its roster prune, the live
    // poll, the JOIN/RESYNC command guard), so leaving such a row out is what stops the worker from
    // observing it the moment it restarts with the id configured, before the identity reconcile has
    // deactivated the row itself. Admin views that must still show the row read their own query
    // (IAdminChannelQueryService), not this one.
    Task<IReadOnlyList<string>> ListActiveChannelNamesAsync(CancellationToken cancellationToken = default);

    // Publishes a RESYNC command for an active channel, making the worker re-resolve the full 7TV
    // truth immediately instead of waiting for the next periodic tick. Fire-and-forget by design:
    // the command protocol is one-way, so "triggered" means "published", not "completed" — the
    // admin channel list's LastSyncedAtUtc is where completion becomes visible. Restricted to
    // active channels: the worker's sync path would otherwise create an EventAPI subscription for
    // a channel the bot is not even in.
    Task<ChannelResyncResult> TriggerResyncAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default);
}
