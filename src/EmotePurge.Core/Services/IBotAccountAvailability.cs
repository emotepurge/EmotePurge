namespace EmotePurge.Core.Services;

/// <summary>
/// The account-wide answer to "can the bot account write at all" (plan #487 2.3, Revision 5a), without any wish rule:
/// <c>BotAccount:Enabled</c> of the own process, <c>Status = valid</c>, <c>ExpiresAtUtc</c> in the future and the kill
/// switch off. A pause after a 429 is <b>not</b> unavailability (a short 429 must not present every due removal). The
/// wishes' view, which adds <c>Wishes:Enabled</c>, is <see cref="IWishBotAvailability"/>.
/// </summary>
public interface IBotAccountAvailability
{
    /// <summary>Reads the credential row fresh on every call; without the switch it answers <c>false</c> without a read.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The same answer together with the bot's 7TV id and the <c>IdentityVersion</c>, from one fresh read of the row
    /// (Revision 6b, K6b-5). Without the switch, and without a row, <see cref="BotAccountAvailabilityState.Unavailable"/>;
    /// without the switch no read at all.
    /// </summary>
    Task<BotAccountAvailabilityState> GetStateAsync(CancellationToken cancellationToken = default);
}
