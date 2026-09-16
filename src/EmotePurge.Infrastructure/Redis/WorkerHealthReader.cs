using System.Text.Json;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

public class WorkerHealthReader(IConnectionMultiplexer connectionMultiplexer, ILogger<WorkerHealthReader> logger) : IWorkerHealthReader
{
    /// <summary>
    /// The one place this key is named. The worker writes the same constant through
    /// <see cref="WorkerHealthKeys"/>; the API no longer knows it at all.
    /// </summary>
    public async Task<WorkerHealthSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        RedisValue value;
        try
        {
            value = await connectionMultiplexer.GetDatabase().StringGetAsync(WorkerHealthKeys.TwitchConnection);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // A missing key and an unreachable Redis mean the same thing to both consumers of this
            // reader: "no snapshot right now". /api/worker/health already renders that as
            // { status = "unknown" }; /api/health already renders it as the deliberate 503 dead-man's
            // switch for the container HEALTHCHECK and Uptime Kuma (see the comment there) — a Redis
            // outage collapsing into that same null is the already-designed degradation, not a new
            // one, and never a laundered 200.
            logger.LogWarning(ex, "Lesen des Worker-Health-Snapshots fehlgeschlagen — behandle als fehlenden Key.");
            return null;
        }

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return JsonSerializer.Deserialize<WorkerHealthSnapshot>(value.ToString(), JsonSerializerOptions.Web);
    }
}

public class WorkerRosterReader(IConnectionMultiplexer connectionMultiplexer, ILogger<WorkerRosterReader> logger) : IWorkerRosterReader
{
    public async Task<WorkerRosterSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        RedisValue value;
        try
        {
            value = await connectionMultiplexer.GetDatabase().StringGetAsync(WorkerHealthKeys.Roster);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // A missing key and an unreachable Redis mean the same thing to both consumers of this
            // reader: "no snapshot right now". /api/admin/roster and /api/admin/channels/{channelName}
            // already render that as snapshotAvailable/available = false — a Redis outage collapsing
            // into that same null is the already-designed degradation, not a new one.
            logger.LogWarning(ex, "Lesen des Worker-Roster-Snapshots fehlgeschlagen — behandle als fehlenden Key.");
            return null;
        }

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return JsonSerializer.Deserialize<WorkerRosterSnapshot>(value.ToString(), JsonSerializerOptions.Web);
    }
}

/// <summary>
/// Shared between the worker (writer) and this reader. Lives in Infrastructure because both projects
/// reference it, while they do not reference each other.
/// </summary>
public static class WorkerHealthKeys
{
    public const string TwitchConnection = "worker:health:twitch";

    /// <summary>The per-channel roster, on its own key — see <see cref="WorkerRosterSnapshot"/>.</summary>
    public const string Roster = "worker:roster";

    /// <summary>
    /// Three times the worker's 20-second publish interval, so a single missed write does not read as
    /// a dead worker.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Same three-ticks rule against the roster's slower 60-second cadence. Not stretched further to
    /// paper over gaps: a roster that outlives its worker by minutes is worse than one that is
    /// honestly missing, and staleness short of that is reported from GeneratedAtUtc anyway.
    /// </summary>
    public static readonly TimeSpan RosterTtl = TimeSpan.FromSeconds(180);
}
