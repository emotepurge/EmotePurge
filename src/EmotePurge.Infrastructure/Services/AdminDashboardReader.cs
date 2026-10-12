using System.Collections.Concurrent;
using EmotePurge.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

/// <inheritdoc cref="IAdminDashboardReader" />
public sealed class AdminDashboardReader : IAdminDashboardReader
{
    public static readonly TimeSpan ActivityTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TrendTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ShortLoadTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LongLoadTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RebuildWaitTimeout = TimeSpan.FromSeconds(5);
    // A stalled read must not hold the single gate until the driver's own command timeout.
    public static readonly TimeSpan RebuildDeadline = TimeSpan.FromSeconds(30);
    public const int BusyRetryAfterSeconds = 5;

    private const string SingleKey = "";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRateLimitTelemetryReader _telemetryReader;
    private readonly TimeProvider _time;
    private readonly SystemLoadOptions _loadOptions;
    private readonly ILogger<AdminDashboardReader> _logger;
    // One rebuild at a time across all load ranges: a cold 24h read is the expensive one, and six
    // ranges rebuilding together would be the memory peak the single gate exists to prevent.
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private readonly TtlCache<AdminDashboardLoad> _shortLoad;
    private readonly TtlCache<AdminDashboardLoad> _longLoad;
    private readonly TtlCache<AdminDashboardActivity> _activity;
    private readonly TtlCache<RateLimitTelemetrySnapshot> _snapshot;
    private readonly TtlCache<RateLimitTelemetryTrend> _trend;

    public AdminDashboardReader(
        IServiceScopeFactory scopeFactory,
        IRateLimitTelemetryReader telemetryReader,
        TimeProvider time,
        SystemLoadOptions loadOptions,
        ILogger<AdminDashboardReader> logger)
    {
        _scopeFactory = scopeFactory;
        _telemetryReader = telemetryReader;
        _time = time;
        _loadOptions = loadOptions;
        _logger = logger;
        _shortLoad = new TtlCache<AdminDashboardLoad>(time, ShortLoadTtl);
        _longLoad = new TtlCache<AdminDashboardLoad>(time, LongLoadTtl);
        _activity = new TtlCache<AdminDashboardActivity>(time, ActivityTtl);
        _snapshot = new TtlCache<RateLimitTelemetrySnapshot>(time, SnapshotTtl);
        _trend = new TtlCache<RateLimitTelemetryTrend>(time, TrendTtl);
    }

    public Task<AdminDashboardActivity> GetActivityAsync(
        AdminDashboardActivityRange range,
        CancellationToken cancellationToken = default)
        => _activity.GetAsync(
            AdminDashboardActivityRanges.ToWire(range),
            async () =>
            {
                // The query service is scoped (AppDbContext); the fill outlives the request that
                // started it, so it opens a scope of its own.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var query = scope.ServiceProvider.GetRequiredService<IAdminDashboardQueryService>();
                return await query.GetActivityAsync(range, _time.GetUtcNow().UtcDateTime, CancellationToken.None);
            },
            cancellationToken);

    public async Task<AdminDashboardLoadRead> GetLoadAsync(
        AdminDashboardLoadRange range,
        CancellationToken cancellationToken = default)
    {
        var cache = range is AdminDashboardLoadRange.Hour1 or AdminDashboardLoadRange.Hours24 ? _shortLoad : _longLoad;
        try
        {
            var load = await cache.GetAsync(
                AdminDashboardLoadRanges.ToWire(range), () => RebuildLoadAsync(range), cancellationToken);
            return AdminDashboardLoadRead.Ok(load);
        }
        catch (RebuildBusyException)
        {
            // Raised inside the single-flight fill, so it is never cached and joined waiters see it too.
            return AdminDashboardLoadRead.BusyFor(BusyRetryAfterSeconds);
        }
    }

    public Task<RateLimitTelemetrySnapshot> GetRateLimitSnapshotAsync(CancellationToken cancellationToken = default)
        => _snapshot.GetAsync(SingleKey, () => _telemetryReader.ReadAsync(CancellationToken.None), cancellationToken);

