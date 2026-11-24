namespace EmotePurge.Core.Services;

/// <summary>
/// Whether the wish bot can act at all, globally (not per channel; the per-channel suspension is
/// <c>BotSuspendedReason</c> on the settings). The wishes' view of <see cref="IBotAccountAvailability"/>: available
/// only while <c>Wishes:Enabled</c> is on and the bot account is available (plan #487 2.3).
/// </summary>
public interface IWishBotAvailability
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
