using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;

namespace EmotePurge.Worker.SevenTv;

/// <summary>
/// The 7TV EventAPI transport: one WebSocket for the whole worker, one session per
/// <see cref="RunSessionAsync"/> call, driven by <see cref="SevenTvEventWorker"/>.
/// Design constraints, each answering a documented failure (docs/Untersuchung-7TV-WebSocket-2026-07-30.md):
/// subscriptions are converged only after a Hello (never before the socket exists), the receive
/// loop processes dispatches strictly sequentially (S2-8), all DB access goes through a scoped
/// ISevenTvSyncService (S3-28), and follow-up full resyncs run outside ApplyEmoteSetUpdateAsync's
/// per-channel gate because SyncChannelAsync takes the same non-reentrant gate.
/// </summary>
public class SevenTvEventClient(
    ILogger<SevenTvEventClient> logger,
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    IRedisPublisher redisPublisher,
    SevenTvSubscriptionRegistry registry) : ISevenTvEventClient
{
    private static readonly Uri EventApiUri = new("wss://events.7tv.io/v3");
    private const int DefaultHeartbeatIntervalMs = 45_000;

    // Spacing between subscribe/unsubscribe frames during bulk convergence. 7TV documents no
    // subscribe rate limit, but Twitch's undocumented JOIN limit was only discovered in production
    // — 100 ms keeps a full resubscribe of hundreds of subscriptions in the low seconds while
    // never bursting.
    private static readonly TimeSpan FrameSpacing = TimeSpan.FromMilliseconds(100);

    private readonly bool _enabled = configuration.GetValue<bool>("SevenTv:EventApi:Enabled");

    private long _lastFrameTicks;
    private long _lastDispatchTicks;
    private long _connectAttemptedTicks;
    private volatile bool _isConnected;

    // The running session's convergence signal; EnsureSubscribed/Unsubscribe nudge it. Null when
    // no session is active — desired state still lands in the registry and the next session's
    // Hello converges it.
    private Channel<bool>? _syncSignal;

    // Boot recovery has just fully synced every channel; the first session skips gap-filling.
    private bool _firstSession = true;

    // Last value 7TV reported, deliberately never cleared — see the interface comment.
    private volatile int _subscriptionLimit;

    public bool IsEnabled => _enabled;
    public int? SubscriptionLimit => _subscriptionLimit > 0 ? _subscriptionLimit : null;
    public bool IsConnected => _isConnected;
    public DateTime? LastFrameReceivedUtc => ReadUtc(ref _lastFrameTicks);
    public DateTime? LastDispatchReceivedUtc => ReadUtc(ref _lastDispatchTicks);
    public DateTime? ConnectAttemptedUtc => ReadUtc(ref _connectAttemptedTicks);

    public void EnsureSubscribed(string channelName, string emoteSetId, string? sevenTvUserId)
    {
        if (registry.SetDesired(channelName, emoteSetId, sevenTvUserId))
        {
            RequestSync();
        }
    }

    public void Unsubscribe(string channelName)
    {
        if (registry.TryRemove(channelName))
        {
            // The convergence diff handles shared sets: the subscription only leaves the socket
            // when no remaining channel desires it.
            RequestSync();
        }
    }

    public async Task<SevenTvSessionResult> RunSessionAsync(CancellationToken cancellationToken)
    {
        WriteUtc(ref _connectAttemptedTicks, DateTime.UtcNow);

        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(EventApiUri, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "7TV-EventAPI-Verbindungsaufbau fehlgeschlagen.");
            return new SevenTvSessionResult(SessionEstablished: false, SevenTvSessionEndReason.ConnectFailed);
        }

        var syncSignal = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions { SingleReader = true });
        _syncSignal = syncSignal;
        var sendPump = SendPumpAsync(socket, syncSignal.Reader, cancellationToken);

        try
        {
            return await ReceiveLoopAsync(socket, cancellationToken);
        }
        finally
        {
            _isConnected = false;
            _syncSignal = null;
            syncSignal.Writer.TryComplete();
            try
            {
                await sendPump;
            }
            catch (OperationCanceledException)
            {
                // Session or host shutdown — the pump's cancellation is the expected way out.
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "7TV-Send-Pump beim Sitzungsende abgebrochen.");
            }
        }
    }

    private async Task<SevenTvSessionResult> ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var established = false;
        var heartbeatTimeoutMs = DefaultHeartbeatIntervalMs * 3;
        var buffer = new byte[16 * 1024];

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            // The read timeout is the heartbeat watchdog: 3 missed intervals without any frame
            // mean the connection is dead even though the socket never noticed.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(heartbeatTimeoutMs);

            using var messageStream = new MemoryStream();
            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await socket.ReceiveAsync(buffer, timeoutCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return EvaluateClose(socket, established);
                    }

                    messageStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Keine 7TV-Frames seit {Seconds}s empfangen — Verbindung gilt als tot.",
                    heartbeatTimeoutMs / 1000);
                return new SevenTvSessionResult(established, SevenTvSessionEndReason.HeartbeatTimeout);
            }

            WriteUtc(ref _lastFrameTicks, DateTime.UtcNow);

            var outcome = await HandleMessageAsync(
                Encoding.UTF8.GetString(messageStream.GetBuffer(), 0, (int)messageStream.Length),
                ct);

            if (outcome.HeartbeatTimeoutMs is { } updatedTimeout)
            {
                heartbeatTimeoutMs = updatedTimeout;
                established = true;
            }

            if (outcome.EndReason is { } endReason)
            {
                return new SevenTvSessionResult(established, endReason);
            }
        }

        ct.ThrowIfCancellationRequested();
        return EvaluateClose(socket, established);
    }

    private async Task<MessageOutcome> HandleMessageAsync(string json, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Unparsebarer 7TV-Frame übersprungen: {Json}", Truncate(json));
            return new MessageOutcome();
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var opProp) || !opProp.TryGetInt32(out var op))
            {
                logger.LogWarning("7TV-Frame ohne op-Code übersprungen: {Json}", Truncate(json));
                return new MessageOutcome();
            }

            switch (op)
            {
                case 0: // Dispatch
                    await HandleDispatchAsync(root, ct);
                    return new MessageOutcome();

                case 1: // Hello
                    return HandleHello(root);

                case 2: // Heartbeat — already counted via LastFrameReceivedUtc
                    return new MessageOutcome();

                case 4: // Reconnect: the server will drop us shortly (TTL, rebalancing). The old
                        // client ignored this op and suffered the disconnect passively.
                    logger.LogInformation("7TV-Server bittet um Reconnect (op 4).");
                    return new MessageOutcome(EndReason: SevenTvSessionEndReason.ServerRequestedReconnect);

                case 5: // Ack
                    HandleAck(root);
                    return new MessageOutcome();

                case 6: // Error — the old client logged this at Debug, indistinguishable from noise.
                    logger.LogWarning("7TV-EventAPI-Fehlerframe: {Payload}", GetRawD(root));
                    return new MessageOutcome();

                case 7: // End of Stream
                    logger.LogInformation("7TV-Server beendet den Stream (op 7): {Payload}", GetRawD(root));
                    return new MessageOutcome(EndReason: SevenTvSessionEndReason.ServerRequestedReconnect);

                default:
                    logger.LogDebug("Unbehandelter 7TV-Opcode {Op} ignoriert.", op);
                    return new MessageOutcome();
            }
        }
    }

    private MessageOutcome HandleHello(JsonElement root)
    {
        var heartbeatIntervalMs = DefaultHeartbeatIntervalMs;
        string? sessionId = null;
        var subscriptionLimit = 0;

        if (root.TryGetProperty("d", out var d))
        {
            if (d.TryGetProperty("heartbeat_interval", out var interval) && interval.TryGetInt32(out var ms))
            {
                heartbeatIntervalMs = ms;
            }
            if (d.TryGetProperty("session_id", out var sid))
            {
                sessionId = sid.GetString();
            }
            if (d.TryGetProperty("subscription_limit", out var limit))
            {
                limit.TryGetInt32(out subscriptionLimit);
            }
        }

        _isConnected = true;
        if (subscriptionLimit > 0)
        {
            // Only ever overwritten by a newer stated limit, never zeroed by a Hello that omits it.
            _subscriptionLimit = subscriptionLimit;
        }

        logger.LogInformation(
            "7TV-EventAPI verbunden (Session {SessionId}, Heartbeat {Seconds}s, Subscription-Limit {Limit}).",
            sessionId, heartbeatIntervalMs / 1000, subscriptionLimit);

        var desired = registry.DesiredSubscriptionCount;
        if (subscriptionLimit > 0 && desired >= subscriptionLimit * 0.9)
        {
            logger.LogWarning(
                "7TV-Subscriptions nähern sich dem Limit: {Desired} von {Limit} — Connection-Sharding wird nötig.",
                desired, subscriptionLimit);
        }

        // A new session confirms nothing; the send pump re-subscribes everything desired.
        registry.ResetAcknowledgements();
        RequestSync();

        return new MessageOutcome(HeartbeatTimeoutMs: heartbeatIntervalMs * 3);
    }

    private void HandleAck(JsonElement root)
    {
        if (root.TryGetProperty("d", out var d) &&
            d.TryGetProperty("command", out var command) && command.GetString() == "SUBSCRIBE" &&
            d.TryGetProperty("data", out var data) &&
            data.TryGetProperty("type", out var type) && type.GetString() is { Length: > 0 } typeName &&
            data.TryGetProperty("condition", out var condition) &&
            condition.TryGetProperty("object_id", out var objectId) && objectId.GetString() is { Length: > 0 } id)
        {
            registry.MarkAcknowledged(typeName, id);
            logger.LogInformation("7TV-Subscription bestätigt: {Type} {ObjectId}.", typeName, id);
        }
    }

    private async Task HandleDispatchAsync(JsonElement root, CancellationToken ct)
    {
        try
        {
            if (!root.TryGetProperty("d", out var d) ||
                !d.TryGetProperty("type", out var typeProp) ||
                typeProp.GetString() is not { Length: > 0 } type ||
                !d.TryGetProperty("body", out var body))
            {
                return;
            }

            WriteUtc(ref _lastDispatchTicks, DateTime.UtcNow);

            if (type == "emote_set.update")
            {
                await HandleEmoteSetUpdateAsync(body, ct);
            }
            else if (type == "user.update")
            {
                await HandleUserUpdateAsync(body, ct);
            }
            else
            {
                logger.LogDebug("7TV-Dispatch {Type} ignoriert.", type);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One broken dispatch must not end the session that carries every other channel's
            // updates; the periodic resync reconciles whatever this dispatch would have applied.
            logger.LogWarning(ex, "7TV-Dispatch-Verarbeitung fehlgeschlagen, übersprungen.");
        }
    }

    private async Task HandleEmoteSetUpdateAsync(JsonElement body, CancellationToken ct)
    {
        if (!body.TryGetProperty("id", out var idProp) || idProp.GetString() is not { Length: > 0 } emoteSetId)
        {
            return;
        }

        var channels = registry.GetChannelsForEmoteSet(emoteSetId);
        if (channels.Count == 0)
        {
            // A subscription for a set no channel desires anymore (e.g. after a leave); the next
            // convergence removes it from the socket.
            logger.LogInformation("7TV-Dispatch für nicht mehr abonniertes Set {SetId} — Subscription wird abgebaut.", emoteSetId);
            RequestSync();
            return;
        }

        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(body, logger);
        if (delta.IsEmpty)
        {
            return;
        }

        // Sequential per channel: two channels sharing the set touch different rows, but staying
        // sequential keeps this loop free of any parallel-writer questions.
        foreach (var channelName in channels)
        {
            SevenTvDeltaResult result;
            using (var scope = scopeFactory.CreateScope())
            {
                var syncService = scope.ServiceProvider.GetRequiredService<ISevenTvSyncService>();
                result = await syncService.ApplyEmoteSetUpdateAsync(channelName, emoteSetId, delta, ct);
            }

            // The registry keyed this dispatch under channelName, but the row may have been renamed
            // out from under it while the call sat at the row gate — every follow-up that addresses
            // the *channel* therefore goes to result.ChannelName (issue #60). The one exception is
            // the ChannelUnknown arm below, which removes a registry entry and so has to name the
            // key the registry actually holds.
            var currentName = result.ChannelName ?? channelName;

            switch (result.Outcome)
            {
                case SevenTvDeltaOutcome.Applied:
                    // The one outcome that persisted a write — everything else changed nothing and
                    // must stay silent (open pages would refetch for nothing).
                    await redisPublisher.PublishChannelSyncedAsync(logger, currentName, ct);
                    break;

                case SevenTvDeltaOutcome.SetNotActive:
                case SevenTvDeltaOutcome.ImplausibleSkipped:
                    // Outside the gate by design — see the interface remark on ApplyEmoteSetUpdateAsync.
                    await ResyncChannelAsync(currentName, adoptResult: true, ct);
                    break;

                case SevenTvDeltaOutcome.ChannelUnknown:
                    registry.TryRemove(channelName);
                    RequestSync();
                    break;
            }
        }
    }

    private async Task HandleUserUpdateAsync(JsonElement body, CancellationToken ct)
    {
        var change = SevenTvDispatchParser.ParseUserSetChange(body, logger);
        if (change is null || !registry.TryGetChannelForUser(change.SevenTvUserId, out var channelName))
        {
            return;
        }

        logger.LogInformation(
            "7TV-Set-Wechsel für {Channel}: {OldSetId} → {NewSetId}.",
            channelName, change.OldEmoteSetId ?? "-", change.NewEmoteSetId ?? "-");

        if (change.NewEmoteSetId is { Length: > 0 } newSetId)
        {
            // The event is fresher than REST (whose cache can lag 10-30 min, SevenTV/SevenTV#81):
            // subscribe the new set immediately, then resync the content. If REST still reports
            // the old set, the event's value stays desired and the next resync tick converges.
            EnsureSubscribed(channelName, newSetId, change.SevenTvUserId);
            var result = await ResyncChannelAsync(channelName, adoptResult: false, ct);
            if (result is not null && result.EmoteSetId != newSetId)
            {
                logger.LogInformation(
                    "7TV-REST meldet für {Channel} noch Set {RestSetId}, Event nennt {EventSetId} — Event-Wert bleibt abonniert.",
                    channelName, result.EmoteSetId, newSetId);
            }
        }
        else
        {
            // No active set anymore; the stale subscription stays until the periodic resync
            // re-resolves the channel.
            await ResyncChannelAsync(channelName, adoptResult: true, ct);
        }
    }

    /// <summary>
    /// Full REST resync of one channel; never throws (logged and swallowed). Publishes
    /// <c>channel.synced</c> whenever the resync really changed something — every caller here is
    /// unattended (dispatch follow-up, set switch, reconnect gap-fill), so a no-op stays silent.
    /// </summary>
    private async Task<SevenTvSyncResult?> ResyncChannelAsync(string channelName, bool adoptResult, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var syncService = scope.ServiceProvider.GetRequiredService<ISevenTvSyncService>();
            var result = await syncService.SyncChannelAsync(channelName, ct);
            // result.ChannelName, not the name this method was called with: the sync re-reads its
            // row under the row gate, so a rename committed in between would otherwise re-register
            // the retired login in the registry — possibly after the handover's LEAVE already tore
            // that subscription down — and publish channel.synced where nobody listens (issue #60).
            if (adoptResult && result is not null)
            {
                EnsureSubscribed(result.ChannelName, result.EmoteSetId, result.SevenTvUserId);
            }

            if (result is { HasChanges: true })
            {
                await redisPublisher.PublishChannelSyncedAsync(logger, result.ChannelName, ct);
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "7TV-Resync für {Channel} nach Dispatch/Reconnect fehlgeschlagen.", channelName);
            return null;
        }
    }

    private async Task SendPumpAsync(ClientWebSocket socket, ChannelReader<bool> syncRequests, CancellationToken ct)
    {
        // What this session actually holds on the wire. Convergence towards the registry's desired
        // state is the only sender on this socket, so no send lock is needed.
        var sessionActive = new HashSet<SevenTvSubscription>();
        var gapFillPending = false;

        while (await syncRequests.WaitToReadAsync(ct))
        {
            while (syncRequests.TryRead(out _))
            {
                // Collapse bursts into one convergence pass.
            }

            await ConvergeSubscriptionsAsync(socket, sessionActive, ct);

            // Gap-filling after the first convergence of a session: dispatches missed between the
            // sessions are unrecoverable (no resume/replay), so every desired channel gets one full
            // resync. Skipped for the process's first session — boot recovery just did exactly that.
            if (!gapFillPending)
            {
                gapFillPending = true;
                await RunGapFillAsync(ct);
            }
        }
    }

    // One convergence pass: diff the session's actually-subscribed set against the registry's
    // current desired state, unsubscribe what is no longer desired, subscribe what is newly
    // desired, and log only when either happened. sessionActive is mutated in place — it is the
    // send pump's own running record of what this socket holds, shared with the loop in
    // SendPumpAsync across every pass of the session.
    private async Task ConvergeSubscriptionsAsync(
        ClientWebSocket socket, HashSet<SevenTvSubscription> sessionActive, CancellationToken ct)
    {
        var desired = registry.BuildDesiredSubscriptions().ToHashSet();

        var toRemove = sessionActive.Where(s => !desired.Contains(s)).ToList();
        foreach (var subscription in toRemove)
        {
            await SendFrameAsync(socket, op: 36, subscription, ct);
            sessionActive.Remove(subscription);
            await Task.Delay(FrameSpacing, ct);
        }

        var toAdd = desired.Where(s => !sessionActive.Contains(s)).ToList();
        foreach (var subscription in toAdd)
        {
            await SendFrameAsync(socket, op: 35, subscription, ct);
            sessionActive.Add(subscription);
            await Task.Delay(FrameSpacing, ct);
        }

        if (toAdd.Count > 0 || toRemove.Count > 0)
        {
            logger.LogInformation(
                "7TV-Subscriptions konvergiert: {Added} abonniert, {Removed} abbestellt, {Total} aktiv.",
                toAdd.Count, toRemove.Count, sessionActive.Count);
        }
    }

    // The gap-fill decision itself: the process's first session skips it (boot recovery just fully
    // synced every channel), every later session's first convergence resyncs every desired channel
    // to cover whatever dispatches were missed while no session was open.
    private async Task RunGapFillAsync(CancellationToken ct)
    {
        if (_firstSession)
        {
            _firstSession = false;
            return;
        }

        var channels = registry.DesiredChannels;
        foreach (var channelName in channels)
        {
            await ResyncChannelAsync(channelName, adoptResult: true, ct);
        }

        // Visible on purpose: during the first live TTL reconnect this ran silently
        // and was indistinguishable from not having run at all.
        logger.LogInformation("Gap-Filling nach Reconnect abgeschlossen: {Count} Channels voll resynct.", channels.Count);
    }

    private async Task SendFrameAsync(ClientWebSocket socket, int op, SevenTvSubscription subscription, CancellationToken ct)
    {
        var frame = JsonSerializer.Serialize(new
        {
            op,
            d = new { type = subscription.Type, condition = new { object_id = subscription.ObjectId } }
        });

        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The receive side will notice the dead socket and end the session; the next session
            // re-subscribes everything from the registry.
            logger.LogWarning(ex, "Senden des 7TV-Frames (op {Op}, {Type} {ObjectId}) fehlgeschlagen.", op, subscription.Type, subscription.ObjectId);
        }
    }

    private SevenTvSessionResult EvaluateClose(ClientWebSocket socket, bool established)
    {
        var closeCode = socket.CloseStatus is { } status ? (int)status : (int?)null;
        var reason = closeCode == 4009
            ? SevenTvSessionEndReason.AlreadySubscribed
            : SevenTvSessionEndReason.RemoteClosed;

        logger.LogInformation(
            "7TV-EventAPI-Verbindung geschlossen (Code {Code}, {Description}).",
            closeCode, socket.CloseStatusDescription);
        return new SevenTvSessionResult(established, reason, closeCode);
    }

    private void RequestSync() => _syncSignal?.Writer.TryWrite(true);

    private static DateTime? ReadUtc(ref long ticks)
    {
        var value = Interlocked.Read(ref ticks);
        return value == 0 ? null : new DateTime(value, DateTimeKind.Utc);
    }

    private static void WriteUtc(ref long ticks, DateTime value) =>
        Interlocked.Exchange(ref ticks, value.Ticks);

    private static string GetRawD(JsonElement root) =>
        root.TryGetProperty("d", out var d) ? Truncate(d.GetRawText()) : "-";

    private static string Truncate(string raw) => raw.Length <= 500 ? raw : raw[..500] + "…";

    /// <summary>What a single received frame told us: a new heartbeat interval, a reason to end the
    /// session, or neither.</summary>
    private readonly record struct MessageOutcome(
        int? HeartbeatTimeoutMs = null,
        SevenTvSessionEndReason? EndReason = null);
}
