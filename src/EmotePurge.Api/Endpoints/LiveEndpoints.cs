using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// The only server -> browser push channel of the app: <c>text/event-stream</c> over the existing
/// cookie session, so every endpoint filter and the whole auth pipeline apply unchanged (a SignalR
/// hub would have needed its own). Both streams share <see cref="OpenAsync"/>; they differ only in
/// which events they let through.
/// </summary>
public static class LiveEndpoints
{
    /// <summary>
    /// Upper bound on one connection's lifetime. Session revocation only runs at the handshake
    /// (OnValidatePrincipal), so an open stream would otherwise outlive a revoked session for as
    /// long as the browser kept it. The browser's automatic EventSource reconnect goes through the
    /// full auth pipeline again, so this costs nothing but bounds the revocation window at 10 min.
    /// </summary>
    private static readonly TimeSpan MaxConnectionLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// <c>Retry-After</c> handed out with the 429 for an exhausted live-stream quota (issue #42).
    /// A fixed heuristic, not a measured value: unlike <c>ResyncCooldownActive</c>'s cooldown timer,
    /// neither the process-wide nor the per-login connection limit has a natural expiry to report —
    /// a slot frees the moment a browser tab closes or a stream hits <see cref="MaxConnectionLifetime"/>.
    /// Lowered from 30 s to 10 s alongside issue #128's keepalive fix: an abandoned stream's slot used
    /// to sit held for up to 30 s (Infrastructure's heartbeat cadence) before a proxy in front of it
    /// ever noticed the browser had cancelled, and only that noticing releases the slot. With
    /// <see cref="StreamAsync"/> now writing within one <see cref="LiveStreamKeepaliveOptions"/>
    /// interval (default 5 s) of a stream going idle, a slot exhausted right now is very likely free
    /// again well inside 10 s — the number still balances the same two things: long enough that a
    /// reconnect storm does not retry every second, short enough that the common case (a slot freed
    /// by the keepalive noticing an abandoned tab) is usable again promptly.
    /// </summary>
    private const int LiveStreamQuotaRetryAfterSeconds = 10;

