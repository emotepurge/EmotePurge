namespace EmotePurge.Api.LogViewer;

/// <summary>
/// Caps the log queries that run at once on this Api instance (spec section 8.4, A22): the viewer's reads can
/// each take up to the 5 s statement limit, and this keeps a few open tabs from queueing a pile of them
/// behind the shared connection pool. This is a plain concurrency gate, not a rate-limit policy — the admin
/// group deliberately has none.
/// </summary>
internal sealed class LogQueryConcurrencyGate(int maxConcurrent = 2, TimeSpan? wait = null)
{
    private readonly SemaphoreSlim _slots = new(maxConcurrent, maxConcurrent);
    private readonly TimeSpan _wait = wait ?? TimeSpan.FromSeconds(5);

    /// <summary>The disposable releases the slot; <c>null</c> when none became free within the wait.</summary>
    public async Task<IDisposable?> TryEnterAsync(CancellationToken ct)
    {
        return await _slots.WaitAsync(_wait, ct) ? new Lease(_slots) : null;
    }

    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
