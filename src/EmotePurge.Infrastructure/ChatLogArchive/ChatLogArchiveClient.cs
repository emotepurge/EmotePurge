using System.Net;
using System.Security.Cryptography;
using System.Text;
using EmotePurge.Core.ChatLogArchive;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.ChatLogArchive;

/// <summary>
/// Streams one channel-day of chat history from the justlog-compatible <c>?raw</c> text endpoint
/// (measured live 2026-09-05, T8) instead of buffering the whole body — a large channel's day can
/// run into the tens of megabytes.
/// <para>
/// <b>Strictly sequential.</b> This client issues at most one request at a time and waits at
/// least <see cref="ChatLogArchiveOptions.RequestDelay"/> between the start of consecutive
/// requests, tracked as instance state on <see cref="_lastRequestStartedAtTicks"/>. A second,
/// concurrent call while one is already in flight is a caller error — there is no internal lock
/// enforcing it. This pacing is bound to the instance's lifetime: the caller must resolve one
/// instance and hold it for the whole run (Task 6's day-loop). Resolving a fresh instance per
/// call — e.g. from a transient DI registration — silently loses the pacing, with no error and no
/// log line (Fixrunde 1 finding; see the registration comment in
/// <c>ServiceCollectionExtensions</c>).
/// </para>
/// <para>
/// <b>No retry, no rate limiter.</b> The archive documents no contract (Premise 5 of the design):
/// every non-2xx response and every transport failure is reported once via
/// <see cref="ChatLogDayResult"/> and left to the caller (the harness, Task 6) to decide whether
/// to resume this day later.
/// </para>
/// <para>
/// <b>Two timeouts, two jobs.</b> The typed <c>HttpClient</c>'s own <c>Timeout</c> only covers the
/// header phase — <c>SendAsync</c> is called with <c>HttpCompletionOption.ResponseHeadersRead</c>,
/// so it returns as soon as headers arrive. Reading the body afterwards is governed by its own
/// <see cref="ChatLogArchiveOptions.BodyTimeout"/>-bounded <see cref="CancellationTokenSource"/>,
/// linked to the caller's token: a body timeout maps to <see cref="ChatLogDayStatus.BodyTimeout"/>,
/// a caller cancellation during the body maps to <see cref="ChatLogDayStatus.Cancelled"/> — the two
/// are told apart via <c>ct.IsCancellationRequested</c>. Both carry the bytes received so far, because
/// the caller books them against its byte budget. A caller cancellation before the body (request
/// pacing, header phase) still propagates as <see cref="OperationCanceledException"/>: nothing was
/// read, so there is nothing to report.
/// </para>
/// <para>
/// <b>The byte cap bites where the bytes arrive</b>, inside the counting stream, not after a line
/// has been assembled: once more than <c>maxBytes</c> have been read the stream reports end of body,
/// so the <see cref="StreamReader"/> cannot keep buffering an overlong or newline-free line past the
/// cap. At most one read buffer (1024 bytes) lands beyond <c>maxBytes</c>.
/// </para>
/// </summary>
public class ChatLogArchiveClient(
    HttpClient httpClient, ChatLogArchiveOptions options, ILogger<ChatLogArchiveClient> logger) : IChatLogArchiveClient
{
    // A day whose lines are more than half unreadable as any recognized IRC command means the wire
    // format itself changed underneath this client (Failure Mode "Wurzelform anders als
    // angenommen"), not that the channel happens to have an unusually moderation-heavy day.
    private const double MalformedLineRatioThreshold = 0.5;

    private long? _lastRequestStartedAtTicks;

    public async Task<ChatLogDayResult> ReadDayAsync(
        string twitchChannelId, DateOnly day, long maxBytes, Func<ChatLogMessage, ValueTask> onMessage, CancellationToken ct)
    {
        await WaitForRequestSlotAsync(ct);
        _lastRequestStartedAtTicks = Environment.TickCount64;

        var path = $"channelid/{twitchChannelId}/{day.Year}/{day.Month}/{day.Day}?raw";

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        // A caller cancellation must propagate as-is (see class comment); only a genuine transport
        // failure — including HttpClient's own header-phase Timeout firing — is reported here.
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Log-Archiv-Anfrage für Kanal {ChannelId}, Tag {Day} fehlgeschlagen.", twitchChannelId, day);
            return new ChatLogDayResult(ChatLogDayStatus.TransportFailure, 0, null, 0, 0, 0, null);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Debug, not Information: 404 is the normal case for most channel-days (T8), and a
                // backfill over thousands of channel-days would otherwise write thousands of "nothing
                // happened" lines (same reasoning as issue #32's loglevel choice for its own no-op path).
                logger.LogDebug("Kein Log-Tag für Kanal {ChannelId}, Tag {Day} (404, laut T8 der Normalzustand).", twitchChannelId, day);
                return new ChatLogDayResult(ChatLogDayStatus.NoLogDay, 0, null, 0, 0, 0, (int)response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning("Log-Archiv hat Kanal {ChannelId}, Tag {Day} gedrosselt (429), Body wird nicht gelesen.", twitchChannelId, day);
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, (int)response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Log-Archiv-Abruf für Kanal {ChannelId}, Tag {Day} lieferte unerwarteten Status {Status}.",
                    twitchChannelId, day, response.StatusCode);
                return new ChatLogDayResult(ChatLogDayStatus.TransportFailure, 0, null, 0, 0, 0, (int)response.StatusCode);
            }

            return await ReadBodyAsync(response, twitchChannelId, day, maxBytes, onMessage, ct);
        }
    }

    private async Task<ChatLogDayResult> ReadBodyAsync(
        HttpResponseMessage response, string twitchChannelId, DateOnly day, long maxBytes,
        Func<ChatLogMessage, ValueTask> onMessage, CancellationToken ct)
    {
        using var bodyTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyTimeoutCts.CancelAfter(options.BodyTimeout);
        var bodyCt = bodyTimeoutCts.Token;

        var httpStatusCode = (int)response.StatusCode;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Declared here rather than inside the try below (Fixrunde 1 finding): a transport failure
        // mid-body needs to report however many bytes had actually arrived before it, not 0 — the
        // two other abort paths (BodyTimeout, ByteCapExceeded) already did this right because they
        // return from inside the same scope as the stream. leaveOpen: true on the StreamReader below
        // means this is the only thing that disposes it, exactly once, in the finally block.
        CountingHashStream? countingStream = null;

        try
        {
            Stream rawStream;
            try
            {
                rawStream = await response.Content.ReadAsStreamAsync(bodyCt);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                logger.LogWarning(ex, "Log-Archiv-Übertragung für Kanal {ChannelId}, Tag {Day} beim Öffnen des Bodys abgebrochen.", twitchChannelId, day);
                return new ChatLogDayResult(ChatLogDayStatus.TransportFailure, 0, null, 0, 0, 0, httpStatusCode);
            }

            // Bytes are counted and fed into the digest as they arrive off the wire, before the
            // corresponding text is decoded into a line and handed to the parser — the digest
            // therefore belongs to the received body, not to what the parser made of it.
            countingStream = new CountingHashStream(rawStream, hash, maxBytes);
            using var reader = new StreamReader(countingStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);

            var scan = await ScanLinesAsync(
                reader, countingStream, maxBytes, onMessage, twitchChannelId, day, httpStatusCode, bodyCt, ct);
            if (scan.Failure is { } failure)
            {
                return failure;
            }

            var (messageCount, nonPrivmsgLines, malformedLines) = (scan.MessageCount, scan.NonPrivmsgLines, scan.MalformedLines);
            var totalLines = messageCount + nonPrivmsgLines + malformedLines;
            if (totalLines > 0 && malformedLines / (double)totalLines > MalformedLineRatioThreshold)
            {
                logger.LogWarning(
                    "Log-Archiv-Antwort für Kanal {ChannelId}, Tag {Day} überwiegend nicht als IRC-Zeilen lesbar ({Malformed}/{Total}) — Wurzelform vermutlich geändert.",
                    twitchChannelId, day, malformedLines, totalLines);
                return new ChatLogDayResult(
                    ChatLogDayStatus.MalformedResponse, countingStream.BytesRead, null, messageCount, nonPrivmsgLines, malformedLines, httpStatusCode);
            }

            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            logger.LogInformation(
                "Log-Archiv-Abruf für Kanal {ChannelId}, Tag {Day} abgeschlossen: {Messages} Nachrichten, {Bytes} Bytes.",
                twitchChannelId, day, messageCount, countingStream.BytesRead);
            return new ChatLogDayResult(
                ChatLogDayStatus.Complete, countingStream.BytesRead, digest, messageCount, nonPrivmsgLines, malformedLines, httpStatusCode);
        }
        finally
        {
            if (countingStream is not null)
            {
                await countingStream.DisposeAsync();
            }
        }
    }

    // Reads and classifies every remaining line of the body: PRIVMSGs go to onMessage, every other
    // recognized command and every unreadable line are only counted. Runs until EOF (line is null,
    // the normal end) or a fatal reason to stop reading further — a body-read timeout, a caller
    // cancellation, a mid-body transport failure, or the byte cap — each of which already carries
    // the counts gathered so far into the ChatLogDayResult it returns as Failure. The narrow
    // try/catch around only the read call is deliberate (Fixrunde 1 finding): an exception onMessage
    // itself throws is the caller's error, not a transport failure, and must propagate unchanged
    // instead of being reported as one.
    private async Task<LineScanOutcome> ScanLinesAsync(
        StreamReader reader, CountingHashStream countingStream, long maxBytes,
        Func<ChatLogMessage, ValueTask> onMessage, string twitchChannelId, DateOnly day, int httpStatusCode,
        CancellationToken bodyCt, CancellationToken ct)
    {
        var messageCount = 0;
        var nonPrivmsgLines = 0;
        var malformedLines = 0;

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(bodyCt);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Returned, not rethrown: the bytes that already arrived must reach the caller's byte
                // budget, and the count dies with the stream once this method is left by a throw.
                logger.LogWarning(
                    "Chat-log archive read for channel {ChannelId}, day {Day} was cancelled by the caller after {Bytes} bytes.",
                    twitchChannelId, day, countingStream.BytesRead);
                return new LineScanOutcome(messageCount, nonPrivmsgLines, malformedLines, new ChatLogDayResult(
                    ChatLogDayStatus.Cancelled, countingStream.BytesRead, null, messageCount, nonPrivmsgLines, malformedLines, httpStatusCode));
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning(
                    "Log-Archiv-Abruf für Kanal {ChannelId}, Tag {Day} wegen Body-Timeout ({Timeout}) abgebrochen.",
                    twitchChannelId, day, options.BodyTimeout);
                return new LineScanOutcome(messageCount, nonPrivmsgLines, malformedLines, new ChatLogDayResult(
                    ChatLogDayStatus.BodyTimeout, countingStream.BytesRead, null, messageCount, nonPrivmsgLines, malformedLines, httpStatusCode));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                logger.LogWarning(ex, "Log-Archiv-Übertragung für Kanal {ChannelId}, Tag {Day} mitten im Body abgebrochen.", twitchChannelId, day);
                return new LineScanOutcome(messageCount, nonPrivmsgLines, malformedLines, new ChatLogDayResult(
                    ChatLogDayStatus.TransportFailure, countingStream.BytesRead, null, messageCount, nonPrivmsgLines, malformedLines, httpStatusCode));
            }

            // Checked before the end-of-body test, not after it: past the cap the counting stream
            // reports end of body, so a null line here can mean "cap reached" as well as "done".
            if (countingStream.CapExceeded)
            {
                logger.LogWarning(
                    "Log-Archiv-Abruf für Kanal {ChannelId}, Tag {Day} über die Byte-Obergrenze ({MaxBytes}) hinaus abgebrochen, Antwort verworfen.",
                    twitchChannelId, day, maxBytes);
                return new LineScanOutcome(messageCount, nonPrivmsgLines, malformedLines, new ChatLogDayResult(
                    ChatLogDayStatus.ByteCapExceeded, countingStream.BytesRead, null, messageCount, nonPrivmsgLines, malformedLines, httpStatusCode));
            }

            if (line is null)
            {
                return new LineScanOutcome(messageCount, nonPrivmsgLines, malformedLines, null);
            }

            if (JustlogRawLineParser.TryParse(line, out var message, out var ircCommand))
            {
                messageCount++;
                await onMessage(message);
            }
            else if (ircCommand is not null)
            {
                nonPrivmsgLines++;
            }
            else
            {
                malformedLines++;
            }
        }
    }

    private async Task WaitForRequestSlotAsync(CancellationToken ct)
    {
        if (_lastRequestStartedAtTicks is null)
        {
            return;
        }

        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastRequestStartedAtTicks.Value);
        var remaining = options.RequestDelay - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, ct);
        }
    }

    // Wraps the raw response stream to count bytes and feed them into the running SHA-256 exactly
    // as they are read off the wire, independent of however StreamReader chooses to buffer them —
    // so the digest and byte count reflect the received body, not an approximation reconstructed
    // from decoded text. It is also where the byte cap bites: once more than maxBytes have been
    // read, every further read reports end of body without touching the wire, so StreamReader
    // cannot go on accumulating an overlong line. CapExceeded tells that apart from a real end.
    private sealed class CountingHashStream(Stream inner, IncrementalHash hash, long maxBytes) : Stream
    {
        public long BytesRead { get; private set; }

        public bool CapExceeded => BytesRead > maxBytes;

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

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        // One read path, so the counting, the hashing and the cap cannot drift apart between the
        // two overloads (StreamReader only ever calls the Memory<byte> one).
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (CapExceeded)
            {
                return 0;
            }

            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                hash.AppendData(buffer.Span[..read]);
                BytesRead += read;
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // Outcome of ScanLinesAsync: the line/command counts gathered up to whatever point scanning
    // stopped at, plus — only when scanning stopped for a fatal reason rather than reaching EOF —
    // the already-built ChatLogDayResult ReadBodyAsync returns as-is.
    private readonly record struct LineScanOutcome(int MessageCount, int NonPrivmsgLines, int MalformedLines, ChatLogDayResult? Failure);
}
