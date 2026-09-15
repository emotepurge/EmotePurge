using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Redis;

namespace EmotePurge.Worker;

// Polls GET /helix/streams for every active channel (idea A10, stage 1) and credits the live
// channels one poll interval's worth of minutes in ChannelLiveDays. Runs on the app access token
// (client credentials) — the endpoint needs no scope, and the worker holds no user tokens.
// Deliberately independent of BootRecoveryGate: it only writes coverage rows, which nothing in
// boot recovery touches, and an immediate first poll makes a worker restart observable right away.
public class TwitchLivePollWorker(
    ILogger<TwitchLivePollWorker> logger,
    ITwitchAppTokenProvider appTokenProvider,
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ITwitchLiveStatusWriter liveStatusWriter,
    ITwitchLiveStatusReader liveStatusReader,
    IRedisPublisher redisPublisher) : BackgroundService
{
    // 300s default: minute-precise coverage is not the goal (the consumer is a per-day marker),
    // and 12 requests/hour stay far below the app token's Helix rate budget.
    private readonly TimeSpan _pollInterval =
        TimeSpan.FromSeconds(configuration.GetValue("Twitch:LivePollIntervalSeconds", 300));

    // Baseline for the transition diff — null until the first successful publish. Not the Redis
    // snapshot itself: reading it back every tick would race the write, and in-memory is exact.
    private IReadOnlySet<string>? _lastPublishedLiveLogins;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Without client credentials every tick would fail on the token fetch — say it once
        // instead of warning forever. The Api shares the same config values, so a correctly
        // configured deployment cannot land here.
        if (string.IsNullOrEmpty(configuration["Auth:Twitch:ClientId"]) ||
            string.IsNullOrEmpty(configuration["Auth:Twitch:ClientSecret"]))
        {
            logger.LogWarning("Twitch-Live-Poll deaktiviert — Auth:Twitch:ClientId/ClientSecret nicht konfiguriert.");
            return;
        }

        using var timer = new PeriodicTimer(_pollInterval);
        do
        {
            await PollOnceAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var channelService = scope.ServiceProvider.GetRequiredService<IChannelService>();
            var activeChannels = await channelService.ListActiveChannelNamesAsync(ct);
            if (activeChannels.Count == 0)
            {
                return;
            }

            var accessToken = await appTokenProvider.GetTokenAsync(ct);
            if (accessToken is null)
            {
                logger.LogWarning("Live-Poll übersprungen — kein Twitch-App-Token verfügbar.");
                return;
            }

            var helixClient = scope.ServiceProvider.GetRequiredService<ITwitchHelixClient>();
            var streams = await helixClient.GetLiveStreamsByLoginsAsync(activeChannels, accessToken, ct);
            if (streams is null)
            {
                // Null must not read as "everyone offline". The token is the most likely culprit
                // (revoked by a secret rotation) — drop it so the next tick starts fresh.
                appTokenProvider.Invalidate();
                logger.LogWarning("Live-Poll fehlgeschlagen — Helix-Streams-Abfrage ohne Ergebnis, Tick wird übersprungen.");
                return;
            }

            // A successful poll is a statement about every polled channel — including "nobody is
            // live" — so publish before the empty-result early-return below. Only a failed poll
            // (the null guard above) publishes nothing and lets the key age out into "unknown".
            await PublishLiveStatusAsync(streams, ct);

            if (streams.Count == 0)
            {
                return;
            }

            var coverageService = scope.ServiceProvider.GetRequiredService<ILiveCoverageService>();
            var minutes = Math.Max(1, (int)Math.Round(_pollInterval.TotalMinutes));
            var credited = await coverageService.AddLiveMinutesAsync(
                streams.Select(s => s.UserLogin).ToList(),
                DateOnly.FromDateTime(DateTime.UtcNow),
                minutes,
                ct);

            // Only reached when someone is live, so quiet nights stay quiet in the log.
            logger.LogInformation("Live-Poll: {LiveCount} von {TotalCount} Channels live, {Credited} Abdeckungszeilen fortgeschrieben.",
                streams.Count, activeChannels.Count, credited);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Regulärer Shutdown.
        }
        catch (Exception ex)
        {
            // Runs forever on a timer — a Postgres hiccup or a Twitch outage must cost one tick,
            // never the worker host (StopHost default would take the usage flush down with it).
            logger.LogWarning(ex, "Live-Poll-Durchlauf fehlgeschlagen.");
        }
    }

    // Best-effort with its own catch: a Redis hiccup must not cost the coverage rows that follow.
    private async Task PublishLiveStatusAsync(IReadOnlyList<TwitchStreamInfo> streams, CancellationToken ct)
    {
        try
        {
            // UserLogin is documented as already-lowercase, but the key is a cross-process
            // contract keyed by normalized names — normalize anyway (rule 9).
            var liveLogins = streams
                .Select(s => ChannelName.Normalize(s.UserLogin))
                .Distinct()
                .ToList();

            // Baseline for the diff. In-memory after the first poll; across a worker restart the
            // previous Redis snapshot (TTL = twice the poll interval) fills in, so a flip during a
            // short restart still produces its event instead of being swallowed. Read before the
            // write below overwrites it.
            var baseline = _lastPublishedLiveLogins;
            if (baseline is null)
            {
                var previousSnapshot = await liveStatusReader.ReadAsync(ct);
                baseline = previousSnapshot?.LiveChannelLogins.ToHashSet(StringComparer.Ordinal);
            }

            await liveStatusWriter.PublishAsync(
                new TwitchLiveStatusSnapshot(DateTime.UtcNow, liveLogins),
                TwitchLiveStatusKeys.TimeToLiveFor(_pollInterval),
                ct);

            _lastPublishedLiveLogins = liveLogins.ToHashSet(StringComparer.Ordinal);

            var changes = LiveStatusDiff.Compute(baseline, liveLogins);
            if (changes.IsEmpty)
            {
                return;
            }

            foreach (var channelName in changes.WentLive)
            {
                await redisPublisher.PublishLiveChangedAsync(logger, channelName, ct);
            }

            foreach (var channelName in changes.WentOffline)
            {
                await redisPublisher.PublishLiveChangedAsync(logger, channelName, ct);
            }

            logger.LogInformation(
                "Live-Status-Wechsel publiziert: live gegangen [{WentLive}], offline gegangen [{WentOffline}].",
                string.Join(", ", changes.WentLive),
                string.Join(", ", changes.WentOffline));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live-Status-Publish nach Redis fehlgeschlagen — Key läuft aus, UI zeigt „unbekannt“.");
        }
    }
}
