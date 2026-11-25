using System.Text.Json;
using System.Text.Json.Serialization;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Worker.SevenTv;
using StackExchange.Redis;

namespace EmotePurge.Worker;

// Publishes the per-channel roster to its own Redis key, so an admin can answer "why is this channel
// not syncing?" without SSH and docker compose logs. The two transports that have to agree — Twitch
// IRC and the 7TV EventAPI — are joined here rather than in the Api, because only this process knows
// its own in-memory state; the Api's job is to compare that against the database.
//
// A separate hosted service rather than four more fields on WorkerHealthPublisher: that one is a
// single PeriodicTimer at 20s, this needs a third of the rate and a different dependency set, and
// the health payload is read by every open monitoring page on every tick. Regel 5 covers a
// BackgroundService as a concrete class.
public class WorkerRosterPublisher(
    ILogger<WorkerRosterPublisher> logger,
    ITwitchChatManager twitchChatManager,
    SevenTvSubscriptionRegistry subscriptionRegistry,
    BootRecoveryGate bootRecoveryGate,
    WorkerIdentity identity,
    IRedisPublisher redisPublisher,
    IConnectionMultiplexer redis) : BackgroundService
{
    private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(60);

    // Far above the Twitch-side ceiling this worker can ever reach on one connection (100 concurrent
    // channels, see CLAUDE.md), so it never trips in practice — it exists so the Redis value cannot
    // grow without bound if that ever changes, and the Truncated flag says so out loud when it does.
    private const int MaxChannels = 500;

    // Own instance, never a mutation of the shared JsonSerializerOptions.Web: null-heavy rows are the
    // common case (a channel with no resolved 7TV user id has three of seven fields null) and this
    // payload is re-serialized every minute for as long as the worker runs.
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PublishInterval);
        do
        {
            await PublishOnceAsync();
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PublishOnceAsync()
    {
        try
        {
            var payload = JsonSerializer.Serialize(BuildSnapshot(), PayloadOptions);

            // The key is deliberately not deleted on shutdown: a clean stop and a crash should look
            // identical to a reader, and the TTL already covers both. Deleting it would make an
            // ordinary redeploy indistinguishable from a worker that never came back.
            await redis.GetDatabase().StringSetAsync(WorkerHealthKeys.Roster, payload, WorkerHealthKeys.RosterTtl);

            // Notification only, same split as the health publisher: the snapshot is the TTL key, the
            // event just says a new one exists so open admin pages refetch instead of polling.
            await redisPublisher.PublishAsync(LiveEvents.Channel, new LiveEvent(LiveEvents.WorkerRoster).Serialize());
        }
        catch (Exception ex)
        {
            // Same rule as the health publish: this must never take the worker host down. The key
            // expires and the API correctly reports the roster as unavailable.
            logger.LogWarning(ex, "Roster-Publish nach Redis fehlgeschlagen.");
        }
    }

    internal WorkerRosterSnapshot BuildSnapshot()
    {
        // Union, not intersection: a channel present in only one of the two transports is exactly
        // the case worth showing, and an inner join would drop it.
        var byChannel = new Dictionary<string, WorkerRosterChannel>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in twitchChatManager.GetRoster())
        {
            byChannel[entry.ChannelName] = new WorkerRosterChannel(
                entry.ChannelName,
                entry.JoinConfirmed,
                entry.LastMessageUtc,
                SevenTvEmoteSetId: null,
                SevenTvEmoteSetAcknowledged: false,
                SevenTvUserId: null,
                SevenTvUserAcknowledged: false,
                IrcIntent: true,
                entry.UnconfirmedSeconds,
                entry.JoinFailureReason);
        }

        foreach (var state in subscriptionRegistry.GetChannelStates())
        {
            byChannel.TryGetValue(state.ChannelName, out var ircSide);
            byChannel[state.ChannelName] = new WorkerRosterChannel(
                // The IRC spelling wins when both are present: it is the one Twitch confirmed.
                ircSide?.ChannelName ?? state.ChannelName,
                ircSide?.IrcJoinConfirmed ?? false,
                ircSide?.LastMessageUtc,
                state.EmoteSetId,
                state.EmoteSetAcknowledged,
                state.SevenTvUserId,
                state.UserAcknowledged,
                // False for a 7TV-only row: the IRC side has no wish for it.
                ircSide?.IrcIntent ?? false,
                ircSide?.IrcUnconfirmedSeconds,
                ircSide?.IrcJoinFailureReason);
        }

        var channels = byChannel.Values
            .OrderBy(c => c.ChannelName, StringComparer.Ordinal)
            .ToList();

        var truncated = channels.Count > MaxChannels;
        if (truncated)
        {
            logger.LogWarning(
                "Roster auf {Max} von {Total} Channels gekürzt — der Snapshot ist unvollständig.",
                MaxChannels, channels.Count);
            channels = channels.GetRange(0, MaxChannels);
        }

        return new WorkerRosterSnapshot(
            DateTime.UtcNow,
            identity.InstanceId,
            identity.ProcessStartedUtc,
            // Not "did boot recovery succeed" but "is it over": until it is, every channel missing
            // from the roster is expected, and a reader that does not know that alerts on every
            // single deploy.
            bootRecoveryGate.Completed.IsCompleted,
            truncated,
            channels,
            WorkerRosterSnapshot.CurrentSchemaVersion);
    }
}
