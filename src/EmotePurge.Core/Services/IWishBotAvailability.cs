namespace EmotePurge.Core.Services;

/// <summary>
/// Whether the wish bot can act at all, globally (not per channel; the per-channel suspension is
/// <c>BotSuspendedReason</c> on the settings). K2 registers a default that always answers false; K4 replaces
/// it with the real answer (one registration).
/// </summary>
public interface IWishBotAvailability
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
