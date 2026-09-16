using System.Text.Json;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// Redis-backed <see cref="IForeignEmoteSetCache"/> (spec 2026-09-09, E3): key prefix
/// <c>7tvforeign:</c>, key is the normalized source login (not the 7TV set id — a login is what the
/// caller has in hand, a set id is only known after the resolution chain already ran, which is
/// exactly the round trip this cache exists to skip), TTL 60 s, value is the fully assembled
/// <see cref="ForeignEmoteSet"/> as JSON — never 7TV's raw response, so a cache hit needs no
/// re-parsing at all.
/// </summary>
/// <remarks>
/// Fail-open in both directions, same shape as <c>ModRoleCache</c>/<c>ChannelResyncCooldown</c>: a
/// Redis outage degrades this feature to "always live", not to a 503 — the cache is a cost
/// optimization, not a correctness boundary.
/// </remarks>
public class ForeignEmoteSetCache(IConnectionMultiplexer connectionMultiplexer, ILogger<ForeignEmoteSetCache> logger)
    : IForeignEmoteSetCache
{
    private const string KeyPrefix = "7tvforeign:";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public async Task<ForeignEmoteSet?> TryGetAsync(string normalizedChannelName, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await connectionMultiplexer.GetDatabase().StringGetAsync(BuildKey(normalizedChannelName));
            if (value.IsNullOrEmpty)
            {
                return null;
            }

            return JsonSerializer.Deserialize<ForeignEmoteSet>(value.ToString(), JsonSerializerOptions.Web);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            // A payload we cannot read is treated the same as a miss — the caller resolves live,
            // which is the safe direction for a read-only preview.
            logger.LogWarning(
                ex, "Lesen des Fremdkanal-Vorschau-Caches für {ChannelName} fehlgeschlagen — behandle als Miss.", normalizedChannelName);
            return null;
        }
    }

    public async Task SetAsync(string normalizedChannelName, ForeignEmoteSet emoteSet, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(emoteSet, JsonSerializerOptions.Web);
            await connectionMultiplexer.GetDatabase().StringSetAsync(BuildKey(normalizedChannelName), payload, Ttl);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(
                ex, "Schreiben des Fremdkanal-Vorschau-Caches für {ChannelName} fehlgeschlagen — Ergebnis wird nur für diesen Request verwendet.", normalizedChannelName);
        }
    }

    private static string BuildKey(string normalizedChannelName) => $"{KeyPrefix}{normalizedChannelName}";
}
