namespace EmotePurge.Core.Services;

/// <summary>
/// Whether the wish bot can act at all, globally (not per channel; the per-channel state is
/// <see cref="WishBotChannelState"/>). The wishes' view of <see cref="IBotAccountAvailability"/>: available only while
/// <c>Wishes:Enabled</c> is on and the bot account is available (plan #487 2.3).
/// </summary>
public interface IWishBotAvailability
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="IBotAccountAvailability.GetStateAsync"/> with <c>Wishes:Enabled</c> folded into
    /// <see cref="BotAccountAvailabilityState.IsAvailable"/>; with the wishes off
    /// <see cref="BotAccountAvailabilityState.Unavailable"/> without asking the account (Revision 6b, K6b-5).
    /// </summary>
    Task<BotAccountAvailabilityState> GetStateAsync(CancellationToken cancellationToken = default);
}
