using System.Text.Json;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

/// <summary>
/// Reader and writer of the live-status key in one class: unlike the health/roster pair, both sides
/// are plain Redis string operations with no worker-side state, so splitting them would only
/// duplicate the wire format.
/// </summary>
public class TwitchLiveStatusStore(IConnectionMultiplexer connectionMultiplexer, ILogger<TwitchLiveStatusStore> logger) : ITwitchLiveStatusReader, ITwitchLiveStatusWriter
{
    public async Task<TwitchLiveStatusSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        RedisValue value;
        try
        {
            value = await connectionMultiplexer.GetDatabase().StringGetAsync(TwitchLiveStatusKeys.LiveChannels);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // A missing key and an unreachable Redis both mean the same thing to every consumer here
            // (MyChannelsService derives "unknown" live state): "no statement right now" — so an outage
            // collapses into the identical null a missing/expired key already produces, not a new case.
            logger.LogWarning(ex, "Lesen des Live-Status-Caches fehlgeschlagen — behandle als fehlenden Key.");
            return null;
        }

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return JsonSerializer.Deserialize<TwitchLiveStatusSnapshot>(value.ToString(), JsonSerializerOptions.Web);
    }

    public async Task PublishAsync(TwitchLiveStatusSnapshot snapshot, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(snapshot, JsonSerializerOptions.Web);
        await connectionMultiplexer.GetDatabase().StringSetAsync(TwitchLiveStatusKeys.LiveChannels, payload, timeToLive);
    }
}

/// <summary>
/// Shared between the worker (writer) and the API (reader), like <see cref="WorkerHealthKeys"/>.
/// </summary>
public static class TwitchLiveStatusKeys
{
    public const string LiveChannels = "worker:live-status";

    /// <summary>
    /// Twice the poll interval, so one missed poll (transient Helix failure costs exactly one tick)
    /// does not read as "unknown" — but a worker that stays gone does, instead of an ever-staler
    /// key claiming everyone is offline.
    /// </summary>
    public static TimeSpan TimeToLiveFor(TimeSpan pollInterval) => pollInterval * 2;
}
