using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>
/// The pure decisions of the bot account's token upkeep (plan #487 2.8): when a token has expired, when it needs a
/// (fresh) verification, when the worker may call 7TV with it at all, and when the expiry warning is due. No I/O, no
/// clock of its own; the runner books what these say.
/// </summary>
public static class BotAccountTokenUpkeepPolicy
{
    /// <summary>A <c>valid</c> token is verified again after this long (plan 2.8 [E]).</summary>
    public static readonly TimeSpan ReverifyInterval = TimeSpan.FromHours(6);

    /// <summary>From this long before <c>exp</c> the worker warns, at most once per UTC day.</summary>
    public static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Whether the upkeep works on a token in <paramref name="status"/> at all: only <c>unverified</c> and <c>valid</c>.
    /// <c>invalid</c> stays until an admin acts and <c>expired</c> until a new token or a swap (revision 4); neither is
    /// opened, verified or moved by the worker.
    /// </summary>
    public static bool IsUpkept(string status) =>
        status is BotAccountStatus.Unverified or BotAccountStatus.Valid;

    /// <summary><c>exp</c> reached: <paramref name="now"/> at or past it. A missing expiry is never "expired" here.</summary>
    public static bool HasExpired(DateTime? expiresAtUtc, DateTime now) =>
        expiresAtUtc is { } at && at <= now;

    /// <summary>
    /// Whether the token of <paramref name="credentialVersion"/> needs a verification: always while <c>unverified</c>;
    /// while <c>valid</c> when <paramref name="forced"/> (an <c>Unauthorized</c> since), when it was never verified
    /// (a renewal swap leaves <c>LastVerifiedAtUtc</c> empty), when the last verification was of another version, or
    /// when it is <see cref="ReverifyInterval"/> old. Never for any other status.
    /// </summary>
    public static bool IsVerificationDue(
        string status,
        DateTime? lastVerifiedAtUtc,
        int? lastVerifiedCredentialVersion,
        int credentialVersion,
        DateTime now,
        bool forced)
    {
        if (status == BotAccountStatus.Unverified)
        {
            return true;
        }

        if (status != BotAccountStatus.Valid)
        {
            return false;
        }

        return forced
            || lastVerifiedAtUtc is not { } verifiedAt
            || lastVerifiedCredentialVersion != credentialVersion
            || verifiedAt + ReverifyInterval <= now;
    }

    /// <summary>The kill switch and a running 429 pause both hold every 7TV call with the token, the verification too.</summary>
    public static bool MayCallSevenTv(bool killSwitch, DateTime? pausedUntilUtc, DateTime now) =>
        !killSwitch && !(pausedUntilUtc is { } until && until > now);

    /// <summary>
    /// Whether to warn now: <c>exp</c> lies within <see cref="ExpiryWarningWindow"/> and still ahead, and no warning was
    /// given on <paramref name="now"/>'s UTC day yet (<paramref name="lastWarnedDayUtc"/> is kept in memory, so a restart
    /// may warn once more).
    /// </summary>
    public static bool ShouldWarnAboutExpiry(DateTime? expiresAtUtc, DateTime now, DateOnly? lastWarnedDayUtc) =>
        expiresAtUtc is { } at
        && at > now
        && at - now <= ExpiryWarningWindow
        && lastWarnedDayUtc != DateOnly.FromDateTime(now);
}
