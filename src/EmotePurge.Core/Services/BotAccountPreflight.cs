using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>The first "no" of <see cref="BotAccountPreflight.Check"/>, or <see cref="Pass"/>.</summary>
public enum BotAccountPreflightOutcome
{
    /// <summary>Every account stage passed: the token may be used for this call.</summary>
    Pass,

    /// <summary>The kill switch is on (read fresh from the database before a write, AK81). The account is unavailable.</summary>
    KillSwitch,

    /// <summary>A 429 pause is running. Not unavailability: nothing falls back, the call waits for a later pass.</summary>
    Paused,

    /// <summary>The token is not <c>valid</c> or its <c>exp</c> is reached. The account is unavailable (global fallback).</summary>
    TokenNotValid,

    /// <summary>
    /// Only the <c>CredentialVersion</c> differs from the opened token's (a renewal swap or a hand <c>PUT</c>): no fallback
    /// reason; the pass ends without a state change and the next pass opens the new seal (plan 2.5, revision 3).
    /// </summary>
    CredentialChanged,

    /// <summary>
    /// The token may not be used yet (<see cref="IBotAccountSession.TokenUsable"/> false: a verification is outstanding after
    /// an <c>Unauthorized</c> or a lock acquisition). No fallback reason, no suspension; works like a 7TV outage.
    /// </summary>
    TokenUnusable,
}

/// <summary>
/// The account stages of the preflight before every 7TV call with the bot token (#487 plan 2.5, revision 5a; Revision 6b
/// K6b-3 and 6c K6c-5 add <see cref="IBotAccountSession.TokenUsable"/>): kill switch → 429 pause → token status, expiry
/// and <c>CredentialVersion</c> → token usable, in this fixed order; the first "no" wins. Pure: the caller passes the
/// credential row as it read it (under the lock it holds for a send, from the session otherwise) and its clock.
/// <see cref="WishBotPreflight"/> calls it between the coverage and the consent stage; another user of the account
/// would call it the same way.
/// </summary>
public static class BotAccountPreflight
{
    /// <param name="account">The credential row (non-secret part) as the caller read it.</param>
    /// <param name="openedCredentialVersion">The <c>CredentialVersion</c> the token in hand was opened under.</param>
    /// <param name="tokenUsable"><see cref="IBotAccountSession.TokenUsable"/> of the pass.</param>
    /// <param name="nowUtc">The caller's clock.</param>
    public static BotAccountPreflightOutcome Check(
        BotAccountSnapshot account, int openedCredentialVersion, bool tokenUsable, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account.KillSwitch)
        {
            return BotAccountPreflightOutcome.KillSwitch;
        }

        if (account.PausedUntilUtc is { } pausedUntil && pausedUntil > nowUtc)
        {
            return BotAccountPreflightOutcome.Paused;
        }

        if (!string.Equals(account.Status, BotAccountStatus.Valid, StringComparison.Ordinal)
            || account.ExpiresAtUtc is not { } expires
            || expires <= nowUtc)
        {
            return BotAccountPreflightOutcome.TokenNotValid;
        }

        if (account.CredentialVersion != openedCredentialVersion)
        {
            return BotAccountPreflightOutcome.CredentialChanged;
        }

        return tokenUsable ? BotAccountPreflightOutcome.Pass : BotAccountPreflightOutcome.TokenUnusable;
    }
}
