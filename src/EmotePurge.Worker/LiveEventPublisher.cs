using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;

namespace EmotePurge.Worker;

/// <summary>
/// The worker's single place for publishing the thin live events, so the swallow-and-log contract
/// exists exactly once: whatever the event announces is already committed by the time this runs, and
/// a Redis hiccup must never turn a successful operation into a failed one.
/// </summary>
/// <remarks>
/// Producers are the three emote-sync paths for <see cref="LiveEvents.ChannelSynced"/> (boot
/// recovery and Redis commands in <see cref="Worker"/>, the periodic resync, the EventAPI client)
/// plus <see cref="TwitchLivePollWorker"/> for <see cref="LiveEvents.LiveChanged"/> and
/// <see cref="ChatLogBackfill.ChatLogBackfillWorker"/> for <see cref="LiveEvents.BackfillProgress"/> and
/// <see cref="LiveEvents.UsageFlushed"/>.
/// </remarks>
internal static class LiveEventPublisher
{
    public static async Task PublishChannelSyncedAsync(
        this IRedisPublisher redisPublisher,
        ILogger logger,
        string channelName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await redisPublisher.PublishAsync(
                LiveEvents.Channel,
                new LiveEvent(LiveEvents.ChannelSynced, ChannelName.Normalize(channelName)).Serialize(),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Live-Event '{Type}' für {Channel} konnte nicht veröffentlicht werden.",
                LiveEvents.ChannelSynced, channelName);
        }
    }

    /// <summary>
    /// Same swallow-and-log contract as above, for <see cref="LiveEvents.LiveChanged"/>: the poll
    /// result is already persisted when this runs, and a Redis pub/sub hiccup must never fail it.
    /// </summary>
    public static async Task PublishLiveChangedAsync(
        this IRedisPublisher redisPublisher,
        ILogger logger,
        string channelName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await redisPublisher.PublishAsync(
                LiveEvents.Channel,
                new LiveEvent(LiveEvents.LiveChanged, ChannelName.Normalize(channelName)).Serialize(),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Live-Event '{Type}' für {Channel} konnte nicht veröffentlicht werden.",
                LiveEvents.LiveChanged, channelName);
        }
    }

    /// <summary>
    /// Same swallow-and-log contract, for the chat-log backfill's two events (spec 6): the block or
    /// transition they announce is already committed, and a lost event only delays a refetch until
    /// the next one or the page's own reload.
    /// </summary>
    public static async Task PublishChannelEventAsync(
        this IRedisPublisher redisPublisher,
        ILogger logger,
        string type,
        string channelName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await redisPublisher.PublishAsync(
                LiveEvents.Channel,
                new LiveEvent(type, ChannelName.Normalize(channelName)).Serialize(),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live event '{Type}' for {Channel} could not be published.", type, channelName);
        }
    }
}
