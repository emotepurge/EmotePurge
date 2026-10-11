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
    public Task<TwitchLiveStatusSnapshot?> ReadAsync(CancellationToken cancellationToken = default) =>
        ReadCoreAsync(logFailures: true);

    public Task<TwitchLiveStatusSnapshot?> ReadQuietlyAsync(CancellationToken cancellationToken = default) =>
        ReadCoreAsync(logFailures: false);

    public async Task PublishAsync(TwitchLiveStatusSnapshot snapshot, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(snapshot, JsonSerializerOptions.Web);
        await connectionMultiplexer.GetDatabase().StringSetAsync(TwitchLiveStatusKeys.LiveChannels, payload, timeToLive);
    }

    private async Task<TwitchLiveStatusSnapshot?> ReadCoreAsync(bool logFailures)
    {
        RedisValue value;
        try
        {
            value = await connectionMultiplexer.GetDatabase().StringGetAsync(TwitchLiveStatusKeys.LiveChannels);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // The logged read maps an outage to the same unknown state as a missing key. The quiet read
            // lets high-frequency callers propagate it to their own throttled failure handling.
            if (!logFailures)
            {
                throw;
            }

            logger.LogWarning(ex, "Reading the live-status cache failed; treating it as a missing key.");
            return null;
        }

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return JsonSerializer.Deserialize<TwitchLiveStatusSnapshot>(value.ToString(), JsonSerializerOptions.Web);
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
