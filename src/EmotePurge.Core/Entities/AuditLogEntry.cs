namespace EmotePurge.Core.Entities;

/// <summary>
/// The vocabulary of <see cref="AuditLogEntry.Action"/>. Constants rather than an enum, because the
/// value is persisted and shipped to the frontend as-is: the strings are the contract (the UI maps
/// each one to an <c>audit.actions.*</c> translation key), and an enum would have made the
/// stored representation an implementation detail that renumbering could silently break.
/// </summary>
public static class AuditActions
{
    public const string ChannelJoin = "channel.join";
    public const string ChannelLeave = "channel.leave";
    public const string ChannelPurge = "channel.purge";
    public const string VoteSessionCreate = "voteSession.create";
    public const string VoteSessionEnd = "voteSession.end";
    public const string VoteSessionDelete = "voteSession.delete";
    public const string EmotesSyncDeleted = "emotes.syncDeleted";
    public const string EmotesSyncRestored = "emotes.syncRestored";
    public const string EmotesSyncImported = "emotes.syncImported";
    public const string UserRevokeSessions = "user.revokeSessions";
    public const string ChannelResync = "channel.resync";
    public const string UserInvalidateRoleCache = "user.invalidateRoleCache";

    // Written by IAccountDeletionService, on request or by the retention job. Deliberately carries no
    // identity of the deleted account: TargetId is AuditActor.DeletedUser's id, and the details hold
    // only the reason and counts.
    public const string UserDelete = "user.delete";

    // The two identity-reconciliation actions. Unlike every constant above them these are written
    // by the worker under AuditActor.System rather than by a user's request — the audit log is the
    // only place a rename or a merge becomes visible after the fact, since both rewrite the very
    // name every other view addresses the channel by.
    public const string ChannelRename = "channel.rename";
    public const string ChannelMerge = "channel.merge";
}

/// <summary>
/// One recorded administrative action. Written by the service that performs the action, inside that
/// action's own <c>SaveChangesAsync</c> — so an entry exists if and only if the action committed.
/// <para>
/// Every reference to something else is a <b>snapshot string, never a foreign key</b>, and that is
/// deliberate on two counts. The actor is not an FK to <c>User</c>: <c>Vote.UserId</c> already uses
/// <c>DeleteBehavior.Restrict</c> to keep a future user deletion from wiping vote history, and a
/// second restricting edge from the audit log would make the audit trail itself the thing blocking
/// that deletion — an audit log must not become an obstacle to the operations it records. The
/// channel is not an FK either, because <c>channel.purge</c> deletes the channel row in the very
/// transaction that writes its audit entry; a cascade would delete the record of the deletion, and a
/// restrict would forbid the purge outright.
/// </para>
/// <para>
/// Retention is bounded: entries older than twelve months are deleted by the retention job (data
/// retention plan, #244). Deleting an account does <em>not</em> delete its entries — they are
/// pseudonymised instead (<c>IAccountDeletionService</c>): the user's id and login are replaced by
/// <c>AuditActor.DeletedUser</c> wherever they appear as actor, as a <c>"user"</c> target, or in that
/// target's <c>login</c> detail, so the record of what happened survives without naming whom.
/// </para>
/// </summary>
public class AuditLogEntry
{
    public long Id { get; set; }

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    // Snapshot of who acted, taken at action time — see the type remarks for why this is not an FK.
    public string ActorTwitchUserId { get; set; } = string.Empty;
    public string ActorLogin { get; set; } = string.Empty;

    // One of the AuditActions constants.
    public string Action { get; set; } = string.Empty;

    // Normalized channel name (ChannelName.Normalize), null for actions with no channel scope.
    public string? ChannelName { get; set; }

    // Optional pointer to the affected object, e.g. ("voteSession", "42").
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }

    // Action-specific payload as JSON (jsonb column), e.g. {"emoteCount":12} or {"title":"..."}.
    // Free-form on purpose: every action may carry what it needs without a schema change, and the
    // UI reads it defensively.
    public string? DetailsJson { get; set; }
}
