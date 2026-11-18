using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The one way the slot writers (full sync, EventAPI delta, in-set report) start the wish reconcile: after
/// their commit, outside their retry loops. It never lets the reconcile endanger the write that called it.
/// </summary>
public interface IWishReconcileTrigger
{
    /// <summary>
    /// Reconciles the wishes of <paramref name="channelId"/> unless the feature switch is off. Swallows every
    /// failure except cancellation; the next reconcile heals what a swallowed failure left behind.
    /// </summary>
    Task TriggerAsync(string channelId, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IWishReconcileTrigger"/>: switch check, call, swallow-and-warn.</summary>
public sealed class WishReconcileTrigger(
    IWishReconcileService reconcile,
    WishOptions options,
    ILogger<WishReconcileTrigger> logger) : IWishReconcileTrigger
{
    public async Task TriggerAsync(string channelId, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            await reconcile.ReconcileChannelAsync(channelId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No alias and no requester: the channel id and the exception are all a log line needs here.
            logger.LogWarning(ex, "Wish reconcile for channel {ChannelId} failed after a slot write; the next run heals it.", channelId);
        }
    }
}
