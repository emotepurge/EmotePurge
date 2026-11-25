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
}
