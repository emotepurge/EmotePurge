using System.Text;
using EmotePurge.Infrastructure.ChatLogArchive;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class BoundedLineScannerTests
{
    [Fact]
    public async Task ReadLineAsync_SplitsOnNewlines_DropsTheCarriageReturn_AndReturnsAnUnterminatedLastLine()
    {
        var scanner = Scanner("first\nsecond\r\n\nlast", maxLineBytes: 64);

        Assert.Equal("first", await ReadTextAsync(scanner));
        Assert.Equal("second", await ReadTextAsync(scanner));
        Assert.Equal("", await ReadTextAsync(scanner));
        Assert.Equal("last", await ReadTextAsync(scanner));
        Assert.Equal(BoundedLineKind.EndOfStream, (await scanner.ReadLineAsync(CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task ReadLineAsync_DoesNotInventAnEmptyLineAfterATrailingNewline()
    {
        var scanner = Scanner("only\n", maxLineBytes: 64);

        Assert.Equal("only", await ReadTextAsync(scanner));
        Assert.Equal(BoundedLineKind.EndOfStream, (await scanner.ReadLineAsync(CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task ReadLineAsync_DecodesMultiByteCharactersSplitAcrossReads()
    {
        // One byte per read: every multi-byte sequence is cut between reads.
        var body = "ä😀ö end\nnext\n";
        var scanner = new BoundedLineScanner(new OneByteAtATimeStream(Encoding.UTF8.GetBytes(body)), maxLineBytes: 64);

        Assert.Equal("ä😀ö end", await ReadTextAsync(scanner));
        Assert.Equal("next", await ReadTextAsync(scanner));
    }

    [Fact]
    public async Task ReadLineAsync_SkipsALeadingByteOrderMark()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("hello\n")).ToArray();
        var scanner = new BoundedLineScanner(new MemoryStream(bytes), maxLineBytes: 64);

        Assert.Equal("hello", await ReadTextAsync(scanner));
    }

    [Fact]
    public async Task ReadLineAsync_AcceptsALineOfExactlyTheLimit_AndRejectsOneByteMore()
    {
        var exact = new string('a', 100);
        var accepted = Scanner(exact + "\nnext\n", maxLineBytes: 100);
        Assert.Equal(exact, await ReadTextAsync(accepted));
        Assert.Equal("next", await ReadTextAsync(accepted));

        var rejected = Scanner(exact + "b\nnext\n", maxLineBytes: 100);
        Assert.Equal(BoundedLineKind.TooLong, (await rejected.ReadLineAsync(CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task ReadLineAsync_AfterTooLong_StaysTooLong_AndReadsNothingFurther()
    {
        var source = new GeneratedStream(10_000_000);
        var scanner = new BoundedLineScanner(source, maxLineBytes: 2048);

        Assert.Equal(BoundedLineKind.TooLong, (await scanner.ReadLineAsync(CancellationToken.None)).Kind);
        var servedAtFailure = source.BytesServed;
        Assert.Equal(BoundedLineKind.TooLong, (await scanner.ReadLineAsync(CancellationToken.None)).Kind);

        Assert.Equal(servedAtFailure, source.BytesServed);
        Assert.True(servedAtFailure <= 2048 + BoundedLineScanner.ReadBufferBytes);
    }

    [Fact]
    public async Task A50MbBodyWithoutANewline_EndsTooLong_WithThePeakBufferWithinTheLimitPlusOneKiB()
    {
        // EPIC AC 24: generated, never materialised.
        const int maxLineBytes = 16 * 1024;
        var source = new GeneratedStream(50L * 1024 * 1024);
        var scanner = new BoundedLineScanner(source, maxLineBytes);

        var result = await scanner.ReadLineAsync(CancellationToken.None);

        Assert.Equal(BoundedLineKind.TooLong, result.Kind);
        Assert.True(scanner.PeakBufferedBytes <= maxLineBytes + 1024, $"peak buffer was {scanner.PeakBufferedBytes}");
        Assert.True(source.BytesServed <= maxLineBytes + 1024, $"read {source.BytesServed} bytes before giving up");
    }

    [Fact]
    public async Task PeakBufferedBytes_StaysSmallForShortLinesOverALongBody()
    {
        var line = new string('x', 300) + "\n";
        var body = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(line, 20_000)));
        var scanner = new BoundedLineScanner(new MemoryStream(body), maxLineBytes: 16 * 1024);

        var count = 0;
        while ((await scanner.ReadLineAsync(CancellationToken.None)).Kind == BoundedLineKind.Line)
        {
            count++;
        }

        Assert.Equal(20_000, count);
        Assert.True(scanner.PeakBufferedBytes <= 1024 + 512, $"peak buffer was {scanner.PeakBufferedBytes}");
    }

    [Fact]
    public void Constructor_RejectsAnUnusableLimit() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedLineScanner(new MemoryStream(), maxLineBytes: 0));

    private static BoundedLineScanner Scanner(string body, int maxLineBytes) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(body)), maxLineBytes);

    private static async Task<string?> ReadTextAsync(BoundedLineScanner scanner)
    {
        var line = await scanner.ReadLineAsync(CancellationToken.None);
        Assert.Equal(BoundedLineKind.Line, line.Kind);
        return line.Text;
    }

    private sealed class OneByteAtATimeStream(byte[] body) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= body.Length)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[0] = body[_position++];
            return ValueTask.FromResult(1);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // `length` bytes of 'a' with no newline, generated on demand.
    private sealed class GeneratedStream(long length) : Stream
    {
        public long BytesServed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var toServe = (int)Math.Min(buffer.Length, length - BytesServed);
            buffer.Span[..toServe].Fill((byte)'a');
            BytesServed += toServe;
            return ValueTask.FromResult(toServe);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
