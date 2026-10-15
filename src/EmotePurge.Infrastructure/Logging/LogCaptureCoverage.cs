namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// In-memory coverage marker of this process (spec §11, 3.3): the latest moment from which the error
/// capture is not gapless. It only ever rises. The sink raises it to "now" whenever an entry of level
/// Error or above is dropped for any reason; the writer seeds it at start (process start, or the marker
/// adopted from a clean predecessor), raises it on failed or unconfirmed batches and heartbeats, and
/// carries it in every heartbeat. Lock-free; <see cref="DateTime.MinValue"/> until first set.
/// </summary>
internal sealed class LogCaptureCoverage
{
    private long _ticks;

    public DateTime LastIncompleteAtUtc => new(Interlocked.Read(ref _ticks), DateTimeKind.Utc);

    /// <summary>Seeds the marker at writer start; never lowers a mark an earlier drop already set.</summary>
    public void Initialize(DateTime value) => Raise(value);

    public void MarkIncomplete(DateTime nowUtc) => Raise(nowUtc);

    private void Raise(DateTime value)
    {
        var ticks = value.Ticks;
        var current = Interlocked.Read(ref _ticks);
        while (ticks > current)
        {
            var seen = Interlocked.CompareExchange(ref _ticks, ticks, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
