using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The one path <c>wish.changed</c> takes to Redis. It lives in Infrastructure rather than in an endpoint
/// handler because the reconcile runs in the Worker (sync) and in the Api (report), and every writer must
/// announce the same way (plan finding 12). Same shape as the publishing of the backfill removal: after the
/// commit, errors logged and swallowed.
/// </summary>
public sealed class WishChangedPublisher(IRedisPublisher redisPublisher, ILogger<WishChangedPublisher> logger) : IWishChangedPublisher
{
    public async Task PublishAsync(string channelName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);

        var liveEvent = new LiveEvent(LiveEvents.WishChanged, ChannelName.Normalize(channelName));
        try
        {
            await redisPublisher.PublishAsync(LiveEvents.Channel, liveEvent.Serialize(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Publishing {EventType} after a wish change failed; the change is committed and clients converge on their next fetch.",
                liveEvent.Type);
        }
    }
}
