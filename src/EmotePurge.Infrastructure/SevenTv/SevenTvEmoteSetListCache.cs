using System.Text.Json;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// Redis-backed <see cref="ISevenTvEmoteSetListCache"/> (spec 2026-09-20, E12): key prefix
/// <c>7tvsets:</c>, key is the immutable Twitch id, value is the finished outcome as JSON —
/// including the negative ones, each with the shelf-life its caller passed in.
/// </summary>
/// <remarks>
/// <para>
/// Its own prefix rather than a second use of <c>7tvforeign:</c>: the two caches answer different
/// questions about the same channel, and one key space for both would let "the sets of A" overwrite
/// "the active set of A".
/// </para>
/// <para>
/// Fail-open in both directions, same shape as <see cref="ForeignEmoteSetCache"/>: a Redis outage
/// degrades this path to "always live", never to a 503 — the cache is a cost optimisation, not a
/// correctness boundary.
/// </para>
/// <para>
/// The stored payload is a flat record of this file's own, not the result type: the result type is
/// invariant-by-construction (no public constructor, payload only on <c>Ok</c>), which is exactly
/// what a deserializer cannot honour. Reading it back through the factories restores the invariant,
/// and a payload that does not fit them — a status the deserializer produced out of range, a
/// success with no list — is treated as a miss rather than handed on.
/// </para>
/// </remarks>
public class SevenTvEmoteSetListCache(IConnectionMultiplexer connectionMultiplexer, ILogger<SevenTvEmoteSetListCache> logger)
    : ISevenTvEmoteSetListCache
{
    private const string KeyPrefix = "7tvsets:";

    public async Task<EmoteSetListResult?> TryGetAsync(string twitchChannelId, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await connectionMultiplexer.GetDatabase().StringGetAsync(BuildKey(twitchChannelId));
            if (value.IsNullOrEmpty)
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize<CachedEmoteSetList>(value.ToString(), JsonSerializerOptions.Web);
            return payload is null ? null : Rehydrate(payload);

        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException or ArgumentOutOfRangeException)
        {
            // A payload we cannot read is treated exactly like a miss — the caller resolves live,
            // which is the safe direction for a read-only list.
            logger.LogWarning(
                ex, "Reading the 7TV emote-set list cache for Twitch id {TwitchId} failed — treated as a miss.", twitchChannelId);
            return null;
        }
    }

    public async Task SetAsync(
        string twitchChannelId, EmoteSetListResult result, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        try
        {
            var payload = JsonSerializer.Serialize(
                new CachedEmoteSetList(
                    result.Status,
                    result.List?.SevenTvActiveEmoteSetId,
                    result.List?.Sets.ToList()),
                JsonSerializerOptions.Web);
            await connectionMultiplexer.GetDatabase().StringSetAsync(BuildKey(twitchChannelId), payload, timeToLive);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(
                ex, "Writing the 7TV emote-set list cache for Twitch id {TwitchId} failed — the result is used for this request only.", twitchChannelId);
        }
    }

    // A success without a list cannot be rebuilt without inventing the one thing the caller asked
    // for, so it is a miss rather than an empty answer — the "never a silent empty list" rule of 6.1
    // holds on the way out of the cache too.
    private static EmoteSetListResult? Rehydrate(CachedEmoteSetList payload) => payload.Status switch
    {
        EmoteSetListStatus.Ok when payload.Sets is { } sets => EmoteSetListResult.Ok(
            new EmoteSetList(payload.ActiveEmoteSetId, sets)),
        EmoteSetListStatus.Ok => null,
        _ => EmoteSetListResult.Failed(payload.Status)
    };

    private static string BuildKey(string twitchChannelId) => $"{KeyPrefix}{twitchChannelId}";

    // Null Sets is what every negative outcome stores; Ok always carries a list, even an empty one.
    private sealed record CachedEmoteSetList(
        EmoteSetListStatus Status, string? ActiveEmoteSetId, List<EmoteSetSummary>? Sets);
}
