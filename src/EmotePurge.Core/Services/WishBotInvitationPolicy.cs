using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>What step 2 of the bot pass does next for one channel (<see cref="WishBotInvitationPolicy.Decide"/>).</summary>
public enum WishBotInvitationAction
{
    /// <summary>Nothing now: no request, no open attempt to resolve, or the facts are not there yet.</summary>
    None,

    /// <summary>
    /// The read in hand began too early to decide a negative answer; read again, one that begins after
    /// <see cref="WishBotInvitationDecision.ReadStartedAfterUtc"/> (plan 2.5b S6-3, K6c-4).
    /// </summary>
    ReadAgain,

    /// <summary>The open acceptance attempt took effect (<c>ACCEPTED</c>, with or without "manage emote sets"; K6d-2).</summary>
    ResolveAccepted,

    /// <summary>The open acceptance attempt did not take effect: the first read that began after its window shows no <c>ACCEPTED</c>.</summary>
    ResolveNotAccepted,

    /// <summary>Accept the open invitation ([B] F7): case (a) a request, or case (b) an activated channel.</summary>
    Accept,

    /// <summary>Confirm the activation request: the read proves the bot an editor with "manage emote sets" (plan 2.9).</summary>
    Confirm,

    /// <summary>End the activation request without the bot mode, with <see cref="WishBotInvitationDecision.Error"/> (plan 2.9).</summary>
    EndRequest,
}

/// <summary>
/// One decision of <see cref="WishBotInvitationPolicy.Decide"/>. Only the fields of its <see cref="Action"/> are set.
/// </summary>
public readonly record struct WishBotInvitationDecision(
    WishBotInvitationAction Action,
    DateTime? ReadStartedAfterUtc = null,
    bool ConsentGone = false,
    bool EndedInRejected = false,
    string? Error = null,
    string? OwnerSevenTvUserId = null)
{
    public static WishBotInvitationDecision Nothing { get; } = new(WishBotInvitationAction.None);
}

/// <summary>
/// The pure rule of step 2 of the bot pass (#487 plan 2.5b, 2.9; Revisions 6a to 6e), one channel at a time: first the
/// resolution of an open acceptance attempt, then an activation request (case (a)), then an invitation of an activated
/// channel (case (b)). Sending, locking and booking stay with the caller, which runs this again behind its locks on the
/// row it re-read.
/// <list type="bullet">
/// <item><b>An open attempt</b> blocks every other decision of the channel. It is resolved only by a read for the bound
/// bot id (<c>BotConsentSevenTvUserId</c>, which stays while the attempt is open): <c>ACCEPTED</c> (also without
/// "manage", K6d-2) took effect at any time; anything else only from a read that began at or after
/// <see cref="NegativeResolutionFrom"/> (attempt + 30 s + 10 s + <c>EditorVisibilitySeconds</c>, K6c-4); an unavailable
/// read leaves it open. The consent still applies while a request or an activation stands (bound to that id).</item>
/// <item><b>A request</b> (not activated, request stamp set) waits while the account is unavailable, ends with
/// <c>bot_account_changed</c> when bound to another account (no read needed), waits while the channel is not covered,
/// is confirmed by <c>editor</c>, accepts on <c>invited</c> once the retry stamp is reached, and ends with
/// <c>not_editor</c> only from a read that began at or after request + <c>EditorVisibilitySeconds</c> (S6-3).</item>
/// <item><b>An activated channel</b> accepts on <c>invited</c> when covered, available, bound to the current account and
/// the retry stamp is reached; its suspension and resume stay with step 3.</item>
/// </list>
/// </summary>
public static class WishBotInvitationPolicy
{
    /// <summary>The oldest an attempt stamp may be when its acceptance is sent (plan 2.5b K6c-4: 30 s, a fixed constant).</summary>
    public static readonly TimeSpan SendWindow = TimeSpan.FromSeconds(30);

