using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EmotePurge.Core.Messaging;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

/// <summary>
/// Limits and pacing of the live-event fan-out. A constructor parameter with defaults rather than
/// IOptions so tests can run with a 50 ms heartbeat without a configuration round-trip.
/// </summary>
public sealed record LiveEventStreamOptions
{
    /// <summary>
    /// Must stay well below the smallest read timeout of anything between us and the browser
    /// (nginx defaults to 60 s), otherwise an idle stream is killed as dead.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Hard ceiling of concurrent streams per process.</summary>
    public int MaxSubscriptions { get; init; } = 500;

    /// <summary>
    /// Concurrent streams one identity may hold (browser tabs of one login). Raised from 3 to 6
    /// because the overview page now holds a stream permanently per tab (<c>live.changed</c>), so
    /// 3 was exhaustible with ordinary multi-tab use: overview + a channel workspace is already
    /// 2 streams, overview + admin monitoring + the admin channel list is 3. An exhausted budget
    /// makes <see cref="ILiveEventStream.SubscribeAsync"/> answer
    /// <see cref="LiveEventSubscribeStatus.QuotaExhausted"/>, the endpoint answer 429 (issue #42 —
    /// a bare, indistinguishable 503 before it), and the browser treats a 429 on an EventSource
    /// handshake as terminal just like a 503 — the tab silently stops receiving live updates for
    /// good. The structural fix — sharing one connection per URL inside the frontend's
    /// <c>LiveUpdateService</c> — has been in place since 2026-08-29
    /// (<c>stream()</c> in <c>live-update.service.ts</c>), which collapses the per-tab count to at
    /// most one per distinct stream URL.
    /// </summary>
    public int MaxPerSubscriber { get; init; } = 6;
}

