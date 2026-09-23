using System.Text.Json;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

/// <summary>
/// Writer and reader of the rate-limit counters in one class, like <see cref="TwitchLiveStatusStore"/>:
/// both sides are the same handful of Redis hashes, and splitting them would declare the bucket layout
/// twice with nothing tying the two copies together.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two bucket tiers, one set of events.</b> Every event is counted twice, into a five-second bucket
/// and into a one-minute bucket. The minute window sums the twelve most recent five-second buckets, the
/// 24-hour window the 1440 most recent minute buckets. One tier alone cannot serve both: minute buckets
/// make "last minute" jump by a whole minute's traffic whenever the clock crosses a boundary, and
/// five-second buckets would need 17280 reads for a day. Two tiers cost one extra increment per event
/// and keep both windows honest — the minute window moves in five-second steps and therefore covers
/// between 55 and 60 seconds, never more, and never in one jump.
/// </para>
/// <para>
/// <b>Both windows are summed from the same events</b>, so the minute count is by construction contained
/// in the 24-hour count; the two can never contradict each other.
/// </para>
/// <para>
/// <b>Every key carries a TTL.</b> Nothing else ever deletes them (the one exception: an account deletion
/// forgetting the last-rejection slot, <see cref="ForgetPartitionAsync"/>) — there is no cleanup job, and
/// the key space is time-indexed, so a bucket without an expiry would stay in Redis forever. The day
/// buckets outlive their own window by an hour so a read at the very edge is never short of data.
/// </para>
/// <para>
/// <b>Fail-open.</b> Every write swallows its exception after a structured German log line, and the read
/// answers <see cref="RateLimitTelemetrySnapshot.Unavailable"/>. Telemetry sits in the product path;
/// counting a rate limit must never be able to cause one.
/// </para>
/// </remarks>
public class RateLimitTelemetryStore(
    IConnectionMultiplexer connectionMultiplexer,
    TimeProvider timeProvider,
    ILogger<RateLimitTelemetryStore> logger) : IRateLimitTelemetry, IRateLimitTelemetryReader
{
    private const string KeyPrefix = "ratelimit:telemetry";

    /// <summary>Separator inside hash field names and composite keys; stripped from every name.</summary>
    private const char DimensionSeparator = '|';

    /// <summary>Five-second buckets, read by the minute window.</summary>
    private const string FineBucketPrefix = KeyPrefix + ":s:";

    /// <summary>One-minute buckets, read by the 24-hour window.</summary>
    private const string CoarseBucketPrefix = KeyPrefix + ":m:";

    private const string LastRejectionKey = KeyPrefix + ":last-rejection";

    private const string CompareAndDeleteScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) else return 0 end";

    // The three field-name prefixes. Spelled out rather than composed from DimensionSeparator so they
    // stay compile-time constants — they end in that same separator.
    private const string PolicyFieldPrefix = "policy|";

    private const string CacheFieldPrefix = "cache|";

    private const string ProviderFieldPrefix = "provider|";

    private const int FineBucketSeconds = 5;

    private const int FineBucketsPerWindow = 12;

    private const int CoarseBucketSeconds = 60;

    private const int CoarseBucketsPerWindow = 1440;

    private const int MaxNameLength = 64;

    /// <summary>Well past the twelve buckets the minute window reads, and short enough to stay tiny.</summary>
    private static readonly TimeSpan FineBucketTtl = TimeSpan.FromMinutes(5);

    /// <summary>One hour beyond the 24-hour window, so its oldest bucket is never gone before it falls out.</summary>
    private static readonly TimeSpan CoarseBucketTtl = TimeSpan.FromHours(25);

    /// <summary>Same retention as the day window: an incident nobody can see a counter for is noise.</summary>
    private static readonly TimeSpan IncidentTtl = TimeSpan.FromHours(25);

    public async Task RecordPolicyDecisionAsync(RateLimitPolicyDecision decision, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        try
        {
            var db = connectionMultiplexer.GetDatabase();
            var field = PolicyField(decision.PolicyName, decision.Accepted ? "accepted" : "rejected");
            var writes = new List<Task>(BucketWrites(db, now, field));

            if (!decision.Accepted)
            {
                var rejection = new RateLimitLastRejection(
                    now.UtcDateTime,
                    decision.HttpMethod,
                    decision.RouteTemplate,
                    decision.PolicyName,
                    decision.Partition,
                    decision.RetryAfterSeconds);
                writes.Add(db.StringSetAsync(LastRejectionKey, Serialize(rejection), IncidentTtl));
            }

            await Task.WhenAll(writes);
        }
        catch (Exception ex)
        {
            LogWriteFailure(ex, "Policy-Entscheidung", decision.PolicyName);
        }
    }

    public async Task RecordProviderResponseAsync(ProviderResponseObservation observation, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        try
        {
            var db = connectionMultiplexer.GetDatabase();
            var dimension = ProviderDimension(observation.ProviderName, observation.CallSource);
            var writes = new List<Task>(BucketWrites(db, now, ProviderField(dimension, "requests")));

            // Only a real 429 counts. A 500 is a provider failure, not a budget signal, and lumping the
            // two together would make the one number an operator acts on unreadable.
            if (observation.StatusCode == 429)
            {
                writes.AddRange(BucketWrites(db, now, ProviderField(dimension, "rate-limited")));
                var incident = new ProviderRateLimitIncident(now.UtcDateTime, observation.RetryAfterSeconds);
                writes.Add(db.StringSetAsync(ProviderIncidentKey(dimension), Serialize(incident), IncidentTtl));
            }

            if (observation.RateLimitLimit is not null
                || observation.RateLimitRemaining is not null
                || observation.RateLimitReset is not null)
            {
                var sample = new ProviderRateLimitHeaderSample(
                    now.UtcDateTime,
                    observation.RateLimitLimit,
                    observation.RateLimitRemaining,
                    observation.RateLimitReset);
                writes.Add(db.StringSetAsync(ProviderHeaderKey(dimension), Serialize(sample), IncidentTtl));
            }

            await Task.WhenAll(writes);
        }
        catch (Exception ex)
        {
            LogWriteFailure(ex, "Provider-Antwort", $"{observation.ProviderName}/{observation.CallSource}");
        }
    }

    public async Task RecordCacheLookupAsync(string cacheName, bool hit, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        try
        {
            var db = connectionMultiplexer.GetDatabase();
            await Task.WhenAll(BucketWrites(db, now, CacheField(cacheName, hit ? "hit" : "miss")));
        }
        catch (Exception ex)
        {
            LogWriteFailure(ex, "Cache-Zugriff", cacheName);
        }
    }

    public async Task<bool> ForgetPartitionAsync(string partition, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = connectionMultiplexer.GetDatabase();
            var stored = await db.StringGetAsync(LastRejectionKey);
            var rejection = Deserialize<RateLimitLastRejection>(stored);
            if (rejection is null || !BelongsToPartition(rejection.Partition, partition))
            {
                return true;
            }

            // Compare-and-delete: the slot is overwritten by the next rejected request of *any*
            // caller, so a plain delete after the read could remove a stranger's rejection that landed
            // in between. The script deletes only the exact value just read, atomically on the server;
            // if it changed, the new value is someone else's and the deleted user's is already gone.
            // A script rather than DELEX/ValueCondition, which needs Redis 8.4 — production runs 7.2.
            await db.ScriptEvaluateAsync(CompareAndDeleteScript, [LastRejectionKey], [stored]);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rate-limit telemetry: the last-rejection slot could not be checked or cleared for a deleted account.");
            return false;
        }
    }

    public async Task<RateLimitTelemetrySnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var db = connectionMultiplexer.GetDatabase();
            var now = timeProvider.GetUtcNow();

            var minute = await SumBucketsAsync(db, BucketKeys(now, FineBucketPrefix, FineBucketSeconds, FineBucketsPerWindow));
            var day = await SumBucketsAsync(db, BucketKeys(now, CoarseBucketPrefix, CoarseBucketSeconds, CoarseBucketsPerWindow));

            var lastRejection = Deserialize<RateLimitLastRejection>(await db.StringGetAsync(LastRejectionKey));

            return new RateLimitTelemetrySnapshot(
                TelemetryAvailable: true,
                BuildPolicyCounters(minute, day),
                lastRejection,
                BuildCacheCounters(minute, day),
                await BuildProviderCountersAsync(db, minute, day));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Rate-Limit-Telemetrie konnte nicht gelesen werden; der Snapshot wird als nicht verfügbar gemeldet.");
            return RateLimitTelemetrySnapshot.Unavailable;
        }
    }

    private async Task<IReadOnlyList<RateLimitProviderCounters>> BuildProviderCountersAsync(
        IDatabase db,
        IReadOnlyDictionary<string, long> minute,
        IReadOnlyDictionary<string, long> day)
    {
        // Which providers exist is derived from the counters themselves rather than from a separate
        // index: one fewer key to keep in sync, at the price that a provider silent for more than a
        // day drops out entirely — which is the honest reading of "nothing happened".
        var dimensions = Dimensions(minute, day, ProviderFieldPrefix, partsPerDimension: 2);
        var counters = new List<RateLimitProviderCounters>(dimensions.Count);

        foreach (var dimension in dimensions)
        {
            var incident = Deserialize<ProviderRateLimitIncident>(await db.StringGetAsync(ProviderIncidentKey(dimension)));
            var headers = Deserialize<ProviderRateLimitHeaderSample>(await db.StringGetAsync(ProviderHeaderKey(dimension)));
            var parts = dimension.Split(DimensionSeparator);

            counters.Add(new RateLimitProviderCounters(
                parts[0],
                parts[1],
                minute.GetValueOrDefault(ProviderField(dimension, "requests")),
                day.GetValueOrDefault(ProviderField(dimension, "requests")),
                minute.GetValueOrDefault(ProviderField(dimension, "rate-limited")),
                day.GetValueOrDefault(ProviderField(dimension, "rate-limited")),
                incident?.RetryAfterSeconds,
                incident?.ObservedAtUtc,
                headers));
        }

        return counters;
    }

    private void LogWriteFailure(Exception exception, string what, string dimension) =>
        logger.LogWarning(
            exception,
            "Rate-Limit-Telemetrie ({Was}, {Dimension}) konnte nicht geschrieben werden; der Produktpfad ist davon nicht betroffen.",
            what,
            dimension);

    private static IEnumerable<Task> BucketWrites(IDatabase db, DateTimeOffset now, string field)
    {
        // Both tiers get the same event. The expiry is re-set on every write instead of only on
        // creation: it is one pipelined command, and a bucket that somehow lost its TTL would
        // otherwise stay in Redis forever.
        var fine = BucketKey(now, FineBucketPrefix, FineBucketSeconds);
        var coarse = BucketKey(now, CoarseBucketPrefix, CoarseBucketSeconds);

        return
        [
            db.HashIncrementAsync(fine, field),
            db.KeyExpireAsync(fine, FineBucketTtl),
            db.HashIncrementAsync(coarse, field),
            db.KeyExpireAsync(coarse, CoarseBucketTtl),
        ];
    }

    private static async Task<IReadOnlyDictionary<string, long>> SumBucketsAsync(IDatabase db, IReadOnlyList<RedisKey> keys)
    {
        // Fired without awaiting in between so StackExchange.Redis pipelines them: 1440 tiny hashes are
        // one multiplexed burst, not 1440 round trips. Only the admin page reads this, every 30 seconds.
        var reads = new Task<HashEntry[]>[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            reads[i] = db.HashGetAllAsync(keys[i]);
        }

        var buckets = await Task.WhenAll(reads);
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var entry in buckets.SelectMany(bucket => bucket))
        {
            var field = entry.Name.ToString();
            if (entry.Value.TryParse(out long value))
            {
                totals[field] = totals.GetValueOrDefault(field) + value;
            }
        }

        return totals;
    }

    private static IReadOnlyList<RateLimitPolicyCounters> BuildPolicyCounters(
        IReadOnlyDictionary<string, long> minute,
        IReadOnlyDictionary<string, long> day) =>
        Dimensions(minute, day, PolicyFieldPrefix, partsPerDimension: 1)
            .Select(name => new RateLimitPolicyCounters(
                name,
                minute.GetValueOrDefault(PolicyField(name, "accepted")),
                minute.GetValueOrDefault(PolicyField(name, "rejected")),
                day.GetValueOrDefault(PolicyField(name, "accepted")),
                day.GetValueOrDefault(PolicyField(name, "rejected"))))
            .ToList();

    private static IReadOnlyList<RateLimitCacheCounters> BuildCacheCounters(
        IReadOnlyDictionary<string, long> minute,
        IReadOnlyDictionary<string, long> day) =>
        Dimensions(minute, day, CacheFieldPrefix, partsPerDimension: 1)
            .Select(name => new RateLimitCacheCounters(
                name,
                minute.GetValueOrDefault(CacheField(name, "hit")),
                minute.GetValueOrDefault(CacheField(name, "miss")),
                day.GetValueOrDefault(CacheField(name, "hit")),
                day.GetValueOrDefault(CacheField(name, "miss"))))
            .ToList();

    /// <summary>
    /// The dimension names present in either window, for one field prefix. Field names are
    /// <c>{prefix}|{name…}|{counter}</c>, so the dimension is everything between the two.
    /// </summary>
    private static IReadOnlyList<string> Dimensions(
        IReadOnlyDictionary<string, long> minute,
        IReadOnlyDictionary<string, long> day,
        string fieldPrefix,
        int partsPerDimension) =>
        minute.Keys
            .Concat(day.Keys)
            .Where(field => field.StartsWith(fieldPrefix, StringComparison.Ordinal))
            .Select(field => field.Split(DimensionSeparator))
            .Where(parts => parts.Length == partsPerDimension + 2)
            .Select(parts => string.Join(DimensionSeparator, parts.Skip(1).Take(partsPerDimension)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<RedisKey> BucketKeys(DateTimeOffset now, string prefix, int bucketSeconds, int bucketCount)
    {
        var newest = now.ToUnixTimeSeconds() / bucketSeconds;
        var keys = new RedisKey[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            keys[i] = $"{prefix}{newest - i}";
        }

        return keys;
    }

    private static RedisKey BucketKey(DateTimeOffset now, string prefix, int bucketSeconds) =>
        $"{prefix}{now.ToUnixTimeSeconds() / bucketSeconds}";

    private static RedisKey ProviderIncidentKey(string dimension) => $"{KeyPrefix}:provider:{dimension}:last-429";

    private static RedisKey ProviderHeaderKey(string dimension) => $"{KeyPrefix}:provider:{dimension}:headers";

    private static string PolicyField(string policyName, string counter) =>
        $"{PolicyFieldPrefix}{Sanitize(policyName)}{DimensionSeparator}{counter}";

    private static string CacheField(string cacheName, string counter) =>
        $"{CacheFieldPrefix}{Sanitize(cacheName)}{DimensionSeparator}{counter}";

    private static string ProviderField(string dimension, string counter) =>
        $"{ProviderFieldPrefix}{dimension}{DimensionSeparator}{counter}";

    private static string ProviderDimension(string providerName, string callSource) =>
        $"{Sanitize(providerName)}{DimensionSeparator}{Sanitize(callSource)}";

    /// <summary>
    /// Names are supposed to be constants, so this is a guard rather than a transformation: it keeps a
    /// stray separator or a runaway string from silently creating a second dimension or an unbounded
    /// field name. Raw URLs never get this far — the contract forbids them at the call site.
    /// </summary>
    private static string Sanitize(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "unknown";
        }

        var cleaned = new string(trimmed
            .Take(MaxNameLength)
            .Select(c => c == DimensionSeparator || char.IsControl(c) || char.IsWhiteSpace(c) ? '-' : c)
            .ToArray());

        return cleaned;
    }

    /// <summary>
    /// The partition itself or a sub-partition of it: the voting policy partitions by
    /// <c>{userId}:{sessionId}</c>, which still names the same user.
    /// </summary>
    private static bool BelongsToPartition(string storedPartition, string partition) =>
        storedPartition == partition
        || storedPartition.StartsWith(partition + ":", StringComparison.Ordinal);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static T? Deserialize<T>(RedisValue value) where T : class =>
        value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<T>(value.ToString(), JsonSerializerOptions.Web);

    /// <summary>The last real provider 429, kept next to its counters. Private: the reader folds it into
    /// <see cref="RateLimitProviderCounters"/>, so it never needs a name outside this class.</summary>
    private sealed record ProviderRateLimitIncident(DateTime ObservedAtUtc, int? RetryAfterSeconds);
}