    public static void MapLiveEndpoints(this WebApplication app)
    {
        // Deliberately no rate-limiting policy on either stream — nor on the admin stream that
        // OpenAdminAsync backs. Every policy runs with QueueLimit = 0, and an Api restart makes every
        // open browser reconnect at once; a 429 there is final for an EventSource, which stops
        // retrying. The connection limits inside ILiveEventStream are the protection instead, and they
        // bound the right quantity: concurrently held streams, not requests per minute.
        app.MapGet("/api/channels/{channelName}/live", (
            string channelName,
            HttpContext httpContext,
            ILiveEventStream liveEventStream,
            LiveStreamKeepaliveOptions keepaliveOptions,
            LiveStreamConnectionRegistry connectionRegistry,
            CancellationToken ct) =>
        {
            // Only "logged in and a well-formed name" — no usage-stats or vote filter. The events
            // are pure "something changed" pings carrying no channel data; the real authorization
            // boundary sits on the refetch each client then performs.
            var normalized = ChannelName.Normalize(channelName);
            return OpenAsync(
                httpContext,
                liveEventStream,
                keepaliveOptions,
                connectionRegistry,
                liveEvent => LiveEvents.ChannelTypes.Contains(liveEvent.Type)
                    && string.Equals(liveEvent.Channel, normalized, StringComparison.Ordinal),
                ct);
        })
        .RequireAuthorization()
        .AddEndpointFilter<ChannelNameValidationFilter>();

        // The overview's stream: any logged-in user may listen. Cross-channel on purpose — the
        // events are rare (a real transition of a tracked channel) and carry only the channel
        // name; the refetch through GET /api/channels/mine stays the authorization boundary.
        // The competing route is GET /api/channels/{channelName} — same segment count, so routing
        // decides by precedence, and a literal segment always outranks a parameter.
        app.MapGet("/api/channels/live-events", (
            HttpContext httpContext,
            ILiveEventStream liveEventStream,
            LiveStreamKeepaliveOptions keepaliveOptions,
            LiveStreamConnectionRegistry connectionRegistry,
            CancellationToken ct) =>
            OpenAsync(
                httpContext,
                liveEventStream,
                keepaliveOptions,
                connectionRegistry,
                liveEvent => string.Equals(liveEvent.Type, LiveEvents.LiveChanged, StringComparison.Ordinal),
                ct))
        .RequireAuthorization();

        // The answer to a question the browser cannot ask any other way (issue #42, stage 2).
        // EventSource surfaces neither status code nor body to onerror — a refused tab sees only
        // readyState CLOSED — so "the infrastructure is away" and "you have six streams open" are
        // indistinguishable at exactly the moment the page goes quiet. This route lets the client ask
        // afterwards, and it is the smaller of the two options the stage-1 write-up named: the
        // alternative was replacing EventSource with a fetch-based SSE reader, which would have put
        // the whole reconnect behaviour — the browser's job today — into our own code for the sake of
        // one hint.
        //
        // Not itself a stream and not rate-limited: it is called at most once per fatal close, costs
        // an in-memory count over at most MaxSubscriptions entries, and putting it behind a policy
        // would risk 429-ing the very request that exists to explain a 429.
        app.MapGet("/api/live/status", (HttpContext httpContext, ILiveEventStream liveEventStream) =>
        {
            var quota = liveEventStream.GetQuota(SubscriberKeyOf(httpContext));
            return Results.Ok(new
            {
                openConnections = quota.OpenConnections,
                maxPerSubscriber = quota.MaxPerSubscriber,
                // The client renders "close a few tabs" only for this one. A refusal while the
                // per-login budget still has room was the process ceiling or Redis, and neither is
                // anything the user can act on — see LiveStreamQuota.ProcessLimitReached.
                perSubscriberLimitReached = quota.PerSubscriberLimitReached,
            });
        })
        .RequireAuthorization();

        // Issue #128, the other half of the keepalive fix: lets the client tell the Api directly that
        // it is done with a stream, instead of the slot release depending on a proxy chain noticing a
        // cancelled request on its own. See LiveStreamConnectionRegistry for the connection-id
        // lifecycle and StreamAsync's doc comment for where the id is handed out.
        //
        // The route constraint keeps a malformed id from ever reaching the handler — routing itself
        // answers with whatever it answers for no match (currently a bare 404), which is fine here:
        // nothing about this route needs the language-neutral errorCode contract, because there is
        // nothing for a legitimate caller to act on differently.
        //
        // RequireRateLimiting(Bookkeeping): a mutation against purely in-process state, no downstream
        // cost at all — even cheaper than the "writes against our own database" shape Bookkeeping was
        // named for, and a normal SPA route change can legitimately fire this a few times in a row.
        // No dedicated policy: this is not shaped like ChannelResync (an unconditional external call)
        // or Voting (partitioned per session), and giving it its own budget would buy nothing over
        // reusing an existing one.
        app.MapDelete("/api/live/connections/{connectionId:regex(^[0-9a-f]{{32}}$)}", (
            string connectionId,
            HttpContext httpContext,
            LiveStreamConnectionRegistry connectionRegistry) =>
        {
            // Always 204 — the caller's own id, someone else's id, an unknown id, or one that already
            // ended — so a connection id can never be probed for existence by watching the status
            // code. TryRelease's bool return exists only for LiveStreamConnectionRegistry's own tests.
            connectionRegistry.TryRelease(connectionId, SubscriberKeyOf(httpContext));
            return Results.NoContent();
        })
        .RequireAuthorization()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);
    }

    /// <summary>
    /// The admin stream. Registered from <see cref="AdminEndpoints"/> so it sits inside the
    /// <c>/api/admin</c> group and inherits its GlobalAdminAuthorizationFilter by construction.
    /// </summary>
    internal static Task<IResult> OpenAdminAsync(
        HttpContext httpContext,
        ILiveEventStream liveEventStream,
        LiveStreamKeepaliveOptions keepaliveOptions,
        LiveStreamConnectionRegistry connectionRegistry,
        CancellationToken ct)
        => OpenAsync(
            httpContext,
            liveEventStream,
            keepaliveOptions,
            connectionRegistry,
            liveEvent => LiveEvents.AdminTypes.Contains(liveEvent.Type),
            ct);

    private static async Task<IResult> OpenAsync(
        HttpContext httpContext,
        ILiveEventStream liveEventStream,
        LiveStreamKeepaliveOptions keepaliveOptions,
        LiveStreamConnectionRegistry connectionRegistry,
        Func<LiveEvent, bool> filter,
        CancellationToken ct)
    {
        var subscriberKey = SubscriberKeyOf(httpContext);
        var result = await liveEventStream.SubscribeAsync(subscriberKey, filter, ct);
        if (result.Status != LiveEventSubscribeStatus.Ok)
        {
            // Before a single byte of the body: once an SSE response has started there is no status
            // code left to send, which is why SubscribeAsync answers eagerly instead of failing
            // lazily during enumeration. Both bodies carry a language-neutral errorCode (Regel 7) —
            // before #42 this was a single bare 503 for either cause.
            return result.Status switch
            {
                LiveEventSubscribeStatus.InfrastructureUnavailable => Results.Json(
                    new { errorCode = ApiErrorCodes.LiveStreamUnavailable },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                LiveEventSubscribeStatus.QuotaExhausted => QuotaExhaustedResult(httpContext),
                // result is a local variable, not a method parameter — ArgumentOutOfRangeException
                // would misrepresent this as a caller error. LiveEventSubscribeStatus has no enum
                // validation of its own, so this branch is genuinely reachable (an unhandled member
                // added later, say), which rules out UnreachableException too.
                _ => throw new InvalidOperationException(
                    $"Unexpected {nameof(LiveEventSubscribeStatus)} value: {result.Status}.")
            };
        }

        var subscription = result.Subscription!;

        // Deliberately in OnStarting and not set directly here: ServerSentEventsResult writes its own
        // Cache-Control ("no-cache,no-store") while executing, so anything assigned before returning
        // the result is silently overwritten — verified against the running server, not assumed.
        // OnStarting runs when the response actually starts, i.e. last.
        httpContext.Response.OnStarting(() =>
        {
            var headers = httpContext.Response.Headers;
            // no-transform on top of no-cache: a transforming proxy is free to buffer and recode a
            // response body, which for an event stream means it arrives in blocks or not at all.
            headers.CacheControl = "no-cache, no-transform";
            // nginx buffers proxied responses by default and would hold frames back until its buffer
            // fills; this is the documented opt-out.
            headers["X-Accel-Buffering"] = "no";
            return Task.CompletedTask;
        });

        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
        lifetime.CancelAfter(MaxConnectionLifetime);

        // Registered right away, before a single frame is written: DELETE /api/live/connections/{id}
        // (issue #128) must be able to find this connection as soon as the client has read the first
        // frame's id — see LiveStreamConnectionRegistry for the rest of the lifecycle.
        var connectionId = connectionRegistry.Register(subscriberKey, lifetime);

        return TypedResults.ServerSentEvents(StreamAsync(subscription, lifetime, keepaliveOptions, connectionId, lifetime.Token));
    }

    /// <summary>
    /// Per login, not per connection: the limit is meant to bound how many streams one account can
    /// pin, and every route that uses this requires authentication. Shared by the subscribe path and
    /// the status probe so the two can never count against different keys.
    /// </summary>
    private static string SubscriberKeyOf(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

    /// <summary>
    /// The 429 for an exhausted live-stream quota — process-wide or per-login, collapsed onto one
    /// code (see <see cref="LiveEventSubscribeStatus.QuotaExhausted"/>). Mirrors the
    /// <c>ResyncCooldownActive</c> shape in <c>ChannelEndpoints</c>: header and body carry the same
    /// value so a client reading either lands on the same number.
    /// </summary>
    private static IResult QuotaExhaustedResult(HttpContext httpContext)
    {
        httpContext.Response.Headers.RetryAfter =
            LiveStreamQuotaRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(
            new { errorCode = ApiErrorCodes.LiveStreamQuotaExhausted, retryAfterSeconds = LiveStreamQuotaRetryAfterSeconds },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// Frames the events. The SSE event type stays the default (<c>message</c>) on purpose: the
    /// discriminator lives inside the JSON body, so a client's <c>onmessage</c> sees every event and
    /// can ignore types it does not know — with a named event type an unknown type would instead be
    /// silently undeliverable.
    /// <para>
    /// Two additions beyond framing, both from issue #128: a proxy chain (Cloudflare -&gt; nginx) only
    /// propagates a browser's cancel to us when we next write, and until this fix nothing was written
    /// until Infrastructure's own 15 s heartbeat — so an abandoned stream held its connection-budget
    /// slot for up to that long. First, the very first yielded item is an immediate heartbeat: per
    /// <c>SseFormatter.WriteAsync</c>'s documented behaviour ("the destination stream is flushed
    /// after each event is written"), yielding gets headers and the first bytes onto the wire the
    /// instant the stream opens, rather than waiting on the first real event or the first
    /// Infrastructure heartbeat. Second, an Api-level keepalive fires whenever the inner subscription
    /// stays quiet for <see cref="LiveStreamKeepaliveOptions.KeepaliveInterval"/> (default 5 s, far
    /// shorter than Infrastructure's 15 s — that one exists to keep proxies from timing out an idle
    /// connection, not for slot-release speed) — so an abandoned stream now writes, and therefore
    /// flushes, within one interval of being abandoned instead of up to 15 s later.
    /// </para>
    /// <para>
    /// A third addition from issue #128's second half: that same first frame also carries an SSE
    /// <c>id:</c> field — <paramref name="connectionId"/>, handed out by
    /// <see cref="LiveStreamConnectionRegistry.Register"/> — so the client can later ask
    /// <c>DELETE /api/live/connections/{connectionId}</c> to end this exact stream on its own
    /// initiative rather than merely aborting the request and hoping a proxy notices promptly. No
    /// later frame carries an id: only the very first one identifies the connection.
    /// </para>
    /// </summary>
    internal static async IAsyncEnumerable<SseItem<string>> StreamAsync(
        ILiveEventSubscription subscription,
        CancellationTokenSource lifetime,
        LiveStreamKeepaliveOptions keepaliveOptions,
        string connectionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await using (subscription)
            {
                // Immediate first frame — see the class doc above. Yielded from inside the
                // await-using block (not before it) so that disposing the enumerator right after this
                // one item — the client-abort case, before a single real event or keepalive tick — still
                // runs the subscription's disposal through the ordinary await-using unwind, exactly as
                // for every later exit path. EventId is init-only, hence the object initializer rather
                // than a constructor argument — SseItem<T> only takes data/eventType there.
                yield return new SseItem<string>(LiveEvent.Heartbeat.Serialize()) { EventId = connectionId };

                var enumerator = subscription.Events.GetAsyncEnumerator(ct);

                // Declared outside the try so the finally below can see whether a MoveNextAsync is
                // still in flight when we get there — see the finally's own comment for why that
                // matters.
                Task<bool>? pendingMoveNext = null;
                try
                {
                    // The pending MoveNextAsync is kept across loop iterations and raced against a
                    // keepalive delay rather than re-issued on every tick: IAsyncEnumerator forbids
                    // overlapping MoveNextAsync calls, and restarting it after a delay "wins" the race
                    // would do exactly that on the next loop pass. AsTask() is called (and the result
                    // kept, never re-awaited as a ValueTask) because a ValueTask may only be consumed
                    // once, and this same pending task is awaited again below.
                    pendingMoveNext = enumerator.MoveNextAsync().AsTask();
                    while (true)
                    {
                        // A standalone CTS, not linked to ct: cancellation of the stream itself
                        // (client abort, MaxConnectionLifetime) is observed by pendingMoveNext instead
                        // — the subscription's Events enumerator already reacts to ct and ends the
                        // enumeration cleanly (see RedisLiveEventStream.ReadAsync) — so this delay only
                        // ever needs cancelling by us, when the real event side of the race wins.
                        using var keepaliveCts = new CancellationTokenSource();
                        var keepaliveDelay = Task.Delay(keepaliveOptions.KeepaliveInterval, keepaliveCts.Token);

                        var winner = await Task.WhenAny(pendingMoveNext, keepaliveDelay).ConfigureAwait(false);

                        if (winner == keepaliveDelay)
                        {
                            yield return new SseItem<string>(LiveEvent.Heartbeat.Serialize());
                            continue;
                        }

                        // pendingMoveNext won: cancel the now-pointless delay so its timer is released
                        // immediately rather than firing later, and observe the resulting
                        // OperationCanceledException right here so it can never surface as an
                        // unobserved task exception once this loop iteration lets the task go.
                        keepaliveCts.Cancel();
                        try
                        {
                            await keepaliveDelay;
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected: the delay lost the race and was cancelled deliberately.
                        }

                        if (!await pendingMoveNext)
                        {
                            yield break;
                        }

                        // SseItem<string> rather than SseItem<LiveEvent>: strings are written verbatim,
                        // so the bytes on the wire are exactly LiveEvent.Serialize() and cannot drift
                        // with the host's JSON options.
                        yield return new SseItem<string>(enumerator.Current.Serialize());
                        pendingMoveNext = enumerator.MoveNextAsync().AsTask();
                    }
                }
                finally
                {
                    // If the consumer stopped enumerating right after a keepalive yield — the write of
                    // that heartbeat failing because the client is gone is exactly how SseFormatter
                    // learns of an abort, and it disposes our iterator in response — pendingMoveNext is
                    // still in flight at that point: the keepalive branch above yields without
                    // reassigning it. Disposing a compiler-generated async-iterator enumerator
                    // (Events => ReadAsync() in RedisLiveEventStream) while a MoveNextAsync on it is
                    // still pending throws, which would replace the real "client aborted" story with an
                    // unrelated exception in the logs. Cancelling the stream's own token first is safe —
                    // the stream is ending here regardless of why — and unblocks whatever ReadAsync is
                    // awaiting (it already reacts to this same token), so the pending call completes on
                    // its own instead of DisposeAsync running into it.
                    if (pendingMoveNext is not null)
                    {
                        lifetime.Cancel();
                        try
                        {
                            await pendingMoveNext;
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected: this is exactly the cancellation just requested above.
                        }
                    }

                    await enumerator.DisposeAsync();
                }
            }
        }
        finally
        {
            // Cancel before dispose, unconditionally — not just belt-and-suspenders alongside the
            // inner finally's own lifetime.Cancel() above. If the consumer disposes the enumerator
            // while it is suspended at the very first yield (the id-carrying first frame), execution
            // never reaches the inner try/finally at all — that Cancel() call is simply never made —
            // so without this one, lifetime would go straight to Dispose() uncancelled. Since
            // LiveStreamConnectionRegistry.Register wires its cleanup to this token being cancelled,
            // not to the CTS being disposed, that would leave the registry entry behind forever: an
            // unbounded per-process leak, and an id that stays "releasable" against an already-disposed
            // CTS. Cancelling an already-cancelled CTS (the ordinary case, via the inner finally) is a
            // documented no-op, so this is free on every other exit path.
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }
}

/// <summary>
/// The Api-level idle keepalive <see cref="LiveEndpoints.StreamAsync"/> uses to make an abandoned SSE
/// stream write — and therefore flush, and therefore let a proxy chain notice the client cancelled —
/// promptly (issue #128). A plain DI singleton rather than a configuration-bound
/// <c>IOptions&lt;RateLimitingOptions&gt;</c>-style type: nothing here needs an operator-tunable value
/// or an admin-visible snapshot, only a seam <c>WebApplicationFactory</c> tests can replace with a
/// short interval without a configuration round-trip — the same reasoning
/// <c>LiveEventStreamOptions</c> already uses on the Infrastructure side for its own (much longer,
/// and unrelated) heartbeat.
/// </summary>
public sealed class LiveStreamKeepaliveOptions
{
    /// <summary>
    /// How long <see cref="LiveEndpoints.StreamAsync"/> waits for a real event before writing a
    /// heartbeat of its own. Deliberately far shorter than Infrastructure's 15 s
    /// <c>LiveEventStreamOptions.HeartbeatInterval</c>, which exists to keep a proxy from timing out
    /// an idle connection — a different concern from releasing a connection-budget slot quickly once
    /// its stream has been abandoned.
    /// </summary>
    public TimeSpan KeepaliveInterval { get; init; } = TimeSpan.FromSeconds(5);
}
