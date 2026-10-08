using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// Redis-backed <see cref="IForeignChannelIdentityCache"/> (spec 2026-09-20 K3 review, P3-1): key
/// prefix <c>7tvforeignidentity:</c>, key is the normalized channel login, value is the resolved
/// Twitch id as a plain string. Its own prefix rather than reusing <c>7tvsets:</c> or
/// <c>7tvforeign:</c> for the same reason those two keep theirs apart — this answers yet another
/// question about the same channel (a login-to-id resolution, not a set list or a preview), and one
/// key space for all three would let one overwrite another.
/// </summary>
public class ForeignChannelIdentityCache(IConnectionMultiplexer connectionMultiplexer, ILogger<ForeignChannelIdentityCache> logger)
    : IForeignChannelIdentityCache
{
    private const string KeyPrefix = "7tvforeignidentity:";

    /// <summary>
    /// Short on purpose: a login rarely changes identity within it, and this cache only exists to
    /// absorb a burst of repeat requests for the same channel (reopening the picker, a page refresh
    /// during a live-test), not to stand in for Helix over any meaningful stretch of time.
    /// </summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(2);

    public async Task<string?> TryGetTwitchUserIdAsync(string normalizedChannelName, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await connectionMultiplexer.GetDatabase().StringGetAsync(BuildKey(normalizedChannelName));
            return value.IsNullOrEmpty ? null : value.ToString();
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // A cache we cannot read is treated exactly like a miss — the caller resolves live,
            // which is the safe direction for a cost optimisation that is not a correctness boundary.
            logger.LogWarning(
                ex, "Reading the foreign-channel identity cache for {ChannelName} failed — treated as a miss.", normalizedChannelName);
            return null;
        }
    }

    public async Task SetTwitchUserIdAsync(string normalizedChannelName, string twitchUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(twitchUserId);

        try
        {
            await connectionMultiplexer.GetDatabase().StringSetAsync(BuildKey(normalizedChannelName), twitchUserId, TimeToLive);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(
                ex, "Writing the foreign-channel identity cache for {ChannelName} failed — the result is used for this request only.", normalizedChannelName);
        }
    }

    private static string BuildKey(string normalizedChannelName) => $"{KeyPrefix}{normalizedChannelName}";
}
