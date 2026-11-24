using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The real <see cref="IWishBotAvailability"/> (plan #487 2.3, Revision 5a): the wishes' view of the bot account,
/// <c>Wishes:Enabled</c> and <see cref="IBotAccountAvailability"/>. It only combines the two; with the wishes off the
/// account is not even asked. Replaces K2's stand-in that was never available, in the same one registration.
/// </summary>
public sealed class WishBotAvailability(WishOptions wishOptions, IBotAccountAvailability botAccount) : IWishBotAvailability
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        wishOptions.Enabled && await botAccount.IsAvailableAsync(cancellationToken);
}
