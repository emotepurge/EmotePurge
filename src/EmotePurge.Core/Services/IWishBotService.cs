namespace EmotePurge.Core.Services;

/// <summary>
/// The wish side of the bot (plan #487 2.9, 2.10): the numbers of the admin read, and the channel part behind the routes
/// under <c>…/wishes/bot</c> and the <c>wishBotModeAvailable</c> flag of <c>/permissions</c>. Nothing here calls 7TV
/// with the bot's token: the Api cannot open it. <see cref="ActivateAsync"/> only records the request the worker
/// confirms; <see cref="CheckActivationAccessAsync"/> asks 7TV about the <b>caller's</b> editor grants.
/// </summary>
public interface IWishBotService
{
    /// <summary>The <c>wishes</c> sub-object of <c>GET /api/admin/bot-account</c>, counted over every channel.</summary>
    Task<WishBotAdminSummary> GetAdminSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>GET …/wishes/bot</c> (spec K4): the channel's bot mode as stored, the account's availability and the caller's
    /// rights, without any 7TV call. Null when no channel has that name.
    /// </summary>
    Task<WishBotChannelDto?> GetChannelAsync(string channelName, WishBotCaller caller, CancellationToken cancellationToken = default);

    /// <summary>
    /// Steps (3) to (5) of the activation filter (plan 2.9, E8, E24), for a manager who is not a global admin by id (the
    /// filter decides steps (1) and (2) itself): the pilot list before any 7TV call, then the broadcaster by Twitch id,
    /// then the caller's 7TV editor grants for the channel's Twitch id. A channel that does not exist is
    /// <see cref="WishBotActivationAccess.Allowed"/>, so the handler answers its 404. The answer names the Twitch id it was
    /// given for (none for a channel that did not exist), which the handler passes on to <see cref="ActivateAsync"/>.
    /// </summary>
    Task<WishBotActivationCheck> CheckActivationAccessAsync(
        string channelName, string callerTwitchUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>POST …/wishes/bot/activate</c> (plan 2.9): behind the channel lock, the bot account unavailable →
    /// <see cref="WishOutcome.BotUnavailable"/>, the bot mode active → <see cref="WishOutcome.BotAlreadyActive"/> (the
    /// activator unchanged); otherwise the request is recorded with the consent bound to the bot account's current 7TV id,
    /// the activation error cleared. A request younger than ten minutes for the same id keeps its requester and is only
    /// nudged again. An open acceptance attempt bound to another id is given up in the same update (audit
    /// <c>wish.botInvitationAbandoned</c>); one bound to the same id keeps its binding. Success publishes
    /// <c>wish.changed</c> and nudges the worker; the bot mode becomes active only when the worker confirms.
    /// <paramref name="checkedTwitchChannelId"/> binds the write to the channel the activation filter checked (E24): the
    /// Twitch id of <see cref="CheckActivationAccessAsync"/>'s answer, an empty string when it found no channel. Behind the
    /// channel lock a channel with another Twitch id (a rename and a rejoin moved the name meanwhile) is
    /// <see cref="WishOutcome.ChannelNotFound"/>, nothing written. Null binds nothing: the filter checked no channel (a
    /// global admin by id, or a switch off).
    /// </summary>
    Task<WishCommandResult> ActivateAsync(
        string channelName, AuditActor actor, string? checkedTwitchChannelId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>DELETE …/wishes/bot</c> (plan 2.9): behind the channel lock D7 is asked again on the stored row
    /// (<see cref="WishOutcome.SettingsRestricted"/>). Neither activated nor requested → success without a write. A
    /// pending request only → the request is cancelled. An activation → switched off, every bot ownership in the channel
    /// ends (decision counted, queue and attempt set cleared) and open attempts are resolved without 7TV
    /// (<see cref="WishBotAttemptResolution"/>). While an acceptance attempt is open, attempt, consent and retry stamp
    /// stay and the activation error becomes <c>invitation_accept_unresolved</c>. Audited as <c>wish.botDeactivate</c>,
    /// one <c>wish.changed</c>. Reachable with <c>BotAccount:Enabled</c> off.
    /// </summary>
    Task<WishCommandResult> DeactivateAsync(
        string channelName, AuditActor actor, bool isGlobalAdmin, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>wishBotModeAvailable</c> of <c>/permissions</c> (plan 2.3): the bot account available and the channel covered
    /// by the pilot list, or the caller a global admin by id. The caller checks both switches first, so a switched-off
    /// server costs no database read.
    /// </summary>
    Task<bool> IsModeAvailableAsync(string channelName, bool callerIsAdminById, CancellationToken cancellationToken = default);
}

/// <summary>Who asks, as far as the bot routes need it.</summary>
/// <param name="TwitchUserId">The caller's Twitch id.</param>
/// <param name="IsGlobalAdmin">A global admin by id or (transitional) by login: the D7 question, as the settings filter asks it.</param>
/// <param name="IsGlobalAdminById">A global admin by id only: the activation question (plan 2.9 step (2)).</param>
public sealed record WishBotCaller(string TwitchUserId, bool IsGlobalAdmin, bool IsGlobalAdminById);

/// <summary>
/// The decision of steps (3) to (5) of the activation filter and the channel it was made for: <paramref name="TwitchChannelId"/>
/// is the checked channel's Twitch id, null when no channel had that name.
/// </summary>
public sealed record WishBotActivationCheck(WishBotActivationAccess Access, string? TwitchChannelId);

/// <summary>The decision of steps (3) to (5) of the activation filter.</summary>
public enum WishBotActivationAccess
{
    /// <summary>The caller may activate (broadcaster, or a moderator with a confirmed editor grant), or the channel does not exist.</summary>
    Allowed,

    /// <summary>The channel is not on the pilot list: 403 <c>wish_bot_channel_not_listed</c>, before any 7TV call.</summary>
    NotListed,

    /// <summary>No editor grant for the channel, no 7TV account, or a channel without Twitch id: 403 <c>wish_bot_activation_forbidden</c>.</summary>
    Forbidden,

    /// <summary>7TV could not say: 503 <c>foreign_channel_seventv_unavailable</c>, never a pass.</summary>
    EditorUnavailable,
}

/// <summary>
/// <c>GET …/wishes/bot</c> (spec K4). <see cref="Available"/> is the account-wide answer, <see cref="Listed"/> the pilot
/// list's, <see cref="Enabled"/> the stored activation. <see cref="ActivationError"/> is one of
/// <c>WishBotChannelReasons.ActivationErrors</c> and <see cref="SuspendedReason"/> one of
/// <c>WishBotChannelReasons.Suspended</c>, both passed on unchanged. <see cref="CanActivate"/> mirrors the activation
/// filter without a 7TV call: for a moderator it is known only while the caller's editor grants are cached, else null.
/// <see cref="CanDeactivate"/> is D7. <see cref="BotSevenTvUserId"/> is the bot account's current public 7TV id (the
/// account the owner invites as an editor), null without a row or with a switch off.
/// </summary>
public sealed record WishBotChannelDto(
    bool Available,
    bool Listed,
    bool Enabled,
    bool ActivationPending,
    DateTime? ActivationRequestedAtUtc,
    string? ActivationError,
    bool? CanActivate,
    bool CanDeactivate,
    string? SuspendedReason,
    DateTime? EditorCheckedAtUtc,
    string? ActivatedByLogin,
    DateTime? ActivatedAtUtc,
    string? BotSevenTvUserId);

/// <summary>The wish numbers on the admin page (spec K4, plan 2.10).</summary>
/// <param name="Queue">The bot's pending work.</param>
/// <param name="BotChannels">Channels with the bot mode activated (<c>BotModeEnabled</c>), suspended or not.</param>
/// <param name="SuspendedChannels">Of those, the ones with a <c>BotSuspendedReason</c>.</param>
/// <param name="OpenRepairChecks">Wishes in a repair check phase (<c>BotRepairAtUtc</c> set, plan 2.5a (h)).</param>
public sealed record WishBotAdminSummary(WishBotQueueCounts Queue, int BotChannels, int SuspendedChannels, int OpenRepairChecks);

/// <summary>The bot's queue.</summary>
/// <param name="Adds"><c>approved</c> wishes in channels with the bot mode activated.</param>
/// <param name="Confirms"><c>unconfirmed</c> wishes (a write whose result is still being confirmed).</param>
/// <param name="Removals">Queued removals (<c>RemovalQueuedAtUtc</c> set).</param>
public sealed record WishBotQueueCounts(int Adds, int Confirms, int Removals);
