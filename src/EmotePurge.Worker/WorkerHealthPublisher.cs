using System.Text.Json;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Worker.SevenTv;
using StackExchange.Redis;

namespace EmotePurge.Worker;

// Schreibt periodisch den Twitch-Verbindungsstatus nach Redis (TTL-Key), damit die Api ihn
// über GET /api/worker/health nach außen zeigen kann, ohne dass Api und Worker direkt
// miteinander kommunizieren müssen — nutzt dieselbe Redis-Infrastruktur wie die
// channel:bot:commands-Pub/Sub. Läuft der Worker nicht (mehr), läuft der Key einfach ab.
//
// Key, TTL und Payload-Typ kommen aus Infrastructure bzw. Core (WorkerHealthKeys,
// WorkerHealthSnapshot): Vorher war das Format hier ein anonymes Objekt und in der Api ein
// eigenes privates Record — dasselbe Wire-Format zweimal deklariert, ohne Verbindung.
public class WorkerHealthPublisher(
    ILogger<WorkerHealthPublisher> logger,
    ITwitchChatManager twitchChatManager,
    ISevenTvEventClient sevenTvEventClient,
    SevenTvSubscriptionRegistry subscriptionRegistry,
    WorkerStats stats,
    IEmoteUsageCounter usageCounter,
    WorkerIdentity identity,
    IConfiguration configuration,
    IRedisPublisher redisPublisher,
    IConnectionMultiplexer redis,
    WishOptions wishOptions) : BackgroundService
{
    private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(20);

    // Read the same way SevenTvPeriodicResyncWorker reads it, including the default, so the number
    // the admin page shows is the one the resync loop actually paces itself by.
    private readonly int _resyncIntervalSeconds = configuration.GetValue("SevenTv:ResyncIntervalSeconds", 60);

    // Liveness file for the container HEALTHCHECK (S3-35): touched only after a successful publish,
    // so a stale mtime means "this loop is not doing its job" — whether the process hung or Redis
    // is unreachable. Set via ENV in the Dockerfile (which is where the HEALTHCHECK reading it
    // lives); empty outside the container, e.g. local `dotnet run` on Windows, where /tmp does
    // not exist.
    private readonly string? _heartbeatFilePath =
        configuration.GetValue<string?>("Worker:HeartbeatFilePath", null);

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
            // Same key, one contract (see the class comment): the 7TV connection state rides in the
            // same snapshot instead of a second Redis key.
            var payload = JsonSerializer.Serialize(
                new WorkerHealthSnapshot(
                    twitchChatManager.IsConnected,
                    twitchChatManager.LastMessageReceivedUtc,
                    twitchChatManager.ConnectAttemptedUtc,
                    sevenTvEventClient.IsEnabled,
                    sevenTvEventClient.IsConnected,
                    sevenTvEventClient.LastFrameReceivedUtc,
                    sevenTvEventClient.LastDispatchReceivedUtc,
                    sevenTvEventClient.ConnectAttemptedUtc,
                    // Detail fields for the admin monitoring page — same key, same snapshot, no
                    // second Redis key (see the class comment).
                    subscriptionRegistry.DesiredChannels.Count,
                    subscriptionRegistry.DesiredSubscriptionCount,
                    subscriptionRegistry.UnacknowledgedCount,
                    stats.ConsecutiveFlushFailures,
                    stats.LastFlushSuccessUtc,
                    stats.LastFlushRowCount,
                    usageCounter.PendingEmoteCount,
                    // Capacity context: what 7TV said the limit is, how often we ask its REST API,
                    // and since when this process has been counting any of it.
                    sevenTvEventClient.SubscriptionLimit,
                    _resyncIntervalSeconds,
                    identity.ProcessStartedUtc,
                    identity.InstanceId,
                    twitchChatManager.LastFrameReceivedUtc,
                    wishOptions.CaptureActive),
                JsonSerializerOptions.Web);

            await redis.GetDatabase().StringSetAsync(WorkerHealthKeys.TwitchConnection, payload, WorkerHealthKeys.Ttl);

            // Nur die Benachrichtigung, nicht der Zustand: der Snapshot bleibt der TTL-Key oben,
            // das Event sagt bloß "es gibt einen neuen" — offene Admin-Seiten holen ihn sich dann
            // über GET /api/admin/health statt weiter im 20-Sekunden-Takt zu pollen. Bewusst im
            // bestehenden try: schlägt es fehl, gilt dieselbe Regel wie für das Health-Update.
            await redisPublisher.PublishAsync(LiveEvents.Channel, new LiveEvent(LiveEvents.WorkerHealth).Serialize());

            // Deliberately last in the try: only a fully successful cycle counts as alive.
            if (!string.IsNullOrWhiteSpace(_heartbeatFilePath))
            {
                await File.WriteAllTextAsync(_heartbeatFilePath, DateTime.UtcNow.ToString("O"));
            }
        }
        catch (Exception ex)
        {
            // Ein fehlgeschlagenes Health-Update darf den Worker-Host nicht mitreißen —
            // der Key läuft dann einfach ab und die Api zeigt korrekt "unbekannt/veraltet" an.
            logger.LogWarning(ex, "Health-Status-Publish nach Redis fehlgeschlagen.");
        }
    }
}
