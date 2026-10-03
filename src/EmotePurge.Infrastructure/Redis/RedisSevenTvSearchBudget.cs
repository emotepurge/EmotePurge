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
/// the tests can wind the clock instead of sleeping through a window. That holds while they share a
/// host; split across machines, their clock skew would shift the window and the blocks by as much.
/// </para>
/// <para>
/// <b>Two blocks, one per cause, each an absolute instant plus a TTL.</b> A key's value is the end of
/// the block in milliseconds, compared against the caller's clock; its TTL only cleans up. Each is
/// only ever extended, and they are kept apart so a charge can say which one holds it back — a
/// lockout 7TV imposed and a precaution of ours mean different things to a user, and a longer
/// low-watermark block must not relabel a running rate limit.
/// </para>
/// <para>
/// <b>Every round trip is bounded.</b> StackExchange.Redis takes no cancellation token, so each call
/// is awaited with a short timeout (default one second) and, for a charge, the caller's token. A slow
/// Redis then costs a caller one second and a refusal, never a hung leaderboard request.
/// </para>
/// </remarks>
public sealed class RedisSevenTvSearchBudget(
    IConnectionMultiplexer connectionMultiplexer,
    SevenTvSearchBudgetOptions options,
    TimeProvider timeProvider,
    ILogger<RedisSevenTvSearchBudget> logger,
    string keyPrefix = RedisSevenTvSearchBudget.DefaultKeyPrefix,
    TimeSpan? operationTimeout = null) : ISevenTvSearchBudget
{
    /// <summary>The key namespace in production; tests pass their own so they never share state.</summary>
    public const string DefaultKeyPrefix = "seventv:search-budget";

    // Result codes of ChargeScript, first element of the returned array.
    private const int GrantedCode = 0;
    private const int BlockedCode = 1;
    private const int WindowFullCode = 2;
    private const int ConsumerShareFullCode = 3;

    // Cause codes in the fourth element of a BlockedCode result.
    private const int RateLimitedCauseCode = 1;
    private const int LowWatermarkCauseCode = 2;

    // KEYS: [1] window of all consumers, [2] window of this consumer, [3] rate-limit block instant,
    // [4] low-watermark block instant.
    // ARGV: [1] now (ms), [2] window (ms), [3] total ceiling, [4] consumer share, [5] unique member.
    // Returns {code, usedInWindow, blockedForMs, causeCode}. The rate-limit block is checked first,
    // so it is the one reported while both are active.
    private const string ChargeScript = """
        local now = tonumber(ARGV[1])
        local rateLimitedUntil = tonumber(redis.call('GET', KEYS[3]) or '0')
        if rateLimitedUntil > now then
          return {1, 0, rateLimitedUntil - now, 1}
        end
        local watermarkUntil = tonumber(redis.call('GET', KEYS[4]) or '0')
        if watermarkUntil > now then
          return {1, 0, watermarkUntil - now, 2}
        end
        local floor = now - tonumber(ARGV[2])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', floor)
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', floor)
        local used = redis.call('ZCARD', KEYS[1])
        if used >= tonumber(ARGV[3]) then
          return {2, used, 0, 0}
        end
        if redis.call('ZCARD', KEYS[2]) >= tonumber(ARGV[4]) then
          return {3, used, 0, 0}
        end
        redis.call('ZADD', KEYS[1], now, ARGV[5])
        redis.call('ZADD', KEYS[2], now, ARGV[5])
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        redis.call('PEXPIRE', KEYS[2], ARGV[2])
        return {0, used + 1, 0, 0}
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

    /// <summary>The bound on one Redis round trip unless the caller passes its own.</summary>
    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The day window plus an hour, like the rate-limit telemetry's day buckets.</summary>
    private static readonly TimeSpan MinRemainingTtl = TimeSpan.FromHours(25);

    private readonly string _windowKey = keyPrefix + ":window";
    private readonly string _rateLimitBlockKey = keyPrefix + ":blocked-until:rate-limited";
    private readonly string _watermarkBlockKey = keyPrefix + ":blocked-until:low-watermark";
    private readonly TimeSpan _operationTimeout = operationTimeout ?? DefaultOperationTimeout;

    public async Task<SevenTvSearchPermit> TryChargeAsync(SevenTvSearchConsumer consumer, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var member = string.Create(CultureInfo.InvariantCulture, $"{now}:{Guid.NewGuid():N}");

        try
        {
            var result = (RedisResult[]?)await connectionMultiplexer.GetDatabase().ScriptEvaluateAsync(
                    ChargeScript,
                    [_windowKey, ConsumerWindowKey(consumer), _rateLimitBlockKey, _watermarkBlockKey],
                    [now, (long)options.Window.TotalMilliseconds, options.MaxRequestsPerWindow, options.ShareOf(consumer), member])
                .WaitAsync(_operationTimeout, cancellationToken);

            if (result is not { Length: 4 })
            {
                throw new RedisException("The search-budget charge script returned an unexpected shape.");
            }

            var used = (int)result[1];
            return (int)result[0] switch
            {
                GrantedCode => new SevenTvSearchPermit(SevenTvSearchRefusal.None, used),
                BlockedCode => new SevenTvSearchPermit(
                    SevenTvSearchRefusal.Blocked,
                    used,
                    TimeSpan.FromMilliseconds((long)result[2]),
                    ToCause((int)result[3])),
                WindowFullCode => new SevenTvSearchPermit(SevenTvSearchRefusal.WindowFull, used),
                ConsumerShareFullCode => new SevenTvSearchPermit(SevenTvSearchRefusal.ConsumerShareFull, used),
                var code => throw new RedisException($"The search-budget charge script returned unknown code {code}."),
            };
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Fail-closed, the opposite of the resync cooldown's choice: that one guards UX cost,
            // this one guards a bucket whose overdraft locks every consumer out for an hour. A slow
            // store lands here too (WaitAsync's TimeoutException); a cancelled caller does not — its
            // OperationCanceledException travels on, since nobody is waiting for the answer.
            logger.LogWarning(
                ex,
                "7TV search budget unreachable or too slow, refusing a {Consumer} search request (fail-closed).",
                consumer);
            return new SevenTvSearchPermit(SevenTvSearchRefusal.StoreUnavailable, 0);
        }
    }

    public async Task ObserveResponseAsync(SevenTvSearchObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var now = timeProvider.GetUtcNow();

        // The caller's token is deliberately not passed on: by the time this runs 7TV has already
        // answered, and dropping a lockout because the browser went away would let the next charge
        // walk into the same hour. Only the timeout bounds it.
        //
        // The block before the telemetry, each in its own try: a failing minimum write must never
        // cost the block that keeps every consumer out of a locked bucket.
        await TryWriteBlockAsync(observation, now);
        await TryLowerMinimumAsync(observation, now);
    }

    private async Task TryWriteBlockAsync(SevenTvSearchObservation observation, DateTimeOffset now)
    {
        if (SevenTvSearchBlockPolicy.BlockFor(observation, options) is not { } block)
        {
            return;
        }

        try
        {
            var until = now.Add(block.Duration).ToUnixTimeMilliseconds();
            var extended = (int)await connectionMultiplexer.GetDatabase().ScriptEvaluateAsync(
                    ExtendBlockScript,
                    [BlockKey(block.Cause)],
                    [until, (long)block.Duration.TotalMilliseconds])
                .WaitAsync(_operationTimeout);

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
            // Fail-open: a lost block lets nothing through as long as the store stays down, because
            // every charge against it is refused; that it is lost once the store is back is the
            // accepted cost of not failing the lookup that observed it.
            logger.LogWarning(
                ex,
                "7TV search budget unreachable or too slow, a {Cause} block of {BlockSeconds}s was dropped.",
                block.Cause,
                (long)block.Duration.TotalSeconds);
        }
    }

    private async Task TryLowerMinimumAsync(SevenTvSearchObservation observation, DateTimeOffset now)
    {
        if (observation.Remaining is not { } remaining)
        {
            return;
        }

        try
        {
            await connectionMultiplexer.GetDatabase().ScriptEvaluateAsync(
                    LowerMinimumScript,
                    [MinRemainingKey(now)],
                    [Math.Max(remaining, 0), (long)MinRemainingTtl.TotalSeconds])
                .WaitAsync(_operationTimeout);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "7TV search budget unreachable or too slow, a minimum-remaining sample was dropped.");
        }
    }

    private string ConsumerWindowKey(SevenTvSearchConsumer consumer) => consumer switch
    {
        SevenTvSearchConsumer.ChannelIdentity => _windowKey + ":channel-identity",
        SevenTvSearchConsumer.Leaderboard => _windowKey + ":leaderboard",
        _ => throw new ArgumentOutOfRangeException(nameof(consumer), consumer, "Unknown SevenTvSearchConsumer."),
    };

    private string BlockKey(SevenTvSearchBlockCause cause) => cause switch
    {
        SevenTvSearchBlockCause.RateLimited => _rateLimitBlockKey,
        SevenTvSearchBlockCause.LowWatermark => _watermarkBlockKey,
        _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unknown SevenTvSearchBlockCause."),
    };

    private string MinRemainingKey(DateTimeOffset now) =>
        string.Create(CultureInfo.InvariantCulture, $"{keyPrefix}:min-remaining:{now.UtcDateTime:yyyyMMddHH}");

    private static SevenTvSearchBlockCause ToCause(int code) => code switch
    {
        RateLimitedCauseCode => SevenTvSearchBlockCause.RateLimited,
        LowWatermarkCauseCode => SevenTvSearchBlockCause.LowWatermark,
        _ => throw new RedisException($"The search-budget charge script returned unknown block cause {code}."),
    };
}
