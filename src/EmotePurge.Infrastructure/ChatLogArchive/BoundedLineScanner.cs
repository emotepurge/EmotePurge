using System.Text;

namespace EmotePurge.Infrastructure.ChatLogArchive;

/// <summary>What <see cref="BoundedLineScanner.ReadLineAsync"/> found.</summary>
public enum BoundedLineKind
{
    /// <summary>A complete line (terminator not included; may be empty).</summary>
    Line,

    /// <summary>The stream ended; no further line.</summary>
    EndOfStream,

    /// <summary>A line exceeded the limit before its terminator arrived. The scanner is unusable afterwards.</summary>
    TooLong
}

/// <summary>One scanner result: <see cref="Text"/> is set only for <see cref="BoundedLineKind.Line"/>.</summary>
public readonly record struct BoundedLine(BoundedLineKind Kind, string? Text);

/// <summary>
/// Line reader for hostile or simply huge bodies. Unlike <c>StreamReader.ReadLineAsync</c>, which
/// accumulates a delimiter-free body without bound, it holds at most one read buffer
/// (<see cref="ReadBufferBytes"/>) plus one line buffer that never grows beyond
/// <c>maxLineBytes</c>: a longer line is reported as <see cref="BoundedLineKind.TooLong"/> the moment
/// the limit is crossed, without reading further.
/// <para>
/// Lines are split on the byte <c>0x0A</c> (which never occurs inside a UTF-8 multi-byte sequence, so
/// splitting before decoding is safe) and decoded as UTF-8; a trailing <c>0x0D</c> is dropped, and a
/// UTF-8 byte-order mark at the very start of the stream is skipped. A final line without a terminator
/// is returned like any other.
/// </para>
/// <para>
/// <b>Counting.</b> The limit counts raw bytes of the line without its <c>0x0A</c> terminator, so a
/// <c>0x0D</c> in front of it counts (a line of exactly the limit plus a CR is too long). A bare
/// <c>0x0D</c> is not a separator - unlike <c>StreamReader</c>, which also splits on it - and stays in
/// the line unless it is the last byte before the terminator. A BOM is recognised however the
/// first reads are cut (a decompression stream may hand out a single byte at first): the first reads
/// are topped up to three bytes before the check, and a body shorter than that stays ordinary content.
/// </para>
/// </summary>
public sealed class BoundedLineScanner
{
    /// <summary>Size of the single read buffer, in bytes.</summary>
    public const int ReadBufferBytes = 1024;

    private const int InitialLineCapacity = 256;

    private readonly Stream _stream;
    private readonly int _maxLineBytes;
    private readonly byte[] _readBuffer = new byte[ReadBufferBytes];
    private byte[] _line;
    private int _lineLength;
    private int _readPosition;
    private int _readLength;
    private bool _atStart = true;
    private bool _ended;
    private bool _tooLong;

    public BoundedLineScanner(Stream stream, int maxLineBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 1);
        _stream = stream;
        _maxLineBytes = maxLineBytes;
        _line = new byte[Math.Min(InitialLineCapacity, maxLineBytes)];
        PeakBufferedBytes = ReadBufferBytes + _line.Length;
    }

    /// <summary>
    /// The largest number of bytes this scanner ever held at once: the read buffer plus the line
    /// buffer's capacity. By construction at most <c>maxLineBytes + ReadBufferBytes</c>.
    /// </summary>
    public int PeakBufferedBytes { get; private set; }

    public async ValueTask<BoundedLine> ReadLineAsync(CancellationToken ct)
    {
        // Checked on every call, not only where the stream is read: lines already buffered (several
        // per read) would otherwise keep coming after a cancel, and an unterminated last line would be
        // followed by a clean end of stream, which the client would report as Complete.
        ct.ThrowIfCancellationRequested();

        if (_tooLong)
        {
            return new BoundedLine(BoundedLineKind.TooLong, null);
        }

        while (true)
        {
            if (_readPosition >= _readLength)
            {
                if (_ended)
                {
                    return FinishAtEnd();
                }

                _readLength = await _stream.ReadAsync(_readBuffer.AsMemory(), ct);
                _readPosition = 0;
                if (_readLength == 0)
                {
                    _ended = true;
                    return FinishAtEnd();
                }

                if (_atStart)
                {
                    _atStart = false;

                    // The first read may be shorter than the three bytes of a BOM (GZipStream and
                    // BrotliStream can start with one byte). Top the buffer up, then decide; a stream
                    // that ends first leaves its 1-2 bytes in the buffer as ordinary content.
                    while (_readLength < 3)
                    {
                        var more = await _stream.ReadAsync(_readBuffer.AsMemory(_readLength), ct);
                        if (more == 0)
                        {
                            _ended = true;
                            break;
                        }

                        _readLength += more;
                    }

                    if (_readLength >= 3 && _readBuffer[0] == 0xEF && _readBuffer[1] == 0xBB && _readBuffer[2] == 0xBF)
                    {
                        _readPosition = 3;
                    }
                }
            }

            var span = _readBuffer.AsSpan(_readPosition, _readLength - _readPosition);
            var newline = span.IndexOf((byte)'\n');
            var chunk = newline >= 0 ? span[..newline] : span;

            if (_lineLength + chunk.Length > _maxLineBytes)
            {
                _tooLong = true;
                return new BoundedLine(BoundedLineKind.TooLong, null);
            }

            Append(chunk);
            if (newline >= 0)
            {
                _readPosition += newline + 1;
                return new BoundedLine(BoundedLineKind.Line, TakeLine());
            }

            _readPosition = _readLength;
        }
    }

    private BoundedLine FinishAtEnd() =>
        _lineLength > 0
            ? new BoundedLine(BoundedLineKind.Line, TakeLine())
            : new BoundedLine(BoundedLineKind.EndOfStream, null);

    private void Append(ReadOnlySpan<byte> chunk)
    {
        var needed = _lineLength + chunk.Length;
        if (needed > _line.Length)
        {
            var capacity = Math.Min(Math.Max(needed, _line.Length * 2), _maxLineBytes);
            Array.Resize(ref _line, capacity);
            PeakBufferedBytes = Math.Max(PeakBufferedBytes, ReadBufferBytes + _line.Length);
        }

        chunk.CopyTo(_line.AsSpan(_lineLength));
        _lineLength = needed;
    }

    private string TakeLine()
    {
        var length = _lineLength;
        _lineLength = 0;
        if (length > 0 && _line[length - 1] == (byte)'\r')
        {
            length--;
        }

        return Encoding.UTF8.GetString(_line, 0, length);
    }
}
