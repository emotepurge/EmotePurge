using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using EmotePurge.Core.ChatLogArchive;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.ChatLogArchive;

/// <summary>
/// Streams a UTC range of one channel's chat history from the justlog-compatible <c>?raw</c> text
/// endpoint (measured live 2026-09-05, T8) instead of buffering the whole body — a large channel's
/// week can run into the tens of megabytes.
/// <para>
/// <b>Strictly sequential.</b> This client issues at most one request at a time and waits at
/// least <see cref="ChatLogArchiveOptions.RequestDelay"/> between the start of consecutive
/// requests, tracked as instance state on <see cref="_lastRequestStartedAtTicks"/>. A second,
/// concurrent call while one is already in flight is a caller error — there is no internal lock
/// enforcing it. This pacing is bound to the instance's lifetime: the caller must resolve one
/// instance and hold it for the whole run. Resolving a fresh instance per call — e.g. from a
/// transient DI registration — silently loses the pacing, with no error and no log line (see the
/// registration comment in <c>ServiceCollectionExtensions</c>).
/// </para>
/// <para>
/// <b>No retry, no rate limiter.</b> The archive documents no contract (Premise 5 of the design):
/// every non-2xx response and every transport failure is reported once via
/// <see cref="ChatLogRangeResult"/> and left to the caller (the backfill worker) to decide whether
/// to retry the block later.
/// </para>
/// <para>
/// <b>Two timeouts, two jobs.</b> The typed <c>HttpClient</c>'s own <c>Timeout</c> only covers the
/// header phase — <c>SendAsync</c> is called with <c>HttpCompletionOption.ResponseHeadersRead</c>,
/// so it returns as soon as headers arrive. Reading the body afterwards is governed by its own
/// <see cref="ChatLogArchiveOptions.RangeBodyTimeout"/>-bounded <see cref="CancellationTokenSource"/>,
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
/// so the line scanner cannot keep buffering an overlong or newline-free line past the cap.
/// </para>
/// </summary>
public class ChatLogArchiveClient(
    HttpClient httpClient, ChatLogArchiveOptions options, ILogger<ChatLogArchiveClient> logger, TimeProvider? timeProvider = null)
    : IChatLogArchiveClient
{
    // A day whose lines are more than half unreadable as any recognized IRC command means the wire
    // format itself changed underneath this client (Failure Mode "Wurzelform anders als
    // angenommen"), not that the channel happens to have an unusually moderation-heavy day.
    private const double MalformedLineRatioThreshold = 0.5;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private long? _lastRequestStartedAtTicks;

    /// <summary>
    /// The primary handler the typed client is registered with: brotli, gzip and deflate are accepted,
    /// so the byte cap and the reported byte counts are over decompressed bytes. Public so the
    /// registration and its test share one definition.
    /// </summary>
    public static HttpMessageHandler CreatePrimaryHandler() =>
        new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };

    public async Task<ChatLogRangeResult> ReadRangeAsync(
        string twitchChannelId, DateTime fromUtc, DateTime toUtcExclusive, long maxBytes,
        Func<ChatLogMessage, ValueTask> onMessage, CancellationToken ct)
    {
        var path = $"channelid/{twitchChannelId}?from={FormatInstant(fromUtc, nameof(fromUtc))}&to={FormatInstant(toUtcExclusive, nameof(toUtcExclusive))}&raw";

        await WaitForRequestSlotAsync(ct);
        _lastRequestStartedAtTicks = Environment.TickCount64;

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        // A caller cancellation propagates, a genuine transport failure (including the header-phase
        // Timeout) is reported.
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Chat-log archive range request for channel {ChannelId} ({From} to {To}) failed.", twitchChannelId, fromUtc, toUtcExclusive);
            return new ChatLogRangeResult(ChatLogDayStatus.TransportFailure, 0, 0, 0, 0, null, null);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Debug: 404 is the normal answer for a range without any log.
                logger.LogDebug("No chat log for channel {ChannelId} in {From} to {To} (404).", twitchChannelId, fromUtc, toUtcExclusive);
                return new ChatLogRangeResult(ChatLogDayStatus.NoLogDay, 0, 0, 0, 0, status, null);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
                logger.LogWarning(
                    "Chat-log archive throttled channel {ChannelId} ({From} to {To}) with 429, Retry-After {RetryAfter}; body not read.",
                    twitchChannelId, fromUtc, toUtcExclusive, retryAfter?.ToString() ?? "none");
                return new ChatLogRangeResult(ChatLogDayStatus.RateLimited, 0, 0, 0, 0, status, retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Chat-log archive range request for channel {ChannelId} ({From} to {To}) returned unexpected status {Status}.",
                    twitchChannelId, fromUtc, toUtcExclusive, response.StatusCode);
                return new ChatLogRangeResult(ChatLogDayStatus.TransportFailure, 0, 0, 0, 0, status, null);
            }

            return await ReadRangeBodyAsync(response, twitchChannelId, maxBytes, onMessage, ct);
        }
    }

    private async Task<ChatLogRangeResult> ReadRangeBodyAsync(
        HttpResponseMessage response, string twitchChannelId, long maxBytes, Func<ChatLogMessage, ValueTask> onMessage, CancellationToken ct)
    {
        using var bodyTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyTimeoutCts.CancelAfter(options.RangeBodyTimeout);
        var bodyCt = bodyTimeoutCts.Token;
        var httpStatusCode = (int)response.StatusCode;

        CountingStream? countingStream = null;
        try
        {
            Stream rawStream;
            try
            {
                rawStream = await response.Content.ReadAsStreamAsync(bodyCt);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                logger.LogWarning(ex, "Chat-log archive range transfer for channel {ChannelId} dropped while opening the body.", twitchChannelId);
                return new ChatLogRangeResult(ChatLogDayStatus.TransportFailure, 0, 0, 0, 0, httpStatusCode, null);
            }

            countingStream = new CountingStream(rawStream, maxBytes);
            var scanner = new BoundedLineScanner(countingStream, options.MaxLineBytes);

            var messageCount = 0;
            var nonPrivmsgLines = 0;
            var malformedLines = 0;

            ChatLogRangeResult Stopped(ChatLogDayStatus stoppedStatus) =>
                new(stoppedStatus, countingStream.BytesRead, messageCount, nonPrivmsgLines, malformedLines, httpStatusCode, null);

            while (true)
            {
                BoundedLine read;
                try
                {
                    read = await scanner.ReadLineAsync(bodyCt);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Chat-log archive range read for channel {ChannelId} was cancelled by the caller after {Bytes} bytes.",
                        twitchChannelId, countingStream.BytesRead);
                    return Stopped(ChatLogDayStatus.Cancelled);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning(
                        "Chat-log archive range read for channel {ChannelId} hit the body timeout ({Timeout}).",
                        twitchChannelId, options.RangeBodyTimeout);
                    return Stopped(ChatLogDayStatus.BodyTimeout);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    logger.LogWarning(ex, "Chat-log archive range transfer for channel {ChannelId} dropped mid-body.", twitchChannelId);
                    return Stopped(ChatLogDayStatus.TransportFailure);
                }
                // A body that claims Content-Encoding br/gzip but is not: the decompression stream
                // throws InvalidOperationException (brotli) or InvalidDataException (gzip/deflate) out
                // of the read. Scoped to this read only - the onMessage callback below is the caller's.
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
                {
                    logger.LogWarning(
                        "Chat-log archive range response for channel {ChannelId} has an undecodable content encoding ({Failure}).",
                        twitchChannelId, ex.GetType().Name);
                    return Stopped(ChatLogDayStatus.MalformedResponse);
                }

                // Before the end-of-body test, as in ScanLinesAsync: past the cap the stream reports
                // end of body, so "no more lines" can mean "cap reached" as well as "done".
                if (countingStream.CapExceeded)
                {
                    logger.LogWarning(
                        "Chat-log archive range read for channel {ChannelId} exceeded the byte cap ({MaxBytes}); response discarded.",
                        twitchChannelId, maxBytes);
                    return Stopped(ChatLogDayStatus.ByteCapExceeded);
                }

                if (read.Kind == BoundedLineKind.TooLong)
                {
                    logger.LogWarning(
                        "Chat-log archive range read for channel {ChannelId} met a line longer than {MaxLineBytes} bytes; response discarded.",
                        twitchChannelId, options.MaxLineBytes);
                    return Stopped(ChatLogDayStatus.LineTooLong);
                }

                if (read.Kind == BoundedLineKind.EndOfStream)
                {
                    break;
                }

                if (JustlogRawLineParser.TryParse(read.Text!, out var message, out var ircCommand))
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

            var totalLines = messageCount + nonPrivmsgLines + malformedLines;
            if (totalLines > 0 && malformedLines / (double)totalLines > MalformedLineRatioThreshold)
            {
                logger.LogWarning(
                    "Chat-log archive range response for channel {ChannelId} is mostly unreadable as IRC lines ({Malformed}/{Total}); wire format probably changed.",
                    twitchChannelId, malformedLines, totalLines);
                return Stopped(ChatLogDayStatus.MalformedResponse);
            }

            logger.LogInformation(
                "Chat-log archive range read for channel {ChannelId} complete: {Messages} messages, {Bytes} bytes.",
                twitchChannelId, messageCount, countingStream.BytesRead);
            return Stopped(ChatLogDayStatus.Complete);
        }
        finally
        {
            if (countingStream is not null)
            {
                await countingStream.DisposeAsync();
            }
        }
    }

    // delta-seconds or HTTP-date, else null; an HTTP-date in the past is "retry now" (zero), never negative.
    private TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - _timeProvider.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
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

    // Second precision in UTC is all a block boundary needs (the archive also accepts milliseconds).
    // The kind is checked rather than converted: a Local or Unspecified instant would be formatted
    // with a 'Z' it does not have, and silently shifting it is not this client's call.
    private static string FormatInstant(DateTime utc, string paramName) =>
        utc.Kind == DateTimeKind.Utc
            ? utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : throw new ArgumentException("The instant must have DateTimeKind.Utc.", paramName);

    // Wraps the raw response stream to count bytes exactly as they are read off the wire,
    // independent of however the line scanner chooses to buffer them — so the byte count reflects
    // the received body, not an approximation reconstructed from decoded text. It is also where the
    // byte cap bites: once more than maxBytes have been read, every further read reports end of
    // body without touching the wire, so the scanner cannot go on accumulating an overlong line.
    // CapExceeded tells that apart from a real end.
    private sealed class CountingStream(Stream inner, long maxBytes) : Stream
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

        // One read path, so the counting and the cap cannot drift apart between the
        // two overloads.
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
}
