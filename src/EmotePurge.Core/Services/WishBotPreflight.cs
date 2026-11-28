using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>The first "no" of <see cref="WishBotPreflight.Check"/>, or <see cref="Pass"/>.</summary>
public enum WishBotPreflightOutcome
{
    /// <summary>Every stage passed.</summary>
    Pass,

    /// <summary><c>Wishes:Enabled</c> or <c>BotAccount:Enabled</c> is off in this process (global fallback).</summary>
    SwitchedOff,

    /// <summary>Neither on <c>Wishes:PilotChannelIds</c> nor activated by a global admin by id (E8): per channel, <c>not_listed</c>.</summary>
    NotCovered,

    /// <summary>The account stage <see cref="BotAccountPreflightOutcome.KillSwitch"/> (global fallback).</summary>
    KillSwitch,

    /// <summary>The account stage <see cref="BotAccountPreflightOutcome.Paused"/> (no fallback, wait).</summary>
    Paused,

    /// <summary>The account stage <see cref="BotAccountPreflightOutcome.TokenNotValid"/> (global fallback).</summary>
    TokenNotValid,

    /// <summary>The account stage <see cref="BotAccountPreflightOutcome.CredentialChanged"/> (no fallback, the pass ends).</summary>
    CredentialChanged,

    /// <summary>The account stage <see cref="BotAccountPreflightOutcome.TokenUnusable"/> (no fallback, no suspension).</summary>
    TokenUnusable,

    /// <summary>The channel's consent belongs to another bot account (plan 2.5b S6-2): per channel, <c>bot_account_changed</c>.</summary>
    BotAccountChanged,

    /// <summary>
    /// No positive editor proof for the current identity within the last minute, or the channel is suspended: nothing is
    /// written until the next editor read proves it again (plan 2.3, 2.5).
    /// </summary>
    EditorUnproven,
}

/// <summary>
/// The preflight before every 7TV write of the wish bot (#487 plan 2.5, spec K4), pure and in a fixed order: switches →
/// coverage (E8) → the account stages of <see cref="BotAccountPreflight"/> (kill switch, pause, token, credential
/// version, token usable) → the channel's consent applies to the current bot account (Revision 6a, S6-2) → the channel's
/// editor proof (at most <see cref="EditorProofFreshness"/> old, booked against the current <c>IdentityVersion</c>,
/// channel not suspended; positive is only <c>editor</c>, plan 2.5b). The first "no" wins and names the fallback, if any
/// (<see cref="SuspensionReasonOf"/>). Reads for a confirmation or a pre-read check the same chain without the editor
/// stage (<c>requireEditorProof: false</c>). The caller runs it as part of the re-check under its locks (plan 2.5a (c)),
/// with the credential row it locked.
/// </summary>
public static class WishBotPreflight
{
    /// <summary>How old the editor proof of a channel may be at a write (plan 2.5: one minute, the life of one read).</summary>
    public static readonly TimeSpan EditorProofFreshness = TimeSpan.FromMinutes(1);

    /// <param name="switchesOn"><c>Wishes:Enabled</c> and <c>BotAccount:Enabled</c> of this process.</param>
    /// <param name="covered">Listed on the pilot list, or the activator is a global admin by id.</param>
    /// <param name="settings">The channel's settings row as the caller read it.</param>
    /// <param name="account">The credential row (non-secret part) as the caller read it.</param>
    /// <param name="openedCredentialVersion">The <c>CredentialVersion</c> the token in hand was opened under.</param>
    /// <param name="tokenUsable"><see cref="IBotAccountSession.TokenUsable"/> of the pass.</param>
    /// <param name="nowUtc">The caller's clock.</param>
    /// <param name="requireEditorProof">False for reads (confirmation, pre-read): the chain ends after the consent stage.</param>
    public static WishBotPreflightOutcome Check(
        bool switchesOn,
        bool covered,
        ChannelWishSettings settings,
        BotAccountSnapshot account,
        int openedCredentialVersion,
        bool tokenUsable,
        DateTime nowUtc,
        bool requireEditorProof = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(account);

        if (!switchesOn)
        {
            return WishBotPreflightOutcome.SwitchedOff;
        }

        if (!covered)
        {
            return WishBotPreflightOutcome.NotCovered;
        }

        switch (BotAccountPreflight.Check(account, openedCredentialVersion, tokenUsable, nowUtc))
        {
            case BotAccountPreflightOutcome.KillSwitch:
                return WishBotPreflightOutcome.KillSwitch;
            case BotAccountPreflightOutcome.Paused:
                return WishBotPreflightOutcome.Paused;
            case BotAccountPreflightOutcome.TokenNotValid:
                return WishBotPreflightOutcome.TokenNotValid;
            case BotAccountPreflightOutcome.CredentialChanged:
                return WishBotPreflightOutcome.CredentialChanged;
            case BotAccountPreflightOutcome.TokenUnusable:
                return WishBotPreflightOutcome.TokenUnusable;
        }

        if (!ConsentApplies(settings, account.BotSevenTvUserId))
        {
            return WishBotPreflightOutcome.BotAccountChanged;
        }

        if (!requireEditorProof)
        {
            return WishBotPreflightOutcome.Pass;
        }

        return settings.BotSuspendedReason is null
            && settings.BotEditorCheckedAtUtc is { } checkedAt
            && nowUtc - checkedAt <= EditorProofFreshness
            && settings.BotEditorCheckedIdentityVersion == account.IdentityVersion
                ? WishBotPreflightOutcome.Pass
                : WishBotPreflightOutcome.EditorUnproven;
    }

    /// <summary>
    /// The channel's suspension reason a "no" stands for (plan 2.8): <c>not_listed</c> for <see cref="WishBotPreflightOutcome.NotCovered"/>,
    /// <c>bot_account_changed</c> for <see cref="WishBotPreflightOutcome.BotAccountChanged"/>, otherwise null (global, a
    /// wait, or nothing to book from the preflight itself).
    /// </summary>
    public static string? SuspensionReasonOf(WishBotPreflightOutcome outcome) => outcome switch
    {
        WishBotPreflightOutcome.NotCovered => WishBotChannelReasons.NotListed,
        WishBotPreflightOutcome.BotAccountChanged => WishBotChannelReasons.BotAccountChanged,
        _ => null,
    };

    /// <summary>The consent applies to <paramref name="botSevenTvUserId"/>; a channel without a bound id never matches (plan 2.5b).</summary>
    public static bool ConsentApplies(ChannelWishSettings settings, string? botSevenTvUserId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.BotConsentSevenTvUserId is { } consent
            && botSevenTvUserId is not null
            && string.Equals(consent, botSevenTvUserId, StringComparison.Ordinal);
    }
}
