using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// The process-wide queue between the sink and the writer, bounded twice: at most
/// <see cref="DefaultMaxEntries"/> entries and at most <see cref="DefaultMaxBytes"/> estimated payload,
/// whichever is tighter. A slot is reserved before any formatting (admission step 5) and the bytes are
/// checked only once the entry is built; a full queue rejects the new entry and never evicts older ones.
/// Nothing here blocks: reservations are compare-and-swap counters and the channel itself is unbounded
/// (the counters bound it), so a write never waits. Reading an entry frees its slot and its bytes.
/// </summary>
internal sealed class LogCaptureBuffer
{
    internal const int DefaultMaxEntries = 5000;
    internal const long DefaultMaxBytes = 8L * 1024 * 1024;

    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly Channel<Item> _channel;

    private int _reservedSlots;
    private long _bufferedBytes;

    public LogCaptureBuffer()
        : this(DefaultMaxEntries, DefaultMaxBytes)
    {
    }

    internal LogCaptureBuffer(int maxEntries, long maxBytes)
    {
        _maxEntries = Math.Max(0, maxEntries);
        _maxBytes = Math.Max(0, maxBytes);
        _channel = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            // A synchronous continuation would run the writer's read loop on the logging thread.
            AllowSynchronousContinuations = false,
        });
        Reader = new ReleasingReader(this);
    }

    /// <summary>Single consumer side for the writer; every entry taken from it frees its slot and bytes.</summary>
    public ChannelReader<CapturedLogEntry> Reader { get; }

    /// <summary>Slots in use: reserved by a <c>Log()</c> call still building its entry, or holding a buffered entry.</summary>
    internal int ReservedSlots => Volatile.Read(ref _reservedSlots);

    internal long BufferedBytes => Interlocked.Read(ref _bufferedBytes);

    public bool TryReserveSlot()
    {
        var current = Volatile.Read(ref _reservedSlots);
        while (current < _maxEntries)
        {
            var seen = Interlocked.CompareExchange(ref _reservedSlots, current + 1, current);
            if (seen == current)
            {
                return true;
            }

            current = seen;
        }

        return false;
    }

    public void ReleaseSlot() => Interlocked.Decrement(ref _reservedSlots);

    /// <summary>
    /// Queues an entry on a slot reserved before. If the byte limit would be exceeded (or the buffer is
    /// completed) the slot is released and <c>false</c> returned; the caller counts the drop.
    /// </summary>
    public bool TryCommit(CapturedLogEntry entry, long estimatedBytes)
    {
        var bytes = Math.Max(0, estimatedBytes);
        if (!TryAddBytes(bytes))
        {
            ReleaseSlot();
            return false;
        }

        if (_channel.Writer.TryWrite(new Item(entry, bytes)))
        {
            return true;
        }

        Interlocked.Add(ref _bufferedBytes, -bytes);
        ReleaseSlot();
        return false;
    }

    /// <summary>No further entries are accepted; the reader completes once it has taken what is left.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    private bool TryAddBytes(long bytes)
    {
        var current = Interlocked.Read(ref _bufferedBytes);
        while (current + bytes <= _maxBytes)
        {
            var seen = Interlocked.CompareExchange(ref _bufferedBytes, current + bytes, current);
            if (seen == current)
            {
                return true;
            }

            current = seen;
        }

        return false;
    }

    private void Release(Item item)
    {
        Interlocked.Add(ref _bufferedBytes, -item.Bytes);
        ReleaseSlot();
    }

    private readonly record struct Item(CapturedLogEntry Entry, long Bytes);

    private sealed class ReleasingReader(LogCaptureBuffer buffer) : ChannelReader<CapturedLogEntry>
    {
        private readonly ChannelReader<Item> _inner = buffer._channel.Reader;

        public override Task Completion => _inner.Completion;

        public override bool CanCount => _inner.CanCount;

        public override int Count => _inner.Count;

        public override bool TryRead([MaybeNullWhen(false)] out CapturedLogEntry item)
        {
            if (_inner.TryRead(out var queued))
            {
                buffer.Release(queued);
                item = queued.Entry;
                return true;
            }

            item = null;
            return false;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            _inner.WaitToReadAsync(cancellationToken);
    }
}
