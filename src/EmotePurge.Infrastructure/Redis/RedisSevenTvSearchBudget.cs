using System.Globalization;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.SevenTv;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

/// <summary>
/// Redis-backed <see cref="ISevenTvSearchBudget"/>: one rolling window shared by the Api and the
/// Worker, a block that 7TV's own answers can set, and the minimum-remaining telemetry (design note
/// <c>docs/Konzept-7TV-Such-Budget-2026-10-03.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The window is a timestamp log, not a counter.</b> Two sorted sets — one for all consumers, one
/// per consumer — hold one member per granted permit, scored by its time in milliseconds. A charge
/// trims what has aged out, checks the block, the total and the consumer's share, and records the
/// permit, all in one Lua script: Redis runs scripts atomically, so an Api and a Worker charging in
/// the same millisecond can never both take the last slot. Rolling for the reason the two
/// in-process budgets give — a fixed window lets twice the ceiling through across its boundary.
/// Memory stays bounded: a set never holds more members than the ceiling, and every key carries a
/// TTL of one window, re-set on each grant.
/// </para>
/// <para>
/// <b>The clock is the caller's.</b> "Now" travels into the scripts as an argument from the
/// <see cref="TimeProvider"/>, not from Redis' <c>TIME</c>: Api and Worker run on the same host, and
/// the tests can wind the clock instead of sleeping through a window.
/// </para>
/// <para>
/// <b>The block is an absolute instant plus a TTL.</b> The key's value is the end of the block in
/// milliseconds, compared against the caller's clock; its TTL only cleans up. It is only ever
/// extended: a short block from a low-watermark sample must not cut a running one-hour lockout short.
/// </para>
/// </remarks>
public sealed class RedisSevenTvSearchBudget(
    IConnectionMultiplexer connectionMultiplexer,
    SevenTvSearchBudgetOptions options,
    TimeProvider timeProvider,
    ILogger<RedisSevenTvSearchBudget> logger,
    string keyPrefix = RedisSevenTvSearchBudget.DefaultKeyPrefix) : ISevenTvSearchBudget
{
    /// <summary>The key namespace in production; tests pass their own so they never share state.</summary>
    public const string DefaultKeyPrefix = "seventv:search-budget";

    // Result codes of ChargeScript, first element of the returned array.
    private const int GrantedCode = 0;
    private const int BlockedCode = 1;
    private const int WindowFullCode = 2;
    private const int ConsumerShareFullCode = 3;

    // KEYS: [1] window of all consumers, [2] window of this consumer, [3] block instant.
    // ARGV: [1] now (ms), [2] window (ms), [3] total ceiling, [4] consumer share, [5] unique member.
    // Returns {code, usedInWindow, blockedForMs}.
    private const string ChargeScript = """
        local now = tonumber(ARGV[1])
        local blockedUntil = tonumber(redis.call('GET', KEYS[3]) or '0')
        if blockedUntil > now then
          return {1, 0, blockedUntil - now}
        end
        local floor = now - tonumber(ARGV[2])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', floor)
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', floor)
        local used = redis.call('ZCARD', KEYS[1])
        if used >= tonumber(ARGV[3]) then
          return {2, used, 0}
        end
        if redis.call('ZCARD', KEYS[2]) >= tonumber(ARGV[4]) then
          return {3, used, 0}
        end
        redis.call('ZADD', KEYS[1], now, ARGV[5])
        redis.call('ZADD', KEYS[2], now, ARGV[5])
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        redis.call('PEXPIRE', KEYS[2], ARGV[2])
        return {0, used + 1, 0}
        """;

    // KEYS: [1] block instant. ARGV: [1] new end (ms), [2] TTL (ms). Returns 1 if the block grew.
    private const string ExtendBlockScript = """
        local current = tonumber(redis.call('GET', KEYS[1]) or '0')
        if tonumber(ARGV[1]) <= current then
          return 0
        end
        redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
        return 1
        """;

    // KEYS: [1] the hour's minimum. ARGV: [1] observed remaining, [2] TTL (s). Returns 1 if lowered.
    private const string LowerMinimumScript = """
        local current = redis.call('GET', KEYS[1])
        if current and tonumber(current) <= tonumber(ARGV[1]) then
          return 0
        end
        redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[2])
        return 1
        """;

    /// <summary>The day window plus an hour, like the rate-limit telemetry's day buckets.</summary>
    private static readonly TimeSpan MinRemainingTtl = TimeSpan.FromHours(25);

    private readonly string _windowKey = keyPrefix + ":window";
    private readonly string _blockKey = keyPrefix + ":blocked-until";

    public async Task<SevenTvSearchPermit> TryChargeAsync(SevenTvSearchConsumer consumer, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var member = string.Create(CultureInfo.InvariantCulture, $"{now}:{Guid.NewGuid():N}");

        try
        {
            var result = (RedisResult[]?)await connectionMultiplexer.GetDatabase().ScriptEvaluateAsync(
                ChargeScript,
                [_windowKey, ConsumerWindowKey(consumer), _blockKey],
                [now, (long)options.Window.TotalMilliseconds, options.MaxRequestsPerWindow, options.ShareOf(consumer), member]);

            if (result is not { Length: 3 })
            {
                throw new RedisException("The search-budget charge script returned an unexpected shape.");
            }

            var used = (int)result[1];
            return (int)result[0] switch
            {
                GrantedCode => new SevenTvSearchPermit(SevenTvSearchRefusal.None, used),
                BlockedCode => new SevenTvSearchPermit(
                    SevenTvSearchRefusal.Blocked, used, TimeSpan.FromMilliseconds((long)result[2])),
                WindowFullCode => new SevenTvSearchPermit(SevenTvSearchRefusal.WindowFull, used),
                ConsumerShareFullCode => new SevenTvSearchPermit(SevenTvSearchRefusal.ConsumerShareFull, used),
                var code => throw new RedisException($"The search-budget charge script returned unknown code {code}."),
            };
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Fail-closed, the opposite of the resync cooldown's choice: that one guards UX cost,
            // this one guards a bucket whose overdraft locks every consumer out for an hour.
            logger.LogWarning(
                ex,
                "7TV search budget unreachable, refusing a {Consumer} search request (fail-closed).",
                consumer);
            return new SevenTvSearchPermit(SevenTvSearchRefusal.StoreUnavailable, 0);
        }
    }

    public async Task ObserveResponseAsync(SevenTvSearchObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var now = timeProvider.GetUtcNow();

        try
        {
            var db = connectionMultiplexer.GetDatabase();

            if (observation.Remaining is { } remaining)
            {
                await db.ScriptEvaluateAsync(
                    LowerMinimumScript,
                    [MinRemainingKey(now)],
                    [Math.Max(remaining, 0), (long)MinRemainingTtl.TotalSeconds]);
            }

            if (SevenTvSearchBlockPolicy.BlockFor(observation, options) is not { } block)
            {
                return;
            }

            var until = now.Add(block.Duration).ToUnixTimeMilliseconds();
            var extended = (int)await db.ScriptEvaluateAsync(
                ExtendBlockScript,
                [_blockKey],
                [until, (long)block.Duration.TotalMilliseconds]);

            if (extended == 1)
            {
                // Once per extension, not per refused request: the refusals that follow say nothing
                // new, and this line is the one an operator needs to explain an hour without searches.
                logger.LogWarning(
                    "7TV search bucket blocked for {BlockSeconds}s across Api and Worker ({Cause}, remaining {Remaining}, reset {ResetSeconds}s).",
                    (long)block.Duration.TotalSeconds,
                    block.Cause,
                    observation.Remaining,
                    observation.ResetSeconds);
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Fail-open: a lost observation lets nothing through, because every charge against an
            // unreachable store is refused anyway.
            logger.LogWarning(ex, "7TV search budget unreachable, an observation of the search bucket was dropped.");
        }
    }

    private string ConsumerWindowKey(SevenTvSearchConsumer consumer) => consumer switch
    {
        SevenTvSearchConsumer.ChannelIdentity => _windowKey + ":channel-identity",
        SevenTvSearchConsumer.Leaderboard => _windowKey + ":leaderboard",
        _ => throw new ArgumentOutOfRangeException(nameof(consumer), consumer, "Unknown SevenTvSearchConsumer."),
    };

    private string MinRemainingKey(DateTimeOffset now) =>
        string.Create(CultureInfo.InvariantCulture, $"{keyPrefix}:min-remaining:{now.UtcDateTime:yyyyMMddHH}");
}