/// <summary>
/// Subscribes to <see cref="LiveEvents.Channel"/> exactly once per process — lazily, on the first
/// client, so a process that never serves a stream never touches the channel — and fans every
/// message out to the currently open connections.
/// </summary>
public sealed class RedisLiveEventStream(
    IRedisSubscriber redisSubscriber,
    ILogger<RedisLiveEventStream> logger,
    LiveEventStreamOptions? options = null) : ILiveEventStream
{
    // Per-connection buffer. Overflow drops the OLDEST event, and that is correct here rather than
    // merely tolerable: these are thin stale-notifications, so a dropped one is fully subsumed by
    // any later one of the same type — the client refetches the complete state either way. The
    // alternative (waiting for room) would block the shared Redis handler and thereby stall every
    // other connection behind one slow reader.
    private const int PerConnectionCapacity = 64;

    private readonly LiveEventStreamOptions _options = options ?? new LiveEventStreamOptions();
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private readonly SemaphoreSlim _subscribeGate = new(1, 1);
    private readonly Lock _limitLock = new();
    private bool _redisSubscribed;

    public async Task<LiveEventSubscribeResult> SubscribeAsync(
        string subscriberKey,
        Func<LiveEvent, bool> filter,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureRedisSubscribedAsync(cancellationToken))
        {
            // Before a single byte of the SSE body: SubscribeAsync answering InfrastructureUnavailable
            // here is exactly the "cannot subscribe right now" contract LiveEndpoints.OpenAsync
            // renders as 503 — not a new case, just the existing degraded answer reached for a second
            // reason. Distinct from QuotaExhausted below since #42: an operator reading the log line
            // just above (missing here because EnsureRedisSubscribedAsync already logged) needs to
            // tell "Redis is down" apart from "the connection budget is full".
            return LiveEventSubscribeResult.Failed(LiveEventSubscribeStatus.InfrastructureUnavailable);
        }

        var subscription = new Subscription(subscriberKey, filter, _options, Remove);

        // Counting and inserting under one lock: two concurrent connections of the same user would
        // otherwise both read the same "n open" and both be admitted past the limit.
        lock (_limitLock)
        {
            if (_subscriptions.Count >= _options.MaxSubscriptions)
            {
                logger.LogWarning(
                    "Live-Event-Stream abgelehnt: prozessweites Limit von {Limit} Verbindungen erreicht.",
                    _options.MaxSubscriptions);
                return LiveEventSubscribeResult.Failed(LiveEventSubscribeStatus.QuotaExhausted);
            }

            var perSubscriber = _subscriptions.Values.Count(open =>
                string.Equals(open.SubscriberKey, subscriberKey, StringComparison.Ordinal));

            if (perSubscriber >= _options.MaxPerSubscriber)
            {
                logger.LogInformation(
                    "Live-Event-Stream abgelehnt: {Key} hält bereits {Limit} Verbindungen.",
                    subscriberKey, _options.MaxPerSubscriber);
                return LiveEventSubscribeResult.Failed(LiveEventSubscribeStatus.QuotaExhausted);
            }

            _subscriptions[subscription.Id] = subscription;
        }

        return LiveEventSubscribeResult.Ok(subscription);
    }

    /// <summary>
    /// Counted under the same lock <see cref="SubscribeAsync"/> uses, so the number cannot be read
    /// halfway through another connection being admitted. Deliberately not cached: the count changes
    /// on every tab open and close, and this runs only when a browser asks after a refused stream.
    /// </summary>
    public LiveStreamQuota GetQuota(string subscriberKey)
    {
        lock (_limitLock)
        {
            var openConnections = _subscriptions.Values.Count(open =>
                string.Equals(open.SubscriberKey, subscriberKey, StringComparison.Ordinal));

            return new LiveStreamQuota(
                openConnections,
                _options.MaxPerSubscriber,
                _subscriptions.Count >= _options.MaxSubscriptions);
        }
    }

    /// <summary>
    /// Returns whether the process is (now, or already) subscribed. On a Redis failure
    /// <see cref="_redisSubscribed"/> is deliberately left <c>false</c> — never set optimistically
    /// before the call succeeds — so the flag can never get "burned": the next caller, once Redis is
    /// reachable again, retries the real subscribe instead of the stream staying dead for the rest of
    /// the process's lifetime.
    /// <para>
    /// The reverse case — the latch short-circuiting <em>past</em> a Redis outage, so a client
    /// arriving mid-outage is admitted with 200 where a cold process would answer 503 — is
    /// deliberate rather than an oversight (measured 2026-09-01 while verifying #42, see
    /// <c>RedisLiveEventStreamOutageTests</c>). StackExchange.Redis restores the channel
    /// subscription itself on reconnect, and the fan-out is process-wide, so a connection admitted
    /// during the outage resumes receiving with every other one and without any client action. A
    /// 503 would be strictly worse for that client: the browser treats a non-2xx SSE handshake as
    /// terminal (<c>LiveUpdateService</c> stops at readyState CLOSED with a single visibility
    /// retry), which turns a transient outage into a permanently dead tab. Events published while
    /// Redis is down are lost either way — pub/sub buffers nothing — so the admitted connection is
    /// no worse off than the ones opened before the outage. The operator signal for a Redis outage
    /// is <c>GET /api/health</c>, which reads the worker snapshot from Redis and answers 503; it
    /// does not depend on this path.
    /// </para>
    /// </summary>
    private async Task<bool> EnsureRedisSubscribedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _redisSubscribed))
        {
            return true;
        }

        await _subscribeGate.WaitAsync(cancellationToken);
        try
        {
            if (_redisSubscribed)
            {
                return true;
            }

            try
            {
                await redisSubscriber.SubscribeAsync(LiveEvents.Channel, OnMessageAsync, cancellationToken);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                logger.LogWarning(ex, "Abonnieren von {Channel} für den Live-Event-Stream fehlgeschlagen — Redis nicht erreichbar.", LiveEvents.Channel);
                return false;
            }

            Volatile.Write(ref _redisSubscribed, true);
            return true;
        }
        finally
        {
            _subscribeGate.Release();
        }
    }

    private Task OnMessageAsync(string _, string payload)
    {
        var liveEvent = LiveEvent.TryParse(payload);
        if (liveEvent is null)
        {
            // Dropped, never fatal: anything may publish onto a Redis channel, and one malformed
            // message must not take every open browser connection down with it.
            logger.LogWarning("Ungültiges Live-Event auf {Channel} verworfen: {Payload}.", LiveEvents.Channel, payload);
            return Task.CompletedTask;
        }

        foreach (var subscription in _subscriptions.Values)
        {
            try
            {
                subscription.Offer(liveEvent);
            }
            catch (Exception ex)
            {
                // One connection's predicate throwing must not cost the remaining connections their
                // event — this loop is the only delivery path for all of them.
                logger.LogWarning(ex, "Zustellung eines Live-Events an eine Verbindung fehlgeschlagen.");
            }
        }

        return Task.CompletedTask;
    }

    private void Remove(Guid id) => _subscriptions.TryRemove(id, out _);

    private sealed class Subscription(
        string subscriberKey,
        Func<LiveEvent, bool> filter,
        LiveEventStreamOptions options,
        Action<Guid> onDisposed) : ILiveEventSubscription
    {
        private readonly Channel<LiveEvent> _channel = System.Threading.Channels.Channel.CreateBounded<LiveEvent>(
            new BoundedChannelOptions(PerConnectionCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        public Guid Id { get; } = Guid.NewGuid();

        public string SubscriberKey { get; } = subscriberKey;

        public IAsyncEnumerable<LiveEvent> Events => ReadAsync();

        public void Offer(LiveEvent liveEvent)
        {
            if (filter(liveEvent))
            {
                // TryWrite, never WriteAsync: this runs on the shared Redis handler and must return
                // immediately. With DropOldest it always succeeds unless the channel is completed.
                _channel.Writer.TryWrite(liveEvent);
            }
        }

        private async IAsyncEnumerable<LiveEvent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                LiveEvent? next;
                try
                {
                    using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idleTimeout.CancelAfter(options.HeartbeatInterval);
                    next = await _channel.Reader.ReadAsync(idleTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Nothing arrived within the heartbeat window — emit a ping instead of a real
                    // event. Heartbeats are injected here, not published to Redis.
                    next = LiveEvent.Heartbeat;
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (ChannelClosedException)
                {
                    yield break;
                }

                yield return next;
            }
        }

        public ValueTask DisposeAsync()
        {
            onDisposed(Id);
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
