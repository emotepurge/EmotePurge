using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Logging;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class LogCaptureBufferTests
{
    private static CapturedLogEntry Entry(string message = "m") =>
        new(Guid.CreateVersion7(), DateTime.UtcNow, LogEntryLevel.Information, "Cat", null, null, message, null, null, null, false, false);

    [Fact]
    public void Defaults_Are5000EntriesAnd8MiB()
    {
        Assert.Equal(5000, LogCaptureBuffer.DefaultMaxEntries);
        Assert.Equal(8L * 1024 * 1024, LogCaptureBuffer.DefaultMaxBytes);
    }

    [Fact]
    public void TryReserveSlot_StopsAtCapacity()
    {
        var buffer = new LogCaptureBuffer(maxEntries: 3, maxBytes: 1000);

        Assert.True(buffer.TryReserveSlot());
        Assert.True(buffer.TryReserveSlot());
        Assert.True(buffer.TryReserveSlot());
        Assert.False(buffer.TryReserveSlot());
        Assert.Equal(3, buffer.ReservedSlots);

        buffer.ReleaseSlot();
        Assert.Equal(2, buffer.ReservedSlots);
        Assert.True(buffer.TryReserveSlot());
    }

    [Fact]
    public void TryCommit_OverByteLimit_ReleasesSlotAndReturnsFalse()
    {
        var buffer = new LogCaptureBuffer(maxEntries: 10, maxBytes: 100);

        Assert.True(buffer.TryReserveSlot());
        Assert.True(buffer.TryCommit(Entry(), 60));
        Assert.True(buffer.TryReserveSlot());
        Assert.False(buffer.TryCommit(Entry(), 41));

        Assert.Equal(1, buffer.ReservedSlots);
        Assert.Equal(60, buffer.BufferedBytes);

        Assert.True(buffer.TryReserveSlot());
        Assert.True(buffer.TryCommit(Entry(), 40));
        Assert.Equal(100, buffer.BufferedBytes);
    }

    [Fact]
    public void Read_ReleasesSlotAndBytes()
    {
        var buffer = new LogCaptureBuffer(maxEntries: 2, maxBytes: 100);
        var first = Entry("first");
        Assert.True(buffer.TryReserveSlot());
        Assert.True(buffer.TryCommit(first, 70));
        Assert.True(buffer.TryReserveSlot());
        Assert.False(buffer.TryCommit(Entry(), 70));

        Assert.True(buffer.Reader.TryRead(out var read));
        Assert.Same(first, read);
        Assert.Equal(0, buffer.ReservedSlots);
        Assert.Equal(0, buffer.BufferedBytes);

        Assert.True(buffer.TryReserveSlot());
        Assert.True(buffer.TryCommit(Entry(), 70));
    }

    [Fact]
    public async Task ReadAsync_ReleasesAndPreservesOrder()
    {
        var buffer = new LogCaptureBuffer(maxEntries: 10, maxBytes: 1000);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(buffer.TryReserveSlot());
            Assert.True(buffer.TryCommit(Entry(i.ToString()), 10));
        }

        buffer.Complete();
        var messages = new List<string>();
        await foreach (var entry in buffer.Reader.ReadAllAsync())
        {
            messages.Add(entry.Message);
        }

        Assert.Equal(["0", "1", "2"], messages);
        Assert.Equal(0, buffer.ReservedSlots);
        Assert.Equal(0, buffer.BufferedBytes);
        Assert.True(buffer.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void TryCommit_AfterComplete_ReturnsFalseAndReleases()
    {
        var buffer = new LogCaptureBuffer(maxEntries: 10, maxBytes: 1000);
        buffer.Complete();

        Assert.True(buffer.TryReserveSlot());
        Assert.False(buffer.TryCommit(Entry(), 10));
        Assert.Equal(0, buffer.ReservedSlots);
        Assert.Equal(0, buffer.BufferedBytes);
    }

    [Fact]
    public void ConcurrentReserve_NeverExceedsCapacity()
    {
        var buffer = new LogCaptureBuffer(maxEntries: 1000, maxBytes: long.MaxValue);
        var admitted = 0;

        Parallel.For(0, 20_000, _ =>
        {
            if (buffer.TryReserveSlot())
            {
                Interlocked.Increment(ref admitted);
            }
        });

        Assert.Equal(1000, admitted);
        Assert.Equal(1000, buffer.ReservedSlots);
    }
}
