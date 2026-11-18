using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The default <see cref="IWishBotAvailability"/> until K4 brings the bot (plan #484 finding 13): never available,
/// so the expiry run presents a bot-owned wish with <c>bot_unavailable</c> instead of queueing its removal. K4
/// replaces the one registration in <c>AddEmotePurgeInfrastructure</c>.
/// </summary>
public sealed class WishBotNeverAvailable : IWishBotAvailability
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}
