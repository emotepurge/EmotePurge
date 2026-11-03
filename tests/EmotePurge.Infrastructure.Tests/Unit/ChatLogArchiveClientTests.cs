using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Infrastructure.ChatLogArchive;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Own streaming fake, not the buffering StubHandler from TwitchHelixClientTests (that one is
// private there and returns a StringContent, which would defeat the point of testing a streaming
// reader). LineChunkedStream hands the reader exactly one file line per ReadAsync call, so byte
// counting stays deterministic and line-granular in these tests; StallingStream simulates
// a connection that stops delivering bytes mid-body (Failure Mode "Log-Client Body").
public class ChatLogArchiveClientTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Unit", "TestData", "chatlog-raw-day.txt");

    // ---- ReadRangeAsync (#346) ------------------------------------------------------------------

    private static readonly DateTime RangeFrom = new(2026, 4, 9, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RangeTo = new(2026, 4, 16, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ReadRangeAsync_RequestsTheSecondPrecisionRangePath_AndCountsTheFixtureBody()
    {
        var fixtureBytes = await File.ReadAllBytesAsync(FixturePath);
        var handler = new CapturingStreamHandler(() => new LineChunkedStream(fixtureBytes));
        var client = CreateClient(handler, new ChatLogArchiveOptions());

        var received = new List<ChatLogMessage>();
        var result = await client.ReadRangeAsync(
            "900000001", RangeFrom, RangeTo, maxBytes: 10_000_000,
            msg => { received.Add(msg); return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.Equal("/channelid/900000001?from=2026-04-09T00:00:00Z&to=2026-04-16T00:00:00Z&raw", handler.LastRequestPathAndQuery);
        Assert.Equal(ChatLogDayStatus.Complete, result.Status);
        Assert.Equal(fixtureBytes.Length, result.BytesReceived);
        Assert.Equal(6, result.MessageCount);
        Assert.Equal(2, result.NonPrivmsgLines);
        Assert.Equal(0, result.MalformedLines);
        Assert.Equal((int)HttpStatusCode.OK, result.HttpStatusCode);
        Assert.Null(result.RetryAfter);
        Assert.Equal("hey everyone", received[0].Text);
        Assert.Equal("waves hello", received[2].Text);
        Assert.Equal("gg", received[^1].Text);
    }

    [Fact]
    public async Task ReadRangeAsync_FormatsMillisecondsAsPlainSecondPrecisionUtc()
    {
        var handler = new CapturingStreamHandler(() => new MemoryStream());
        var client = CreateClient(handler, new ChatLogArchiveOptions());

        await client.ReadRangeAsync(
            "1", new DateTime(2026, 4, 9, 23, 59, 59, 999, DateTimeKind.Utc), new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc),
            1000, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("/channelid/1?from=2026-04-09T23:59:59Z&to=2026-04-10T00:00:00Z&raw", handler.LastRequestPathAndQuery);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task ReadRangeAsync_WithANonUtcInstant_Throws_BeforeAnyRequest(DateTimeKind kind)
    {
        var handler = new CapturingStreamHandler(() => new MemoryStream());
        var client = CreateClient(handler, new ChatLogArchiveOptions());
        var nonUtc = new DateTime(2026, 4, 9, 0, 0, 0, kind);

        var fromEx = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ReadRangeAsync("1", nonUtc, RangeTo, 1000, _ => ValueTask.CompletedTask, CancellationToken.None));
        var toEx = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ReadRangeAsync("1", RangeFrom, nonUtc, 1000, _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("fromUtc", fromEx.ParamName);
        Assert.Equal("toUtcExclusive", toEx.ParamName);
        Assert.Null(handler.LastRequestPathAndQuery);
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task ReadRangeAsync_WithABodyThatIsNotWhatItsContentEncodingSays_ReturnsMalformedResponse(string encoding)
    {
        await using var server = await CompressedLoopbackServer.StartRawAsync(Encoding.UTF8.GetBytes("this is plain text, not a compressed stream at all"), encoding);
        using var http = new HttpClient(ChatLogArchiveClient.CreatePrimaryHandler()) { BaseAddress = server.BaseAddress };
        var client = new ChatLogArchiveClient(http, new ChatLogArchiveOptions(), new RecordingLogger<ChatLogArchiveClient>());

        var result = await client.ReadRangeAsync("1", RangeFrom, RangeTo, 10_000_000, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.MalformedResponse, result.Status);
        Assert.Equal(0, result.MessageCount);
    }

    [Fact]
    public async Task ReadRangeAsync_WhenTheCallbackThrowsInvalidOperation_StillPropagatesIt()
    {
        // The corrupt-encoding catch is scoped to the body read; a caller's own InvalidOperationException
        // must never be relabelled as a malformed response.
        var fixtureBytes = await File.ReadAllBytesAsync(FixturePath);
        var client = CreateClient(new CapturingStreamHandler(() => new LineChunkedStream(fixtureBytes)), new ChatLogArchiveOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ReadRangeAsync("1", RangeFrom, RangeTo, 10_000_000, _ => throw new InvalidOperationException("caller bug"), CancellationToken.None));
    }

    [Fact]
    public async Task ReadRangeAsync_With404_ReturnsNoLogDay_WithoutInvokingCallback()
    {
        var client = CreateClient(new FixedStatusStubHandler(HttpStatusCode.NotFound), new ChatLogArchiveOptions());
        var callbackInvoked = false;

        var result = await client.ReadRangeAsync(
            "1", RangeFrom, RangeTo, 1000, _ => { callbackInvoked = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.NoLogDay, result.Status);
        Assert.False(callbackInvoked);
        Assert.Equal((int)HttpStatusCode.NotFound, result.HttpStatusCode);
        Assert.Equal(0, result.BytesReceived);
    }

    [Fact]
    public async Task ReadRangeAsync_With429AndDeltaSecondsRetryAfter_ReturnsRateLimitedWithTheDelay()
    {
        var client = CreateClient(new RetryAfterHandler(r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120))), new ChatLogArchiveOptions());

        var result = await client.ReadRangeAsync("1", RangeFrom, RangeTo, 1000, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
        Assert.Equal((int)HttpStatusCode.TooManyRequests, result.HttpStatusCode);
        Assert.Equal(0, result.BytesReceived);
    }

    [Fact]
    public async Task ReadRangeAsync_With429WithoutRetryAfter_ReturnsRateLimitedWithNullDelay()
    {
        var client = CreateClient(new RetryAfterHandler(_ => { }), new ChatLogArchiveOptions());

        var result = await client.ReadRangeAsync("1", RangeFrom, RangeTo, 1000, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.RateLimited, result.Status);
        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task ReadRangeAsync_With429AndHttpDateRetryAfter_ReturnsTheRemainingTime_AndZeroForAPastDate()
    {
        var clock = new HandWoundTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        var future = new DateTimeOffset(2026, 10, 9, 12, 5, 0, TimeSpan.Zero);
        var past = new DateTimeOffset(2026, 10, 9, 11, 0, 0, TimeSpan.Zero);

        var futureClient = CreateClient(
            new RetryAfterHandler(r => r.Headers.RetryAfter = new RetryConditionHeaderValue(future)), new ChatLogArchiveOptions(), clock);
        var pastClient = CreateClient(
            new RetryAfterHandler(r => r.Headers.RetryAfter = new RetryConditionHeaderValue(past)), new ChatLogArchiveOptions(), clock);

        var futureResult = await futureClient.ReadRangeAsync("1", RangeFrom, RangeTo, 1000, _ => ValueTask.CompletedTask, CancellationToken.None);
        var pastResult = await pastClient.ReadRangeAsync("1", RangeFrom, RangeTo, 1000, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(5), futureResult.RetryAfter);
        Assert.Equal(TimeSpan.Zero, pastResult.RetryAfter);
    }

    [Fact]
    public async Task ReadRangeAsync_WithBodyThatStalls_ReturnsBodyTimeoutAfterRangeBodyTimeout()
    {
        var handler = new CapturingStreamHandler(() => new StallingStream(Encoding.UTF8.GetBytes("@partial"), stallAfterBytes: 4));
        var options = new ChatLogArchiveOptions { RangeBodyTimeout = TimeSpan.FromMilliseconds(300) };
        var client = CreateClient(handler, options);

        var sw = Stopwatch.StartNew();
        var result = await client.ReadRangeAsync("1", RangeFrom, RangeTo, 10_000_000, _ => ValueTask.CompletedTask, CancellationToken.None);
        sw.Stop();

        Assert.Equal(ChatLogDayStatus.BodyTimeout, result.Status);
        Assert.Equal(4, result.BytesReceived);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"expected the range body timeout to fire quickly, took {sw.Elapsed}");
    }

    [Fact]
    public async Task ReadRangeAsync_WithCallerCancellationMidBody_ReturnsCancelledWithTheBytesSoFar()
    {
        var handler = new CapturingStreamHandler(() => new StallingStream(Encoding.UTF8.GetBytes("@partial"), stallAfterBytes: 4));
        var client = CreateClient(handler, new ChatLogArchiveOptions());
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var result = await client.ReadRangeAsync("1", RangeFrom, RangeTo, 10_000_000, _ => ValueTask.CompletedTask, cts.Token);

        Assert.Equal(ChatLogDayStatus.Cancelled, result.Status);
        Assert.Equal(4, result.BytesReceived);
    }

    [Fact]
    public async Task ReadRangeAsync_WhenTheCallbackCancelsOnAnUnterminatedLastLine_ReturnsCancelled_NotComplete()
    {
        // The last line has no newline, so the scanner hands it out at end of stream and the next call
        // would report a clean end. The cancel must win: the run was cancelled, it did not complete.
        var fixtureBytes = await File.ReadAllBytesAsync(FixturePath);
        var firstLine = fixtureBytes[..Array.IndexOf(fixtureBytes, (byte)'\n')];
        var client = CreateClient(new CapturingStreamHandler(() => new MemoryStream(firstLine)), new ChatLogArchiveOptions());
        using var cts = new CancellationTokenSource();

        var result = await client.ReadRangeAsync(
            "1", RangeFrom, RangeTo, 10_000_000, async _ => await cts.CancelAsync(), cts.Token);

        Assert.Equal(ChatLogDayStatus.Cancelled, result.Status);
        // The bytes that had arrived: the whole unterminated line, which was also counted as a message.
        Assert.Equal(firstLine.Length, result.BytesReceived);
        Assert.Equal(1, result.MessageCount);
    }

    [Fact]
    public async Task ReadRangeAsync_WithCallerCancellationBeforeTheHeaders_StillThrows()
    {
        var client = CreateClient(new HangingHandler(), new ChatLogArchiveOptions());
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ReadRangeAsync("1", RangeFrom, RangeTo, 1000, _ => ValueTask.CompletedTask, cts.Token));
    }

    [Fact]
    public async Task ReadRangeAsync_EnforcesMaxBytes_WithinOneBufferOfTheCap()
    {
        const long maxBytes = 64 * 1024;
        var source = new NewlineFreeStream(8 * 1024 * 1024);
        var client = CreateClient(new CapturingStreamHandler(() => source), new ChatLogArchiveOptions { MaxLineBytes = 1024 * 1024 });

        var result = await client.ReadRangeAsync("1", RangeFrom, RangeTo, maxBytes, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.ByteCapExceeded, result.Status);
        Assert.Equal(source.BytesServed, result.BytesReceived);
        Assert.True(result.BytesReceived > maxBytes);
        Assert.True(result.BytesReceived <= maxBytes + 1024, $"expected at most one buffer beyond the cap, got {result.BytesReceived}");
    }

    [Fact]
    public async Task ReadRangeAsync_WithABodyExactlyAsLargeAsTheCap_IsComplete_AndOneByteLessIsNot()
    {
        var fixtureBytes = await File.ReadAllBytesAsync(FixturePath);
        var client = CreateClient(new CapturingStreamHandler(() => new LineChunkedStream(fixtureBytes)), new ChatLogArchiveOptions());

        var exact = await client.ReadRangeAsync("1", RangeFrom, RangeTo, fixtureBytes.Length, _ => ValueTask.CompletedTask, CancellationToken.None);
        var tooSmall = await client.ReadRangeAsync("1", RangeFrom, RangeTo, fixtureBytes.Length - 1, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.Complete, exact.Status);
        Assert.Equal(ChatLogDayStatus.ByteCapExceeded, tooSmall.Status);
    }

    [Fact]
    public async Task ReadRangeAsync_WithA50MbNewlineFreeBody_EndsLineTooLong_AfterReadingAtMostOneLineAndOneBuffer()
    {
        // EPIC AC 24, unit part: the body is generated, never materialised, and the client must stop
        // at the line limit instead of buffering it.
        var options = new ChatLogArchiveOptions();
        var source = new NewlineFreeStream(50L * 1024 * 1024);
        var client = CreateClient(new CapturingStreamHandler(() => source), options);
        var callbackInvoked = false;

        var result = await client.ReadRangeAsync(
            "1", RangeFrom, RangeTo, maxBytes: 256L * 1024 * 1024,
            _ => { callbackInvoked = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.LineTooLong, result.Status);
        Assert.False(callbackInvoked);
        Assert.Equal(source.BytesServed, result.BytesReceived);
        Assert.True(
            result.BytesReceived <= options.MaxLineBytes + BoundedLineScanner.ReadBufferBytes,
            $"expected to stop within the line limit plus one buffer, read {result.BytesReceived}");
    }

    [Fact]
    public async Task ReadRangeAsync_WithMostlyUnparsableLines_ReturnsMalformedResponse()
    {
        var body = string.Join('\n', Enumerable.Repeat("this is not an irc line at all", 6)) + "\n";
        var client = CreateClient(new CapturingStreamHandler(() => new MemoryStream(Encoding.UTF8.GetBytes(body))), new ChatLogArchiveOptions());

        var result = await client.ReadRangeAsync("1", RangeFrom, RangeTo, 10_000_000, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.MalformedResponse, result.Status);
        Assert.Equal(6, result.MalformedLines);
        Assert.Equal(0, result.MessageCount);
    }

    [Fact]
    public async Task ReadRangeAsync_WithTransportErrorMidBody_ReturnsTransportFailureWithTheBytesSoFar()
    {
        var handler = new CapturingStreamHandler(() => new ThrowingAfterBytesStream(Encoding.UTF8.GetBytes("@partial-line-before-drop"), throwAfterBytes: 5));
        var client = CreateClient(handler, new ChatLogArchiveOptions());

        var result = await client.ReadRangeAsync("1", RangeFrom, RangeTo, 10_000_000, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.TransportFailure, result.Status);
        Assert.Equal(5, result.BytesReceived);
        Assert.Equal((int)HttpStatusCode.OK, result.HttpStatusCode);
    }

    [Fact]
    public async Task ReadRangeAsync_WhenCallbackThrows_PropagatesTheException()
    {
        var fixtureBytes = await File.ReadAllBytesAsync(FixturePath);
        var client = CreateClient(new CapturingStreamHandler(() => new LineChunkedStream(fixtureBytes)), new ChatLogArchiveOptions());

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            client.ReadRangeAsync("1", RangeFrom, RangeTo, 10_000_000, _ => throw new IOException("storage full"), CancellationToken.None));

        Assert.Equal("storage full", thrown.Message);
    }

    [Fact]
    public async Task ReadRangeAsync_WithUnexpectedNon2xxStatus_ReturnsTransportFailureWithStatusCode()
    {
        var client = CreateClient(new FixedStatusStubHandler(HttpStatusCode.InternalServerError), new ChatLogArchiveOptions());

        var result = await client.ReadRangeAsync("1", RangeFrom, RangeTo, 1000, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.TransportFailure, result.Status);
        Assert.Equal((int)HttpStatusCode.InternalServerError, result.HttpStatusCode);
    }

    [Fact]
    public async Task ReadRangeAsync_WithHeaderPhaseTransportError_ReturnsTransportFailureWithNullStatus()
    {
        var client = CreateClient(new ThrowingHandler(), new ChatLogArchiveOptions());

        var result = await client.ReadRangeAsync("1", RangeFrom, RangeTo, 1000, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(ChatLogDayStatus.TransportFailure, result.Status);
        Assert.Null(result.HttpStatusCode);
    }

    [Fact]
    public async Task ReadRangeAsync_CalledTwiceInARow_WaitsAtLeastRequestDelayBetweenStarts()
    {
        var options = new ChatLogArchiveOptions { RequestDelay = TimeSpan.FromMilliseconds(300) };
        var client = CreateClient(new FixedStatusStubHandler(HttpStatusCode.NotFound), options);

        var sw = Stopwatch.StartNew();
        await client.ReadRangeAsync("1", RangeFrom, RangeTo, 1000, _ => ValueTask.CompletedTask, CancellationToken.None);
        await client.ReadRangeAsync("1", RangeFrom, RangeTo, 1000, _ => ValueTask.CompletedTask, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed >= options.RequestDelay - TimeSpan.FromMilliseconds(50), $"only waited {sw.Elapsed}");
    }

    [Fact]
    public async Task ReadRangeAsync_DecodesABrotliBody_WithThePrimaryHandlerTheRegistrationUses()
    {
        // The registration's decompression is real transport behaviour, so this goes over a loopback
        // socket: a stub handler would hand the client the already-decoded stream and prove nothing.
        var plain = await File.ReadAllBytesAsync(FixturePath);
        await using var server = await CompressedLoopbackServer.StartAsync(plain);
        using var http = new HttpClient(ChatLogArchiveClient.CreatePrimaryHandler()) { BaseAddress = server.BaseAddress };
        var client = new ChatLogArchiveClient(http, new ChatLogArchiveOptions(), new RecordingLogger<ChatLogArchiveClient>());

        var result = await client.ReadRangeAsync("1", RangeFrom, RangeTo, 10_000_000, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.True(server.CompressedLength < plain.Length);
        Assert.Equal(ChatLogDayStatus.Complete, result.Status);
        // BytesReceived counts the decompressed bytes the parser saw, not the smaller wire size.
        Assert.Equal(plain.Length, result.BytesReceived);
        Assert.Equal(6, result.MessageCount);
    }

    // One-shot loopback HTTP server that answers the first request with `body` brotli-compressed and
    // Content-Encoding: br.
    private sealed class CompressedLoopbackServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _serve;

        private CompressedLoopbackServer(HttpListener listener, Task serve, Uri baseAddress, int compressedLength)
        {
            _listener = listener;
            _serve = serve;
            BaseAddress = baseAddress;
            CompressedLength = compressedLength;
        }

        public Uri BaseAddress { get; }

        public int CompressedLength { get; }

        public static Task<CompressedLoopbackServer> StartRawAsync(byte[] wireBody, string contentEncoding) =>
            StartListeningAsync(wireBody, contentEncoding);

        public static async Task<CompressedLoopbackServer> StartAsync(byte[] body)
        {
            byte[] compressed;
            using (var buffer = new MemoryStream())
            {
                await using (var brotli = new System.IO.Compression.BrotliStream(buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                {
                    await brotli.WriteAsync(body);
                }

                compressed = buffer.ToArray();
            }

            return await StartListeningAsync(compressed, "br");
        }

        private static Task<CompressedLoopbackServer> StartListeningAsync(byte[] compressed, string contentEncoding)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var serve = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync();
                context.Response.AddHeader("Content-Encoding", contentEncoding);
                context.Response.ContentLength64 = compressed.Length;
                await context.Response.OutputStream.WriteAsync(compressed);
                context.Response.Close();
            });

            return Task.FromResult(new CompressedLoopbackServer(listener, serve, new Uri($"http://127.0.0.1:{port}/"), compressed.Length));
        }

        public async ValueTask DisposeAsync()
        {
            await _serve;
            _listener.Close();
        }
    }

    private static ChatLogArchiveClient CreateClient(HttpMessageHandler handler, ChatLogArchiveOptions options, TimeProvider? timeProvider = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://logs.example.test/") };
        return new ChatLogArchiveClient(httpClient, options, new RecordingLogger<ChatLogArchiveClient>(), timeProvider);
    }

    // Records the request the client built, then serves the stream.
    private sealed class CapturingStreamHandler(Func<Stream> streamFactory) : HttpMessageHandler
    {
        public string? LastRequestPathAndQuery { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestPathAndQuery = request.RequestUri!.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(streamFactory()) });
        }
    }

    private sealed class RetryAfterHandler(Action<HttpResponseMessage> configure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            configure(response);
            return Task.FromResult(response);
        }
    }

    private sealed class FixedStatusStubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class StreamStubHandler(HttpStatusCode statusCode, Func<Stream> streamFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode) { Content = new StreamContent(streamFactory()) };
            return Task.FromResult(response);
        }
    }

    // Fails before any response is even produced — simulates a DNS/connection failure during the
    // header phase (distinct from every other failure test here, which fails during or after the
    // response headers already arrived).
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Simulated connection failure.");
    }

    // Never produces a response on its own — only the caller's token ends the header phase.
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    // Hands StreamReader exactly one source line (including its trailing '\n') per ReadAsync call,
    // so the client's line-granular byte counting/hashing behaves deterministically in tests
    // instead of depending on however much a real network stream happens to buffer ahead.
    private sealed class LineChunkedStream(byte[] body) : Stream
    {
        private readonly List<(int Offset, int Length)> _lines = SplitIntoLines(body);
        private int _lineIndex;
        private int _offsetInLine;

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
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_lineIndex >= _lines.Count)
            {
                return ValueTask.FromResult(0);
            }

            var (offset, length) = _lines[_lineIndex];
            var remaining = length - _offsetInLine;
            var toCopy = Math.Min(remaining, buffer.Length);
            body.AsSpan(offset + _offsetInLine, toCopy).CopyTo(buffer.Span);
            _offsetInLine += toCopy;
            if (_offsetInLine >= length)
            {
                _lineIndex++;
                _offsetInLine = 0;
            }

            return ValueTask.FromResult(toCopy);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static List<(int Offset, int Length)> SplitIntoLines(byte[] body)
        {
            var result = new List<(int, int)>();
            var start = 0;
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] == (byte)'\n')
                {
                    result.Add((start, i - start + 1));
                    start = i + 1;
                }
            }

            if (start < body.Length)
            {
                result.Add((start, body.Length - start));
            }

            return result;
        }
    }

    // Delivers `stallAfterBytes` bytes normally, then hangs on every subsequent read until the
    // caller's token cancels — simulating a connection that stops sending mid-body without closing
    // (Failure Mode "Log-Client Body": "Aggregator-Instanz stockt mitten in 16 MB").
    private sealed class StallingStream(byte[] body, int stallAfterBytes) : Stream
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

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var cap = Math.Min(stallAfterBytes, body.Length);
            if (_position >= cap)
            {
                // Never completes on its own — only the caller's token (directly, or via the
                // client's body-timeout CTS linked to it) can end this.
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            var toCopy = Math.Min(buffer.Length, cap - _position);
            body.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return toCopy;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A body of `length` bytes without a single newline, served in whatever chunk size the reader
    // asks for. BytesServed records how much of it was actually pulled, so a test can tell "stopped
    // near the cap" apart from "read everything, then noticed".
    private sealed class NewlineFreeStream(long length) : Stream
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

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

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

    // Delivers `throwAfterBytes` bytes normally, then throws IOException — simulating a dropped
    // connection mid-body, distinct from a stall (the socket errors out instead of going silent).
    private sealed class ThrowingAfterBytesStream(byte[] body, int throwAfterBytes) : Stream
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

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= throwAfterBytes)
            {
                throw new IOException("Simulated connection drop mid-body.");
            }

            var toCopy = Math.Min(buffer.Length, throwAfterBytes - _position);
            body.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return ValueTask.FromResult(toCopy);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
