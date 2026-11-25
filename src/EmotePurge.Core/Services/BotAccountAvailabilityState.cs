using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>
/// The account-wide availability of the bot account together with what the per-channel state needs from the same
/// read (plan #487 2.3, Revision 6b K6b-5): the bot's current 7TV id (against the channel's consent) and the
/// <c>IdentityVersion</c> (against the editor proof). No token material, no seal, no status beyond the answer.
/// </summary>
/// <param name="IsAvailable">The answer of <see cref="IBotAccountAvailability.IsAvailableAsync"/> for the same row.</param>
/// <param name="BotSevenTvUserId">The bot account's 7TV id as the row holds it; null without a row or a switch.</param>
/// <param name="IdentityVersion">The row's <c>IdentityVersion</c>; 0 without a row or a switch.</param>
public readonly record struct BotAccountAvailabilityState(bool IsAvailable, string? BotSevenTvUserId, int IdentityVersion)
{
    /// <summary>A switch off or no credential row: not available, nothing to pass on.</summary>
    public static BotAccountAvailabilityState Unavailable { get; } = new(false, null, 0);

    /// <summary>
    /// The one rule behind the account-wide answer, shared by every reader of the row (plan 2.3, Revision 6d K6d-7):
    /// the switches of the own process on, <c>Status = valid</c>, an expiry in the future and the kill switch off. A pause
    /// after a 429 is deliberately not part of it.
    /// </summary>
    public static bool IsUsable(bool switchesOn, string? status, DateTime? expiresAtUtc, bool killSwitch, DateTime nowUtc) =>
        switchesOn
        && string.Equals(status, BotAccountStatus.Valid, StringComparison.Ordinal)
        && expiresAtUtc is { } expires
        && expires > nowUtc
        && !killSwitch;

    /// <summary>The state of a read credential row; the ids are passed on whether or not the account is usable.</summary>
    public static BotAccountAvailabilityState Of(
        bool switchesOn,
        string? status,
        DateTime? expiresAtUtc,
        bool killSwitch,
        string? botSevenTvUserId,
        int identityVersion,
        DateTime nowUtc) =>
        new(IsUsable(switchesOn, status, expiresAtUtc, killSwitch, nowUtc), botSevenTvUserId, identityVersion);
}
