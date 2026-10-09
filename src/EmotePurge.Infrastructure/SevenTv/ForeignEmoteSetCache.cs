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
    // v2 prefixes since the payload gained ForeignEmoteRow.AddedAt (#346, D43): an entry written by
    // the previous code can never be read, whatever its remaining TTL. Belt and braces with the
    // payload's SchemaVersion check in TryGetByKeyAsync.
    private const string KeyPrefix = "7tvforeign:v2:login:";

    /// <summary>
    /// The payload shape this code writes and requires when reading (#346, D43). Version 2 introduced
    /// <see cref="ForeignEmoteRow.AddedAt"/>; a cached payload below it is a miss and is refetched, so
    /// a row's <c>AddedAt == null</c> on a current payload always means "7TV reported none", never "the
    /// field did not exist yet".
    /// </summary>
    internal const int CurrentSchemaVersion = 2;

    // The set-ID read mode's own namespace (spec 2026-09-20, E12): nested under the same prefix as
    // the login-keyed entries above but never colliding with one — a channel login can never contain
    // a colon, so "set:{id}" and any normalized login are disjoint strings by construction. An entry
    // here for channel A's currently-inactive set must never be overwritten by, or overwrite, the
    // "{login}"-keyed entry for A's active set.
    private const string SetIdKeyPrefix = "7tvforeign:v2:set:";

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

            // The version lives in an envelope around the set, not on the set: the set is also the
            // public HTTP answer, and a cache internal must not leak into it. A payload written before
            // the envelope existed has no SchemaVersion (reads as 0) and no EmoteSet, so it is a miss
            // like any older version.
            var envelope = JsonSerializer.Deserialize<CachedForeignEmoteSet>(value.ToString(), JsonSerializerOptions.Web);
            if (envelope is null || envelope.SchemaVersion < CurrentSchemaVersion || envelope.EmoteSet is null)
            {
                logger.LogDebug("Foreign-channel preview cache entry for {Identifier} predates schema version {Version}; treating it as a miss.", logIdentifier, CurrentSchemaVersion);
                return null;
            }

            return envelope.EmoteSet;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            // A payload we cannot read is treated the same as a miss — the caller resolves live,
            // which is the safe direction for a read-only preview.
            logger.LogWarning(
                ex, "Reading the foreign-channel preview cache for {Identifier} failed; treating it as a miss.", logIdentifier);
            return null;
        }
    }

    private async Task SetByKeyAsync(string key, string logIdentifier, ForeignEmoteSet emoteSet)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new CachedForeignEmoteSet(CurrentSchemaVersion, emoteSet), JsonSerializerOptions.Web);
            await connectionMultiplexer.GetDatabase().StringSetAsync(key, payload, Ttl);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(
                ex, "Writing the foreign-channel preview cache for {Identifier} failed; the result is used for this request only.", logIdentifier);
        }
    }

    private static string BuildLoginKey(string normalizedChannelName) => $"{KeyPrefix}{normalizedChannelName}";

    private static string BuildSetIdKey(string emoteSetId) => $"{SetIdKeyPrefix}{emoteSetId}";

    // What is stored: the set plus the shape version it was written with. Nested at the class end.
    private sealed record CachedForeignEmoteSet(int SchemaVersion, ForeignEmoteSet? EmoteSet);
}
