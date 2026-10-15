using System.Collections.Concurrent;

namespace EmotePurge.Infrastructure.Services;

// Keyed TTL cache with single-flight: one fill per key runs at a time, every concurrent reader waits
// on it under its own token. The expiry is stamped when the fill finishes, so a running fill never
// expires; a failed fill is expired at once and replaced by the next reader, i.e. never cached.
internal sealed class TtlCache<T>
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
