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

    // The set-ID read mode's own namespace (spec 2026-09-20, E12): nested under the same prefix as
    // the login-keyed entries above but never colliding with one — a channel login can never contain
    // a colon, so "set:{id}" and any normalized login are disjoint strings by construction. An entry
    // here for channel A's currently-inactive set must never be overwritten by, or overwrite, the
    // "{login}"-keyed entry for A's active set.
    private const string SetIdKeyPrefix = "7tvforeign:set:";

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public Task<ForeignEmoteSet?> TryGetAsync(string normalizedChannelName, CancellationToken cancellationToken = default) =>
        TryGetByKeyAsync(BuildLoginKey(normalizedChannelName), normalizedChannelName);

    public Task SetAsync(string normalizedChannelName, ForeignEmoteSet emoteSet, CancellationToken cancellationToken = default) =>
        SetByKeyAsync(BuildLoginKey(normalizedChannelName), normalizedChannelName, emoteSet);

    public Task<ForeignEmoteSet?> TryGetBySetIdAsync(string emoteSetId, CancellationToken cancellationToken = default) =>
        TryGetByKeyAsync(BuildSetIdKey(emoteSetId), emoteSetId);

    public Task SetBySetIdAsync(string emoteSetId, ForeignEmoteSet emoteSet, CancellationToken cancellationToken = default) =>
        SetByKeyAsync(BuildSetIdKey(emoteSetId), emoteSetId, emoteSet);

    private async Task<ForeignEmoteSet?> TryGetByKeyAsync(string key, string logIdentifier)
    {
        try
        {
            var value = await connectionMultiplexer.GetDatabase().StringGetAsync(key);
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
                ex, "Lesen des Fremdkanal-Vorschau-Caches für {Identifier} fehlgeschlagen — behandle als Miss.", logIdentifier);
            return null;
        }
    }

    private async Task SetByKeyAsync(string key, string logIdentifier, ForeignEmoteSet emoteSet)
    {
        try
        {
            var payload = JsonSerializer.Serialize(emoteSet, JsonSerializerOptions.Web);
            await connectionMultiplexer.GetDatabase().StringSetAsync(key, payload, Ttl);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(
                ex, "Schreiben des Fremdkanal-Vorschau-Caches für {Identifier} fehlgeschlagen — Ergebnis wird nur für diesen Request verwendet.", logIdentifier);
        }
    }

    private static string BuildLoginKey(string normalizedChannelName) => $"{KeyPrefix}{normalizedChannelName}";

    private static string BuildSetIdKey(string emoteSetId) => $"{SetIdKeyPrefix}{emoteSetId}";
}