    /// <summary>The write client's time limit (plan 2.4: 10 s); the latest end of the wait is the stamp + 30 s + this.</summary>
    public static readonly TimeSpan ClientTimeLimit = TimeSpan.FromSeconds(10);

    /// <summary>How long after a "not accepted" (<c>LackingPrivileges</c>, <c>Rejected</c>) the next attempt waits (S6-6).</summary>
    public static readonly TimeSpan RetryWait = TimeSpan.FromMinutes(15);

    /// <summary>The trigger of an acceptance for a request (case (a)), in the audit details.</summary>
    public const string TriggerActivation = "activation";

    /// <summary>The trigger of an acceptance for an activated channel (case (b)), in the audit details.</summary>
    public const string TriggerActivated = "activated";

    /// <summary>The earliest start of a read whose negative answer resolves the attempt of <paramref name="attemptAtUtc"/>.</summary>
    public static DateTime NegativeResolutionFrom(DateTime attemptAtUtc, TimeSpan editorVisibility) =>
        attemptAtUtc + SendWindow + ClientTimeLimit + editorVisibility;

    /// <summary>Whether a request or an activation still stands for the bound bot id (plan 2.5b "Zustimmung gilt").</summary>
    public static bool ConsentStands(ChannelWishSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.BotConsentSevenTvUserId is not null
            && (settings.BotModeEnabled || settings.BotActivationRequestedAtUtc is not null);
    }

    /// <summary>The trigger of an acceptance for the channel as it stands (case (a) or (b)); null when neither stands.</summary>
    public static string? TriggerOf(ChannelWishSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.BotModeEnabled
            ? TriggerActivated
            : settings.BotActivationRequestedAtUtc is not null ? TriggerActivation : null;
    }

    /// <param name="settings">The channel's settings row as read.</param>
    /// <param name="twitchChannelId">The channel's Twitch id, the key of the editor read.</param>
    /// <param name="account">The bot account's current state (availability, bot id, identity).</param>
    /// <param name="covered">
    /// Case (a): on the pilot list or the requester is a global admin by id; case (b): the pilot list or the activator.
    /// </param>
    /// <param name="read">
    /// The editor read for the bound bot id while an attempt is open, else for the current one; null when there is none.
    /// </param>
    /// <param name="editorVisibility"><c>BotAccount:EditorVisibilitySeconds</c>.</param>
    /// <param name="nowUtc">The caller's clock.</param>
    public static WishBotInvitationDecision Decide(
        ChannelWishSettings settings,
        string? twitchChannelId,
        BotAccountAvailabilityState account,
        bool covered,
        BotAccountEditorRead? read,
        TimeSpan editorVisibility,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.BotInvitationAttemptAtUtc is { } attemptAt)
        {
            return DecideAttempt(settings, twitchChannelId, attemptAt, read, editorVisibility, nowUtc);
        }

        var requestPending = !settings.BotModeEnabled && settings.BotActivationRequestedAtUtc is not null;
        if (requestPending)
        {
            return DecideRequest(settings, twitchChannelId, account, covered, read, editorVisibility, nowUtc);
        }

        if (!settings.BotModeEnabled
            || !covered
            || !account.IsAvailable
            || !WishBotPreflight.ConsentApplies(settings, account.BotSevenTvUserId)
            || !RetryReached(settings, nowUtc)
            || EntryOf(read, account, twitchChannelId) is not { Status: BotAccountEditorStatus.Invited } invited)
        {
            return WishBotInvitationDecision.Nothing;
        }