    public Task<RateLimitTelemetryTrend> GetRateLimitTrendAsync(CancellationToken cancellationToken = default)
        => _trend.GetAsync(SingleKey, () => _telemetryReader.ReadTrendAsync(CancellationToken.None), cancellationToken);

    private async Task<AdminDashboardLoad> RebuildLoadAsync(AdminDashboardLoadRange range)
    {
        using (var timeout = new CancellationTokenSource(RebuildWaitTimeout, _time))
        {
            try
            {
                await _rebuildGate.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new RebuildBusyException();
            }
        }

        // The try opens right after the acquire: nothing between WaitAsync and the release may throw
        // past the finally, or the gate would stay taken and every later read would answer Busy.
        var wire = "?";
        var started = 0L;
        var outcome = "failed";
        try
        {
            wire = AdminDashboardLoadRanges.ToWire(range);
            started = _time.GetTimestamp();
            using var deadline = new CancellationTokenSource(RebuildDeadline, _time);
            _logger.LogDebug("Load dashboard cold rebuild started range={Range}", wire);

            // The query service is scoped (AppDbContext); the fill outlives the request that started it.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var query = scope.ServiceProvider.GetRequiredService<IAdminDashboardQueryService>();
            var load = await query.GetLoadAsync(
                range, _time.GetUtcNow().UtcDateTime, _loadOptions.IntervalSeconds, deadline.Token);
            outcome = "ok";
            return load;
        }
        finally
        {
            try
            {
                _logger.LogDebug(
                    "Load dashboard cold rebuild finished range={Range} ms={ElapsedMs} outcome={Outcome}",
                    wire,
                    (long)_time.GetElapsedTime(started).TotalMilliseconds,
                    outcome);
            }
            finally
            {
                _rebuildGate.Release();
            }
        }
    }

    private sealed class RebuildBusyException : Exception;

    // Keyed TTL cache with single-flight: one fill per key runs at a time, every concurrent reader waits
    // on it under its own token. The expiry is stamped when the fill finishes, so a running fill never
    // expires; a failed fill is expired at once and replaced by the next reader, i.e. never cached.
    private sealed class TtlCache<T>
    {
        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly TimeProvider _time;
        private readonly TimeSpan _ttl;

        public TtlCache(TimeProvider time, TimeSpan ttl)
        {
            _time = time;
            _ttl = ttl;
        }

        public async Task<T> GetAsync(string key, Func<Task<T>> fill, CancellationToken cancellationToken)
        {
            while (true)
            {
                var entry = _entries.GetOrAdd(key, _ => NewEntry(fill));
                if (entry.IsExpired(_time.GetUtcNow()))
                {
                    // Swap against the exact entry seen; the loser of a race simply re-reads the winner's.
                    _entries.TryUpdate(key, NewEntry(fill), entry);
                    continue;
                }

                return await entry.Value.WaitAsync(cancellationToken);
            }
        }

        private Entry NewEntry(Func<Task<T>> fill)
        {
            var entry = new Entry();
            // Lazy with ExecutionAndPublication: only the entry that ends up in the dictionary is ever
            // started, however many were constructed in a race.
            entry.Start = new Lazy<Task<T>>(() => RunAsync(entry, fill));
            return entry;
        }

        private async Task<T> RunAsync(Entry entry, Func<Task<T>> fill)
        {
            try
            {
                var result = await Task.Run(fill);
                entry.ExpiresAt = _time.GetUtcNow() + _ttl;
                return result;
            }
            catch
            {
                entry.ExpiresAt = DateTimeOffset.MinValue;
                throw;
            }
        }

        private sealed class Entry
        {
            private long _expiresAtTicks = long.MaxValue;

            public Lazy<Task<T>> Start { get; set; } = null!;

            public Task<T> Value => Start.Value;

            public DateTimeOffset ExpiresAt
            {
                set => Interlocked.Exchange(ref _expiresAtTicks, value.UtcTicks);
            }

            public bool IsExpired(DateTimeOffset now) => now.UtcTicks >= Interlocked.Read(ref _expiresAtTicks);
        }
    }
}
