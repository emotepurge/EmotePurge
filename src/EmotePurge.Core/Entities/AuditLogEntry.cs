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

    // Emote tags (#201). Details carry only the tag id (plus the entry count on delete) — never the
    // name, so the audit log does not hold free text a moderator typed.
    public const string TagCreate = "tag.create";
    public const string TagRename = "tag.rename";
    public const string TagDelete = "tag.delete";
    public const string TagPlayedIn = "tag.playedIn";
    public const string TagRemoved = "tag.removed";

    // Chat-log backfill (#346). The request carries the chosen set as the target and the month count
    // as its only detail; the cancel carries nothing. Completion is deliberately not audited. The
    // removal of a channel's imported data (#384) carries the removed coverage days as its only
    // detail, no target: it covers every set and every run of the channel.
    public const string BackfillRequest = "backfill.request";
    public const string BackfillCancel = "backfill.cancel";
    public const string BackfillRemoveImported = "backfill.removeImported";

    // Wish emotes (#481). Written by the wish service under the acting manager. The target is
    // ("wish", id); details carry ids, states and numbers, never the requester's name or text.
    public const string WishCreate = "wish.create";
    public const string WishAdopt = "wish.adopt";
    public const string WishExtend = "wish.extend";
    public const string WishKeep = "wish.keep";
    public const string WishRemove = "wish.remove";
    public const string WishClose = "wish.close";
    public const string WishRetry = "wish.retry";
    public const string WishReAdd = "wish.readd";
    public const string WishSettings = "wish.settings";

    /// <summary>An active wish fell due. Written by the expiry worker under <see cref="AuditActor.System"/>, not by a request.</summary>
    public const string WishDue = "wish.due";

    /// <summary>PATCH of alias or duration; not in the spec's list, added so no manager action goes unrecorded.</summary>
    public const string WishUpdate = "wish.update";

    /// <summary>
    /// A manager decided an open wish (K6): details <c>wishId</c>, <c>outcome</c> (<c>approved</c> or <c>merged</c>) and
    /// <c>mergedIntoWishId</c> (null for <c>approved</c>). A merge writes <see cref="WishExtend"/> on the running wish in
    /// the same transaction.
    /// </summary>
    public const string WishApprove = "wish.approve";

    /// <summary>A manager rejected an open wish (K6); the standard wish details, never the redemption text.</summary>
    public const string WishReject = "wish.reject";

    /// <summary>
    /// The reconcile linked a wish without a click (<c>pending</c> or <c>approved</c>, K6). Written under
    /// <see cref="AuditActor.System"/>; details <c>wishId</c>, <c>source</c>, <c>fromStatus</c>, <c>matchKind</c>.
    /// </summary>
    public const string WishAutoLink = "wish.autoLink";

    /// <summary>
    /// An automatic capture link was released back to <c>pending</c> (K6): by a manager, or by the reconcile's
    /// date correction under <see cref="AuditActor.System"/> with <c>cause: date_correction</c>.
    /// </summary>
    public const string WishUnlink = "wish.unlink";

    /// <summary>
    /// A manager deleted an ended wish for good (K7a, spec E36). Details exactly <c>wishId</c>, <c>sevenTvEmoteId</c>,
    /// <c>durationDays</c> and <c>fromStatus</c> (the end state it was deleted from): never the alias, the emote name, a
    /// login or the redemption text, and no <c>previousExpiresAtUtc</c>. The entry outlives the row it names.
    /// </summary>
    public const string WishDelete = "wish.delete";

    // Learned channel-point rewards (K6), written by the reward service under the acting manager. Target
    // ("wishReward", row id); details carry the row id and, for a change, the names of the changed fields only:
    // never the label, the reward's Twitch tag, a redemption text or a login. Starting and cancelling a learning
    // session are not audited.
    public const string WishRewardAdd = "wish.rewardAdd";
    public const string WishRewardChange = "wish.rewardChange";
    public const string WishRewardRemove = "wish.rewardRemove";

    // The bot account (#487, plan 2.11). Account-wide, so no channel. Details never carry the token, the seal, a
    // name or a login: tokenSet only expiresAtUtc, killSwitch only enabled, tokenDelete nothing.
    public const string BotAccountTokenSet = "botAccount.tokenSet";
    public const string BotAccountTokenDelete = "botAccount.tokenDelete";

    /// <summary>The kill switch changed (an unchanged value writes nothing); details <c>enabled</c>.</summary>
    public const string BotAccountKillSwitch = "botAccount.killSwitch";

    /// <summary>The worker moved the token to <c>invalid</c> or <c>expired</c>; written under <see cref="AuditActor.System"/>.</summary>
    public const string BotAccountTokenState = "botAccount.tokenState";

    // The wish bot (#487, plan 2.11). Channel-scoped like every wish action, target ("wish", id) where a wish is meant.
    // Bot transitions without an entry of their own (bot_unconfirmed, bot_already_in_set, bot_reject, ownership_lost,
    // the fallback) are visible on the wish and write nothing. The bot's own writes go under AuditActor.System; the
    // activation is recorded under the manager who asked for it.
    public const string WishBotActivate = "wish.botActivate";
    public const string WishBotDeactivate = "wish.botDeactivate";
    public const string WishBotAdd = "wish.botAdd";
    public const string WishBotRemove = "wish.botRemove";
    public const string WishBotRepair = "wish.botRepair";
    public const string WishBotSuspectRemoval = "wish.botSuspectRemoval";
    public const string WishBotSuspend = "wish.botSuspend";
    public const string WishBotRemoveRequest = "wish.botRemoveRequest";
    public const string WishRecheck = "wish.recheck";

    /// <summary>
    /// <c>activate</c> gave up an open invitation acceptance bound to an earlier bot account (plan 2.9, Revision 6d K6d-4,
    /// 6e K6e-2), under the manager who activated. Details exactly <c>attemptedAtUtc</c> and
    /// <c>abandonedBotSevenTvUserId</c> (the public 7TV id of the earlier account, so the owner knows whom to remove as an
    /// editor should the acceptance have taken effect after all).
    /// </summary>
    public const string WishBotInvitationAbandoned = "wish.botInvitationAbandoned";

    /// <summary>
    /// An acceptance attempt whose consent no longer applied resolved negatively, its effect at 7TV still not certain
    /// (plan 2.5b, Revision 6e K6e-1), written by the worker under the system actor. Details exactly <c>attemptedAtUtc</c>.
    /// </summary>
    public const string WishBotInvitationUnresolved = "wish.botInvitationUnresolved";
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