        return new WishBotInvitationDecision(WishBotInvitationAction.Accept, OwnerSevenTvUserId: invited.OwnerSevenTvUserId);
    }

    private static WishBotInvitationDecision DecideAttempt(
        ChannelWishSettings settings,
        string? twitchChannelId,
        DateTime attemptAt,
        BotAccountEditorRead? read,
        TimeSpan editorVisibility,
        DateTime nowUtc)
    {
        // Only a read for the bound id resolves; an unavailable read (or none) leaves the attempt open, no deadline clears it.
        if (settings.BotConsentSevenTvUserId is not { } bound
            || read is null
            || !string.Equals(read.BotSevenTvUserId, bound, StringComparison.Ordinal)
            || read.For(twitchChannelId) is not { } entry)
        {
            return WishBotInvitationDecision.Nothing;
        }

        var consentGone = !ConsentStands(settings);
        if (entry.Accepted)
        {
            return new WishBotInvitationDecision(WishBotInvitationAction.ResolveAccepted, ConsentGone: consentGone);
        }

        var from = NegativeResolutionFrom(attemptAt, editorVisibility);
        if (read.StartedAtUtc >= from)
        {
            // A retry stamp later than the attempt can only come from this attempt: it ended in Rejected (K6b-1).
            var endedInRejected = settings.BotInvitationRetryAtUtc is { } retry && retry > attemptAt;
            return new WishBotInvitationDecision(
                WishBotInvitationAction.ResolveNotAccepted, ConsentGone: consentGone, EndedInRejected: endedInRejected);
        }

        return nowUtc >= from
            ? new WishBotInvitationDecision(WishBotInvitationAction.ReadAgain, ReadStartedAfterUtc: from)
            : WishBotInvitationDecision.Nothing;
    }

    private static WishBotInvitationDecision DecideRequest(
        ChannelWishSettings settings,
        string? twitchChannelId,
        BotAccountAvailabilityState account,
        bool covered,
        BotAccountEditorRead? read,
        TimeSpan editorVisibility,
        DateTime nowUtc)
    {
        // Globally unavailable: the request waits (plan 2.9).
        if (!account.IsAvailable || account.BotSevenTvUserId is not { } botId)
        {
            return WishBotInvitationDecision.Nothing;
        }

        // Consent to another bot account: ends without acceptance and without confirmation, whatever a read says (S6-2).
        if (!WishBotPreflight.ConsentApplies(settings, botId))
        {
            return new WishBotInvitationDecision(WishBotInvitationAction.EndRequest, Error: WishBotChannelReasons.BotAccountChanged);
        }

        if (!covered || EntryOf(read, account, twitchChannelId) is not { } entry)
        {
            return WishBotInvitationDecision.Nothing;
        }

        switch (entry.Status)
        {
            case BotAccountEditorStatus.Editor:
                return new WishBotInvitationDecision(WishBotInvitationAction.Confirm);
            case BotAccountEditorStatus.Invited:
                return RetryReached(settings, nowUtc)
                    ? new WishBotInvitationDecision(WishBotInvitationAction.Accept, OwnerSevenTvUserId: entry.OwnerSevenTvUserId)
                    : WishBotInvitationDecision.Nothing;
        }

        // not_editor (also ACCEPTED without "manage"): decides only a read that began after the visibility window (S6-3).
        var visibleFrom = settings.BotActivationRequestedAtUtc!.Value + editorVisibility;
        if (read!.StartedAtUtc >= visibleFrom)
        {
            return new WishBotInvitationDecision(WishBotInvitationAction.EndRequest, Error: WishBotChannelReasons.NotEditor);
        }

        return nowUtc >= visibleFrom
            ? new WishBotInvitationDecision(WishBotInvitationAction.ReadAgain, ReadStartedAfterUtc: visibleFrom)
            : WishBotInvitationDecision.Nothing;
    }

    // The read's verdict for the channel, only when the read belongs to the current account and identity (plan 2.3).
    private static BotAccountEditorEntry? EntryOf(BotAccountEditorRead? read, BotAccountAvailabilityState account, string? twitchChannelId) =>
        read is not null
        && read.IdentityVersion == account.IdentityVersion
        && string.Equals(read.BotSevenTvUserId, account.BotSevenTvUserId, StringComparison.Ordinal)
            ? read.For(twitchChannelId)
            : null;

    private static bool RetryReached(ChannelWishSettings settings, DateTime nowUtc) =>
        settings.BotInvitationRetryAtUtc is not { } retry || retry <= nowUtc;
}
