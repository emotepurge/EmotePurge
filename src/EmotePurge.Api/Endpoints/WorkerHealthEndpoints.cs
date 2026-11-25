using EmotePurge.Api.Health;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

public static class WorkerHealthEndpoints
{
    public static void MapWorkerHealthEndpoints(this WebApplication app)
    {
        // Deliberately policy-free: this is the app's own badge poll, every open page hitting it every
        // 30 seconds, so a budget here would reject the app's own baseline traffic. One Redis read.
        app.MapGet("/api/worker/health", async (IWorkerHealthReader healthReader, CancellationToken ct) =>
        {
            // The worker publishes the snapshot to Redis with a TTL (see WorkerHealthPublisher), so Api
            // and Worker never talk directly. If the worker is gone or its publisher is wedged, the key
            // simply expires — and that absence is itself the signal. Reading it goes through
            // IWorkerHealthReader rather than IConnectionMultiplexer directly: this was the only place
            // in the Api that reached past its own service layer into infrastructure, and it meant the
            // wire format was declared once here and once in the worker.
            var snapshot = await healthReader.ReadAsync(ct);
            if (snapshot is null)
            {
                return Results.Ok(new { status = "unknown", reasonCode = ApiErrorCodes.NoHealthData });
            }

            // Status derivation and its thresholds live in WorkerHealthStatus, shared with the
            // admin endpoint. This response deliberately stays minimal and unauthenticated: it
            // feeds the header badge every visitor polls, so it exposes no operational detail
            // (subscription counts, flush failures) — those are admin-only (Z1 split).
            var derived = WorkerHealthStatus.Derive(snapshot, DateTime.UtcNow);

            return Results.Ok(new
            {
                status = derived.Status,
                snapshot.IsConnected,
                snapshot.LastMessageReceivedUtc,
                secondsSinceLastMessage = derived.SecondsSinceLastMessage,
                sevenTv = new
                {
                    status = derived.SevenTvStatus,
                    snapshot.SevenTvLastDispatchUtc,
                    secondsSinceLastFrame = derived.SevenTvSecondsSinceLastFrame,
                },
            });
        });

        // The machine-facing twin of the badge endpoint above (review Z1 rest / S3-35): no payload,
        // the status code is the whole answer — 200 only while the worker's Twitch pipeline is
        // "connected", 503 for disconnected/stale/missing snapshot. That makes `curl -f` in the
        // container HEALTHCHECK and the external uptime monitor's pull check double as the
        // dead-man's switch: a worker that stops publishing expires the Redis key and this flips
        // to 503 without anyone pushing anything. The 7TV status deliberately does not factor in —
        // the periodic REST resync keeps emote data correct without the event socket, so a 7TV
        // hiccup is degraded, not down.
        app.MapGet("/api/health", async (IWorkerHealthReader healthReader, CancellationToken ct) =>
        {
            var snapshot = await healthReader.ReadAsync(ct);
            var isHealthy = snapshot is not null
                && WorkerHealthStatus.Derive(snapshot, DateTime.UtcNow).Status == "connected";

            return Results.StatusCode(isHealthy
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable);
        }).RequireRateLimiting(RateLimitPolicyNames.PublicHealth);

        // The channel-level twin (#516): 200 while every channel the worker is meant to be in is
        // confirmed or still within its grace, 503 when one is overdue or the verdict cannot be
        // reached. Payload-free like /api/health, with a counter of its own. The verdict (5 s cache,
        // grace, exclusions) lives in IChannelJoinHealthService; the handler only maps it. Never a
        // container HEALTHCHECK target: a channel-level fault must not restart a healthy container.
        app.MapGet("/api/health/channels", async (IChannelJoinHealthService joinHealth, CancellationToken ct) =>
        {
            var alarming = await joinHealth.IsAlarmingAsync(ct);
            return Results.StatusCode(alarming
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK);
        }).RequireRateLimiting(RateLimitPolicyNames.PublicChannelHealth);
    }
}
