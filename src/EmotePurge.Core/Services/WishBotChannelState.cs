using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>The effective bot state of a channel (plan #487 2.3).</summary>
public enum WishBotChannelMode
{
    /// <summary>The bot mode is not activated: the manual flow.</summary>
    Manual,

    /// <summary>Activated, not suspended, the account available, covered, consented to this account and freshly proven editor.</summary>
    Bot,

    /// <summary>Activated, but the bot cannot act for the channel right now: the manual flow until it can again.</summary>
    Fallback,
}

/// <summary>
/// The one rule for the effective bot state of a channel (plan #487 2.3, [W1]), pure: Api and Worker compute it the same
/// way from the same columns, so a channel the runner has not booked yet already counts as <c>fallback</c> everywhere.
/// What hangs on it: <c>mode</c> in the DTO, which states need a human, the header counter and the expiry run's row 4. The
/// right to change the channel's settings (D7) does <b>not</b>; it hangs on the stored activation (<c>BotModeEnabled</c>).
/// </summary>
public static class WishBotChannelState
{
    /// <summary>How old the last successful editor check may get before the editor state counts as unknown ([F4], Sol-P2-7).</summary>
    public static readonly TimeSpan EditorCheckFreshness = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The channel's state. <paramref name="settings"/> null is a channel without a settings row (manual).
    /// </summary>
    /// <param name="settings">The channel's settings row as stored.</param>
    /// <param name="account">The bot account's state from one read of its row (or, in the worker, the session's).</param>
    /// <param name="listed">The channel is on <c>Wishes:PilotChannelIds</c> (by Twitch id, <c>*</c> = every channel).</param>
    /// <param name="activatorIsAdminById">The activating manager is a global admin by Twitch id, never through the login list (E8, [B] F2).</param>
    /// <param name="nowUtc">The caller's clock.</param>
    public static WishBotChannelMode Of(
        ChannelWishSettings? settings,
        BotAccountAvailabilityState account,
        bool listed,
        bool activatorIsAdminById,
        DateTime nowUtc)
    {
        if (settings is not { BotModeEnabled: true })
        {
            return WishBotChannelMode.Manual;
        }

        return IsActing(settings, account, listed, activatorIsAdminById, nowUtc)
            ? WishBotChannelMode.Bot
            : WishBotChannelMode.Fallback;
    }

    // Every condition of "bot" besides the activation itself. A channel without a bound bot id counts as deviating
    // (2.5b); an editor proof counts only for the identity it was booked against and only while it is fresh.
    private static bool IsActing(
        ChannelWishSettings settings, BotAccountAvailabilityState account, bool listed, bool activatorIsAdminById, DateTime nowUtc) =>
        account.IsAvailable
        && settings.BotSuspendedReason is null
        && (listed || activatorIsAdminById)
        && settings.BotConsentSevenTvUserId is { } consent
        && string.Equals(consent, account.BotSevenTvUserId, StringComparison.Ordinal)
        && settings.BotEditorCheckedAtUtc is { } checkedAt
        && nowUtc - checkedAt <= EditorCheckFreshness
        && settings.BotEditorCheckedIdentityVersion == account.IdentityVersion;
}
