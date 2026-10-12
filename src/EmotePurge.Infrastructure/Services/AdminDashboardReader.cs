using System.Collections.Concurrent;
using EmotePurge.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EmotePurge.Infrastructure.Services;

/// <inheritdoc cref="IAdminDashboardReader" />
public sealed class AdminDashboardReader : IAdminDashboardReader
{
    public static readonly TimeSpan ActivityTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TrendTtl = TimeSpan.FromSeconds(30);

    private const string SingleKey = "";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRateLimitTelemetryReader _telemetryReader;
    private readonly TimeProvider _time;
    private readonly TtlCache<AdminDashboardActivity> _activity;
    private readonly TtlCache<RateLimitTelemetrySnapshot> _snapshot;
    private readonly TtlCache<RateLimitTelemetryTrend> _trend;

    public AdminDashboardReader(
        IServiceScopeFactory scopeFactory,
        IRateLimitTelemetryReader telemetryReader,
        TimeProvider time)
    {
        _scopeFactory = scopeFactory;
        _telemetryReader = telemetryReader;
        _time = time;
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

    public Task<RateLimitTelemetrySnapshot> GetRateLimitSnapshotAsync(CancellationToken cancellationToken = default)
        => _snapshot.GetAsync(SingleKey, () => _telemetryReader.ReadAsync(CancellationToken.None), cancellationToken);

    public Task<RateLimitTelemetryTrend> GetRateLimitTrendAsync(CancellationToken cancellationToken = default)
        => _trend.GetAsync(SingleKey, () => _telemetryReader.ReadTrendAsync(CancellationToken.None), cancellationToken);

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
