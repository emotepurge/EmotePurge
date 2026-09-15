using System.Collections.Concurrent;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Services;

// Caches the full moderated-channel list per Twitch user in Redis, sharing the existing role-cache
// TTL (Auth:ModCheckCacheTtlMinutes). The cache is what removes the Helix cost from the request
// path; the live pagination stays the truth, so every failure mode simply degrades to a miss.
//
// Only a complete, successful pagination is written. A timeout, a 429, a 5xx, a token failure or a
// pagination that ran out of pages leaves the key absent, so the next request retries live instead
// of serving a wrong "moderates nothing" for the whole TTL.
public class ModeratedChannelsProvider(
    ITwitchUserTokenService userTokenService,
    ITwitchHelixClient helixClient,
    IConnectionMultiplexer connectionMultiplexer,
    IConfiguration configuration,
    IRateLimitTelemetry telemetry,
    ILogger<ModeratedChannelsProvider> logger) : IModeratedChannelsProvider
{
    // Process-wide on purpose although the service itself is scoped: the gate has to span the
    // concurrent requests of one user, which live in different DI scopes. Same reasoning and same
    // known limit as TwitchTokenRefreshGate — with more than one Api replica this would need a
    // distributed lock; each replica would then pay one pagination per TTL.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public async Task<ModeratedChannelsLookup> GetModeratedChannelsAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default)
    {
        // A cache hit deliberately checks no token: a broken refresh token only surfaces on the
        // next miss (at most one TTL away) or through the hourly live validation of used tokens.
        // Accepted staleness — the alternative would put a token round trip back into every read.
        var cached = await TryReadCacheAsync(principal.TwitchUserId);
        if (cached is not null)
        {
            return new ModeratedChannelsLookup(cached, ReauthRequired: false);
        }

        using var lease = await AcquireGateAsync(principal.TwitchUserId, cancellationToken);

        // Double-check: whoever held the gate before us has just written the entry, and paginating
        // again would spend exactly the Helix calls this gate exists to avoid.
        cached = await TryReadCacheAsync(principal.TwitchUserId);
        if (cached is not null)
        {
            return new ModeratedChannelsLookup(cached, ReauthRequired: false);
        }

        var token = await userTokenService.GetValidAccessTokenAsync(principal, cancellationToken);
        if (token.AccessToken is null)
        {
            logger.LogInformation(
                "Moderierte Channels für {UserId} nicht ermittelbar: kein gültiger Access Token (Reauth nötig: {ReauthRequired}).",
                principal.TwitchUserId, token.ReauthRequired);
            return new ModeratedChannelsLookup(null, token.ReauthRequired);
        }

        var channels = await helixClient.GetModeratedChannelsAsync(token.AccessToken, principal.TwitchUserId, cancellationToken);
        if (channels is null)
        {
            // Transient, or an incomplete pagination the Helix client already reported as a
            // failure — not cached, so the next request tries live again.
            return new ModeratedChannelsLookup(null, token.ReauthRequired);
        }

        var normalized = channels
            .Select(channel => new TwitchModeratedChannelInfo(ChannelName.Normalize(channel.Login), channel.BroadcasterId))
            .ToList();

        await WriteCacheAsync(principal.TwitchUserId, normalized);
        return new ModeratedChannelsLookup(normalized, token.ReauthRequired);
    }

    /// <summary>
    /// One cache lookup, counted. Wrapped around the read rather than repeated at its two call sites,
    /// so the double-check behind the gate is counted too: it is a real lookup, and one that hits
    /// there is one Helix pagination the gate just saved.
    /// </summary>
    private async Task<IReadOnlyList<TwitchModeratedChannelInfo>?> TryReadCacheAsync(string twitchUserId)
    {
        var cached = await ReadCacheAsync(twitchUserId);
        telemetry.RecordCacheLookup(RateLimitCacheNames.ModeratedChannels, cached is not null);
        return cached;
    }

    private async Task<IReadOnlyList<TwitchModeratedChannelInfo>?> ReadCacheAsync(string twitchUserId)
    {
        RedisValue value;
        try
        {
            value = await connectionMultiplexer.GetDatabase().StringGetAsync(BuildKey(twitchUserId));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Lesen des Moderated-Channels-Caches für {UserId} fehlgeschlagen — behandle als Miss.", twitchUserId);
            return null;
        }

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            // An empty JSON array is a valid, cached answer ("moderates nothing"); only a missing
            // key or an unreadable payload counts as a miss.
            var stored = JsonSerializer.Deserialize<List<StoredModeratedChannel>>(value.ToString(), JsonSerializerOptions.Web);
            return stored?.Select(entry => new TwitchModeratedChannelInfo(entry.Login, entry.BroadcasterId)).ToList();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Moderated-Channels-Cache für {UserId} ist unlesbar — behandle als Miss.", twitchUserId);
            return null;
        }
    }

    private async Task WriteCacheAsync(string twitchUserId, IReadOnlyList<TwitchModeratedChannelInfo> channels)
    {
        var payload = JsonSerializer.Serialize(
            channels.Select(channel => new StoredModeratedChannel(channel.Login, channel.BroadcasterId)).ToList(),
            JsonSerializerOptions.Web);

        try
        {
            await connectionMultiplexer.GetDatabase().StringSetAsync(BuildKey(twitchUserId), payload, CacheTtl());
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // A lost write only costs the next request another pagination; the answer we just
            // fetched live stays valid for this one.
            logger.LogWarning(ex, "Schreiben des Moderated-Channels-Caches für {UserId} fehlgeschlagen — Ergebnis wird nur für diesen Request verwendet.", twitchUserId);
        }
    }

    private static async Task<IDisposable> AcquireGateAsync(string twitchUserId, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(twitchUserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    private TimeSpan CacheTtl() => TimeSpan.FromMinutes(configuration.GetValue<int?>("Auth:ModCheckCacheTtlMinutes") ?? 10);

    private static string BuildKey(string twitchUserId) => $"modlist:{twitchUserId}";

    private sealed record StoredModeratedChannel(string Login, string BroadcasterId);

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
