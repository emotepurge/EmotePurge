using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// Redis-backed <see cref="ISevenTvEditorGrantsHoldCache"/>: key prefix <c>7tveditorhold:</c>, key is
/// the immutable Twitch user id, value is the held <see cref="SevenTvLookupStatus"/> by name.
/// </summary>
/// <remarks>
/// <para>
/// Its own prefix, never a second shape under <c>7tveditor:</c>: that key holds the grants every
/// reader shares — the authorization path included — and a held failure written there would reach
/// a reader that fails closed on it.
/// </para>
/// <para>
/// Fail-open in both directions, like <see cref="SevenTvEmoteSetListCache"/>: a Redis outage turns
/// "held" into "try again", and the breaker and the budget still stand between that attempt and 7TV.
/// Only the two statuses the guarded service ever holds are read back; anything else is a miss.
/// </para>
/// </remarks>
public class SevenTvEditorGrantsHoldCache(IConnectionMultiplexer connectionMultiplexer, ILogger<SevenTvEditorGrantsHoldCache> logger)
    : ISevenTvEditorGrantsHoldCache
{
    private const string KeyPrefix = "7tveditorhold:";

    public async Task<SevenTvLookupStatus?> TryGetAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        RedisValue value;
        try
        {
            value = await connectionMultiplexer.GetDatabase().StringGetAsync(BuildKey(twitchUserId));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(
                ex, "Reading the held 7TV editor-grant failure for {UserId} failed — treated as nothing held.", twitchUserId);
            return null;
        }

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return Enum.TryParse<SevenTvLookupStatus>(value.ToString(), ignoreCase: false, out var status)
            && status is SevenTvLookupStatus.Unavailable or SevenTvLookupStatus.NoSevenTvAccount
                ? status
                : null;
    }

    public async Task SetAsync(
        string twitchUserId, SevenTvLookupStatus status, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        try
        {
            await connectionMultiplexer.GetDatabase().StringSetAsync(BuildKey(twitchUserId), status.ToString(), timeToLive);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(
                ex, "Holding the 7TV editor-grant failure for {UserId} failed — the next report tries again.", twitchUserId);
        }
    }

    private static string BuildKey(string twitchUserId) => $"{KeyPrefix}{twitchUserId}";
}
