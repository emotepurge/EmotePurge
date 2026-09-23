using System.Collections.Concurrent;
using EmotePurge.Core.Chat;
using EmotePurge.Core.Matching;
using EmotePurge.Core.Services;
using TwitchLib.Client;
using TwitchLib.Client.Enums;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Models;

namespace EmotePurge.Worker;

/// <summary>
/// The transport half of the Twitch connection (issue #68): it holds exactly one
/// <see cref="TwitchClient"/> at a time, wires and unwires it, stamps frames, tracks the desired
/// channels and paces every JOIN we issue. It makes no timing decision of its own — when and how
/// often to rebuild belongs to <see cref="TwitchConnectionWatchdog"/> and
/// <see cref="TwitchReconnectBackoffPolicy"/>, and TwitchLib's event handlers here only ever record
/// state and deposit a signal (design decision E1).
/// </summary>
public class TwitchChatManager(
    ILogger<TwitchChatManager> logger,
    ILoggerFactory loggerFactory,
    IEmoteMatchCache emoteMatchCache,
    IEmoteUsageCounter usageCounter,
    IBotChatterDetector botChatterDetector,
    WorkerStats stats) : ITwitchChatManager
{
    // Twitch permits 20 joins per 10 seconds on a non-verified connection, and TwitchLib paces them
    // not at all: JOINs bypass its ThrottlingService (that one only covers chat messages) and its
    // queue advances as fast as confirmations arrive — 126–231ms per channel, measured against
    // Twitch on 2026-09-08. Twenty channels back to back therefore sit on or above the limit, which
    // costs the excess channels their join until EnsureJoinedAsync retries them. 600ms keeps every
    // join path at roughly 16 per 10 seconds.
    //
    // Since the client is built with NoReconnectionPolicy there is no unthrottled path left:
    // TwitchLib never rejoins on its own any more, every JOIN this process issues goes through the
    // gate below, and the rejoin after a rebuild is just another caller of it.
    private static readonly TimeSpan MinIntervalBetweenJoins = TimeSpan.FromMilliseconds(600);

    // How long one JoinChannelAsync may take before the round gives up on it. The call is not the
    // JOIN's confirmation, only its send, and on a healthy connection it returns in single-digit
    // milliseconds — but it can block indefinitely, for the same reason ConnectAttemptTimeout
    // exists: QueueingJoinCheckAsync ends in ClientBase.SendAsync, which awaits the send semaphore
    // and then ClientWebSocket.SendAsync on base.Token, and nothing cancels that token while this
    // loop is the thing that would have to cancel it. Ten seconds is three orders of magnitude
    // above the normal case and still well inside one watchdog tick.
    private static readonly TimeSpan JoinSendTimeout = TimeSpan.FromSeconds(10);

    // The whole-attempt bound on ConnectAsync. TwitchLib's own TimeOutEstablishConnection (15s)
    // covers only ClientWebSocket.ConnectAsync inside WebSocketClient.ConnectClientAsync; the IRC
    // handshake that follows is sent from ClientBase.OpenPrivateAsync's RaiseConnected, through
    // three to six ClientBase.SendAsync calls that await a semaphore and then ClientWebSocket.
    // SendAsync on base.Token — and base.Token is cancelled only by ClosePrivateAsync, which is
    // exactly what this loop cannot reach while it is blocked here. 30s is that 15s socket bound
    // plus room for the handshake sends, and far above the ~0.9s a healthy attempt measured.
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(30);

    // How long an opened socket may stay silent before we call the attempt failed. TwitchLib sends
    // the IRC handshake as soon as the WebSocket is up and Twitch answers "004" within a
    // round-trip, so ten seconds is two orders of magnitude of headroom, not a guess.
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    // How long the round waits for Twitch to confirm the JOINs it just sent — TwitchLib's own join
    // timeout is 5s, so anything unconfirmed after that will not arrive at all and belongs in the
    // "K offen" figure rather than in a longer wait.
    private static readonly TimeSpan JoinConfirmationTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan JoinConfirmationPollInterval = TimeSpan.FromMilliseconds(150);

    // Desired channels, not confirmed ones — the value records whether Twitch confirmed the JOIN.
    // Tracking intent instead of success is what makes a failed join retryable (see TryJoinAsync
    // and EnsureJoinedAsync).
    private readonly ConcurrentDictionary<string, bool> _desiredChannels = new(StringComparer.OrdinalIgnoreCase);

    // Per channel what _lastMessageReceivedUtcTicks is for the connection as a whole. The global
    // one answers "is the socket alive", this one answers "is *this* channel alive" — the question
    // a support case actually asks, and one the global figure hides: a single busy channel keeps it
    // fresh while thirty others sit silently unjoined. Same OrdinalIgnoreCase comparer as
    // _desiredChannels above, or `HandOfBlood` and `handofblood` become two roster rows.
    private readonly ConcurrentDictionary<string, long> _lastMessageByChannelTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _joinGate = new(1, 1);
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    // Guards the identity of the current client against the handlers that act on it. An identity
    // check on its own protects nothing: a handler can be preempted between passing it and writing,
    // and resume after a rebuild has installed the successor — check and state change therefore
    // share one critical section, held under the same latch as the swap itself.
    //
    // Deliberately *not* _reconnectLock. ReconnectOnceAsync holds that one while it waits for the
    // "004", and that "004" arrives through OnConnected: a handler taking the same latch would wait
    // for the loop that is waiting for the handler. This one is its own, and it bounds exactly two
    // things against each other — (a) the swap of _client, its generation, its wiring and its
    // handshake source in ReconnectOnceAsync, and (b) per handler the unit of "am I still the
    // current client" plus the state change that answer authorises.
    //
    // Two properties keep it deadlock-free, and both are checkable by reading this file: no `await`
    // runs inside any of its critical sections (grep for `lock (_clientStateGate)` — every one of
    // them is straight-line field work), and nothing that holds it ever acquires _reconnectLock, so
    // it is only ever the *inner* latch (ReconnectOnceAsync takes _reconnectLock first, then this)
    // and there is no cycle to deadlock on. The only other latch taken from inside it is the signal
    // slot's own, always in that direction and never the reverse.
    private readonly Lock _clientStateGate = new();

    // The one place a loss becomes a rebuild. Deliberately not injected: it is this manager's own
    // state, and the loop reaches it only through WaitForReconnectRequestAsync.
    private readonly TwitchReconnectSignalSlot _signalSlot = new();

    private volatile TwitchClient _client = CreateClient(loggerFactory);

    // Counted from 1 for the boot client and never reset: it is both the key of the signal slot's
    // generation rule and the rebuild id in every log line about a connection.
    private int _clientGeneration = 1;
    private int _connected;
    private long _lastMessageReceivedUtcTicks;
    private long _lastFrameReceivedUtcTicks;
    private long _connectAttemptedUtcTicks;

    // When the current session's handshake ("004") arrived, or 0 when there is no session. Set by
    // OnConnected, cleared when a rebuild attempt starts — which is what makes "no handshake at
    // all" (a failed attempt) distinguishable from "a session that ended" for the backoff policy.
    private long _sessionStartedUtcTicks;
    private DateTime _lastJoinIssuedUtc = DateTime.MinValue;

    // Completed by OnConnected of the client the current attempt is opening. Recreated per attempt,
    // so a late "004" of an abandoned client can never satisfy the wait of a newer one.
    private TaskCompletionSource? _handshakeSource;

    // Detaches the handlers currently registered on _client, through the very delegate instances
    // WireUpClient registered — they are closures, and a freshly built lambda would remove none of
    // them (see UnwireClient). Always the current client's: set when one is wired, replaced when the
    // next one is, and both happen under _reconnectLock (or once at boot).
    private Action? _unwireCurrentClient;

    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    public DateTime? LastMessageReceivedUtc => ReadTimestamp(ref _lastMessageReceivedUtcTicks);

    public DateTime? LastFrameReceivedUtc => ReadTimestamp(ref _lastFrameReceivedUtcTicks);

    public DateTime? ConnectAttemptedUtc => ReadTimestamp(ref _connectAttemptedUtcTicks);

    public void Initialize()
    {
        // Under the gate like every other wiring step, although nothing races with it at boot:
        // _unwireCurrentClient and _client are the gate's state, and one unguarded writer is how
        // an invariant quietly stops being one.
        lock (_clientStateGate)
        {
            WireUpClient(_client, Volatile.Read(ref _clientGeneration));
        }
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        // One attempt, no background retry loop: if it fails, the signal below is what the rebuild
        // loop finds the first time it waits, and the loop owns every attempt from then on. Boot
        // recovery runs either way — joins record their intent and the first successful rebuild
        // rejoins them.
        try
        {
            var outcome = await OpenAndAwaitHandshakeAsync(_client, Volatile.Read(ref _clientGeneration), ct);
            if (outcome.HandshakeCompleted)
            {
                return;
            }

            logger.LogWarning(
                "Initialer TwitchClient-Connect fehlgeschlagen ({Reason}) — der Wiederaufbau übernimmt.",
                outcome.FailureReason);
            RequestReconnect(
                TwitchSessionEndReason.InitialConnectFailed,
                $"Boot-Connect fehlgeschlagen ({outcome.FailureReason}).");
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down while we were still opening: nothing to signal, nothing to wait
            // for. The half-open client dies with the process.
        }
    }

    public async Task JoinChannelAsync(string channelName)
    {
        // Record the intent *before* attempting it. Tracking only confirmed joins meant a channel
        // whose JOIN failed — e.g. because the client happened to be mid-rebuild — was never
        // retried by any reconnect: database and match cache looked correct while usage data
        // stayed empty forever, with no signal anywhere.
        _desiredChannels.AddOrUpdate(channelName, false, (_, confirmed) => confirmed);
        await TryJoinAsync(channelName, TwitchJoinSource.Command, CancellationToken.None);
    }

    public async Task EnsureJoinedAsync(string channelName)
    {
        // Safety net driven by the periodic resync, which enumerates all active channels anyway:
        // covers lost Redis commands, joins that failed during boot recovery, and joins Twitch
        // never confirmed.
        if (_desiredChannels.TryGetValue(channelName, out var confirmed) && confirmed)
        {
            return;
        }

        _desiredChannels.AddOrUpdate(channelName, false, (_, stored) => stored);
        await TryJoinAsync(channelName, TwitchJoinSource.ConvergenceNet, CancellationToken.None);
    }

    public async Task LeaveChannelAsync(string channelName)
    {
        // Drop the intent first, so nothing rejoins this channel afterwards even if the leave
        // itself fails or the client is currently disconnected. TryJoinAsync re-checks the intent
        // after its gate, so a leave that lands mid-rejoin also stops the JOIN that round would
        // otherwise still send.
        _desiredChannels.TryRemove(channelName, out _);
        // A left channel's last message time would otherwise outlive it for the process lifetime and
        // resurrect as a stale timestamp if the channel is ever rejoined.
        _lastMessageByChannelTicks.TryRemove(channelName, out _);

        if (!IsConnected)
        {
            logger.LogWarning(
                "Leave für {Channel} übersprungen — TwitchClient ist derzeit nicht verbunden.",
                channelName);
            return;
        }

        try
        {
            await _client.LeaveChannelAsync(channelName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Leave fehlgeschlagen für {Channel}.", channelName);
        }
    }

    public IReadOnlyList<TwitchRosterEntry> GetRoster()
    {
        // Built from the desired set, not from the message dictionary: a channel we want and never
        // got a single message from is exactly the row worth showing, and it exists only here.
        var roster = new List<TwitchRosterEntry>(_desiredChannels.Count);
        foreach (var (channelName, joinConfirmed) in _desiredChannels)
        {
            var ticks = _lastMessageByChannelTicks.TryGetValue(channelName, out var stored) ? stored : 0;
            roster.Add(new TwitchRosterEntry(
                channelName,
                joinConfirmed,
                ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc)));
        }

        roster.Sort(static (left, right) => string.CompareOrdinal(left.ChannelName, right.ChannelName));
        return roster;
    }

    public void RequestReconnect(TwitchSessionEndReason reason, string detail)
    {
        // The tick's and the debug trigger's way in. Under the gate so that reading the current
        // generation and offering it are one step: a rebuild starting in between would condemn the
        // generation this stamp names, and the signal would be discarded although the caller meant
        // the client that is current *now*.
        lock (_clientStateGate)
        {
            OfferSignal(Volatile.Read(ref _clientGeneration), reason, detail);
        }
    }

    public Task<TwitchReconnectRequest> WaitForReconnectRequestAsync(CancellationToken ct) =>
        _signalSlot.TakeAsync(ct);

    public async Task<TwitchConnectOutcome> ReconnectOnceAsync(CancellationToken ct)
    {
        await _reconnectLock.WaitAsync(ct);
        try
        {
            TwitchClient oldClient;
            int oldGeneration;
            TwitchClient newClient;
            int generation;

            // Steps 1 and 3 are one indivisible section, not two: in between, _client still *is*
            // the old one, so a handler slipping through there would pass its identity check and
            // write into state that is being torn down. Holding the gate across both also parks
            // every handler that entered before the detach until the swap is complete — when they
            // resume, they see the successor, fail their check and discard themselves.
            lock (_clientStateGate)
            {
                oldClient = _client;
                oldGeneration = Volatile.Read(ref _clientGeneration);

                // Step 1 — detach the old client. From here on nothing it raises reaches us.
                UnwireClient();
                SetConnected(false);
                Interlocked.Exchange(ref _sessionStartedUtcTicks, 0);
                MarkAllChannelsUnconfirmed();

                // Condemn the generation being retired — every attempt, successful or failed, and
                // not only the one whose signal the loop took out of the slot. A failed attempt's
                // client stays current and wired for the whole backoff; a late "004" can still
                // start its session, and the loss it reports afterwards would be deposited like a
                // fresh one — tearing a *healthy* successor down one round later (see
                // TwitchReconnectSignalSlot.CondemnGeneration for the full sequence).
                _signalSlot.CondemnGeneration(oldGeneration);

                // Step 3 — a fresh object, never OpenAsync() on the old one: a reused client drags
                // along TwitchLib's half-emptied join queue and its _currentlyJoiningChannels flag,
                // while a new one has an empty queue and exactly one read loop (E3).
                newClient = CreateClient(loggerFactory);
                // The generation is drawn before wiring, because the handlers capture it
                // (WireUpClient) rather than reading it when they fire.
                generation = Interlocked.Increment(ref _clientGeneration);
                WireUpClient(newClient, generation);
                _client = newClient;
            }

            // Step 2 — clean the old one up in the background rather than awaiting it (E4). Its
            // socket is already gone, and DisconnectAsync alone carries ~1.9s of built-in waits
            // that would be pure counting gap. Outside the gate on purpose: it hands work to
            // another task and has no business widening the critical section.
            DisconnectInBackground(oldClient, oldGeneration);

            // Steps 4 and 5.
            return await OpenAndAwaitHandshakeAsync(newClient, generation, ct);
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    public async Task<TwitchRejoinOutcome> RejoinDesiredChannelsAsync(CancellationToken ct)
    {
        var channels = _desiredChannels.Keys.ToArray();
        if (channels.Length == 0)
        {
            return new TwitchRejoinOutcome(0, 0, 0, false);
        }

        logger.LogInformation("Rejoine {Count} gewünschte(n) Channel(s).", channels.Length);

        var aborted = false;
        foreach (var channelName in channels)
        {
            if (ct.IsCancellationRequested || !IsConnected)
            {
                // Abort instead of producing one "aufgeschoben" line per remaining channel: the
                // connection is gone again and its loss is already a signal, or the host is
                // stopping and the remaining channels no longer matter.
                aborted = true;
                break;
            }

            await TryJoinAsync(channelName, TwitchJoinSource.Rejoin, ct);
        }

        if (!aborted)
        {
            aborted = !await WaitForJoinConfirmationsAsync(channels, ct);
        }

        return TallyRejoin(channels, aborted);
    }

    private async Task<TwitchConnectOutcome> OpenAndAwaitHandshakeAsync(
        TwitchClient client,
        int generation,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        Interlocked.Exchange(ref _connectAttemptedUtcTicks, startedAt.Ticks);

        // Created before the connect, because OnConnected can fire the moment the socket is up.
        // RunContinuationsAsynchronously so the loop never resumes inside TwitchLib's read loop.
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Published under the same gate OnConnected reads it through, so that "the client is
        // current" and "this is its attempt's handshake" can never be observed apart: OnConnected
        // captures the source while it holds the gate and completes exactly that instance.
        lock (_clientStateGate)
        {
            _handshakeSource = handshake;
        }

        bool opened;
        try
        {
            // Exactly one attempt: NoReconnectionPolicy is ReconnectionPolicy(0, maxAttempts: 1),
            // so TwitchLib does not loop here. Its own TimeOutEstablishConnection (15s) bounds only
            // the socket open, not the IRC handshake sends that follow inside the same call, so the
            // attempt as a whole is bounded here (see ConnectAttemptTimeout) — without this the
            // single task that both ticks the watchdog and consumes loss signals could park here
            // forever and take the whole self-healing loop with it.
            opened = await client.ConnectAsync().WaitAsync(ConnectAttemptTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            // Abandoned, not awaited: TwitchLib's connect is not cancellable, so the host must not
            // wait for it. The half-open client is disposed of in the background.
            DisconnectInBackground(client, generation);
            throw;
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "TwitchClient-Verbindungsaufbau (Client #{Generation}) nach {Seconds}s ohne Ergebnis abgebrochen.",
                generation,
                ConnectAttemptTimeout.TotalSeconds);

            // Same disposal path as the cancelled case above: the call may still be sitting in an
            // uncancellable WebSocket send, and only DisconnectAsync cancels the token it waits on.
            DisconnectInBackground(client, generation);
            return new TwitchConnectOutcome(false, TwitchSessionEndReason.ConnectTimeout, DateTime.UtcNow - startedAt, generation);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TwitchClient-Verbindungsaufbau (Client #{Generation}) mit Exception beendet.", generation);
            return new TwitchConnectOutcome(false, TwitchSessionEndReason.ConnectFaulted, DateTime.UtcNow - startedAt, generation);
        }

        if (!opened)
        {
            // ConnectAsync signals failure by returning false rather than throwing — discarding
            // that result used to make a failed connect indistinguishable from a successful one.
            return new TwitchConnectOutcome(false, TwitchSessionEndReason.ConnectFailed, DateTime.UtcNow - startedAt, generation);
        }

        try
        {
            await handshake.Task.WaitAsync(HandshakeTimeout, ct);
        }
        catch (TimeoutException)
        {
            return new TwitchConnectOutcome(false, TwitchSessionEndReason.HandshakeTimeout, DateTime.UtcNow - startedAt, generation);
        }

        return new TwitchConnectOutcome(true, null, DateTime.UtcNow - startedAt, generation);
    }

    private async Task TryJoinAsync(string channelName, TwitchJoinSource source, CancellationToken ct)
    {
        if (!IsConnected)
        {
            // Information, not Warning: with a self-driven rebuild this is an expected state that
            // lasts a few seconds, and the channel stays desired, so the rejoin round or the
            // convergence net picks it up.
            logger.LogInformation(
                "Join für {Channel} aufgeschoben — TwitchClient ist derzeit nicht verbunden.",
                channelName);
            return;
        }

        // Serialises and paces every join we issue, across all callers: boot recovery, the rejoin
        // after a rebuild, Redis join commands and the periodic EnsureJoinedAsync sweep would
        // otherwise each burst independently. A single ad-hoc join is unaffected whenever the
        // previous one is long enough ago.
        try
        {
            await _joinGate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var sinceLastJoin = DateTime.UtcNow - _lastJoinIssuedUtc;
            if (sinceLastJoin < MinIntervalBetweenJoins)
            {
                await Task.Delay(MinIntervalBetweenJoins - sinceLastJoin, ct);
            }

            // Re-checked *after* the gate, immediately before sending: a LEAVE that arrives while
            // this join waited its turn would otherwise still be joined, and nobody would ever part
            // it again — the intent it would be parted by is already gone (G5, R9).
            if (!_desiredChannels.ContainsKey(channelName))
            {
                logger.LogDebug(
                    "JOIN für {Channel} verworfen — der Channel ist zwischenzeitlich nicht mehr gewünscht.",
                    channelName);
                return;
            }

            _lastJoinIssuedUtc = DateTime.UtcNow;
            // The origin line: it says *who* wanted this JOIN. TwitchLib's own "Joining channel"
            // line right after it says it was actually sent. Both are needed, because TwitchLib
            // deduplicates a second JOIN for an already-sent channel silently — without this line
            // a confirmation from the convergence net is indistinguishable from one of the rejoin
            // round, and an SLO-2 measurement could be certified by the wrong path.
            logger.LogInformation(
                "JOIN für {Channel} angestoßen (Quelle {Source}, Client #{Generation}).",
                channelName, source, Volatile.Read(ref _clientGeneration));
            // WaitAsync bounds *our* wait, not the send. TwitchLib's JoinChannelAsync can block on
            // its send semaphore and on the WebSocket send, and neither is cancellable — leaving
            // here therefore means abandoned, exactly as the connect in OpenAndAwaitHandshakeAsync
            // is abandoned rather than aborted; the send may still complete on its own afterwards.
            // Nobody should read either exit as a real abort. Without the token a shutdown could
            // park the watchdog in here and break the "StopAsync returns within about a second"
            // promise — the seconds the final usage flush needs (#122); without the timeout a
            // single stuck send would hold _joinGate for good and with it every Redis JOIN and the
            // periodic 7TV resync, on top of the rebuild loop itself.
            await _client.JoinChannelAsync(channelName).WaitAsync(JoinSendTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutdown during the throttle pause; the remaining channels are irrelevant now.
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "JOIN für {Channel} nach {Seconds}s ohne Rückkehr abgebrochen — der Client wird verworfen.",
                channelName,
                JoinSendTimeout.TotalSeconds);

            // The send is still parked on base.Token, and only the client's own DisconnectAsync
            // cancels that token. Requesting the rebuild is therefore not just about retrying the
            // join: discarding this client is what actually releases the stuck call.
            RequestReconnect(
                TwitchSessionEndReason.JoinSendTimeout,
                $"JOIN für {channelName} blockierte länger als {JoinSendTimeout.TotalSeconds}s.");
        }
        catch (Exception ex)
        {
            // Must not abort the caller's loop (boot recovery, rejoin, periodic resync).
            logger.LogWarning(ex, "Join fehlgeschlagen für {Channel} — wird beim nächsten Reconnect nachgeholt.", channelName);
        }
        finally
        {
            _joinGate.Release();
        }
    }

    /// <summary>
    /// Waits for Twitch to confirm the channels this round sent, and reports whether it got that far
    /// — <c>false</c> means the wait was cut short by the token or by another loss, not that
    /// confirmations are missing (that is <see cref="TwitchRejoinOutcome.Open"/>'s job).
    /// </summary>
    private async Task<bool> WaitForJoinConfirmationsAsync(string[] channels, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + JoinConfirmationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (AllConfirmed(channels))
            {
                return true;
            }

            if (!IsConnected)
            {
                return false;
            }

            try
            {
                await Task.Delay(JoinConfirmationPollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return true;
    }

    private bool AllConfirmed(string[] channels)
    {
        foreach (var channelName in channels)
        {
            // A channel that left the desired set mid-round is not pending — it is gone.
            if (_desiredChannels.TryGetValue(channelName, out var confirmed) && !confirmed)
            {
                return false;
            }
        }

        return true;
    }

    private TwitchRejoinOutcome TallyRejoin(string[] channels, bool aborted)
    {
        var desired = 0;
        var confirmed = 0;
        foreach (var channelName in channels)
        {
            if (!_desiredChannels.TryGetValue(channelName, out var joinConfirmed))
            {
                // Left mid-round: counted in neither figure, so a deliberate LEAVE can never show
                // up as an unconfirmed channel. The round then closes as "N-1 gewünscht,
                // N-1 bestätigt, 0 offen" rather than accusing itself of a missing join.
                continue;
            }

            desired++;
            if (joinConfirmed)
            {
                confirmed++;
            }
        }

        return new TwitchRejoinOutcome(desired, confirmed, desired - confirmed, aborted);
    }

    public Task SimulateServerReconnectAsync() => _client.OnReadLineTestAsync(":tmi.twitch.tv RECONNECT");

    private void OfferSignal(int generation, TwitchSessionEndReason reason, string detail)
    {
        var request = new TwitchReconnectRequest(reason, detail, CurrentSessionDuration(), DateTime.UtcNow, generation);
        switch (_signalSlot.Offer(request))
        {
            case TwitchReconnectSignalOutcome.Deposited:
                logger.LogInformation(
                    "Verlust-Signal abgelegt ({Reason}): {Detail} — Client #{Generation}.",
                    reason, detail, generation);
                break;
            case TwitchReconnectSignalOutcome.Coalesced:
                logger.LogInformation(
                    "Weiteres Verlust-Signal ({Reason}): {Detail} — mit dem vorliegenden zusammengefasst, Client #{Generation}.",
                    reason, detail, generation);
                break;
            default:
                logger.LogInformation(
                    "Verlust-Signal verworfen ({Reason}): {Detail} — Client #{Generation} ist bereits ersetzt.",
                    reason, detail, generation);
                break;
        }
    }

    /// <summary>
    /// The two-part rule every TwitchLib handler goes through before it may signal: the event must
    /// come from the client we currently hold, and that client must have completed its handshake at
    /// some point — <see cref="_sessionStartedUtcTicks"/> is non-zero exactly then, and only the
    /// start of a rebuild attempt clears it again. The second half matters even with the slot's
    /// generation rule: a fresh attempt's client carries a *higher* generation than the condemned
    /// one, so its own OnDisconnected would otherwise pass as a genuine loss instead of being
    /// reported through the attempt's return value.
    /// <para>
    /// Both <paramref name="source"/> and <paramref name="generation"/> are *captured at wiring
    /// time*, never taken from TwitchLib's <c>sender</c> and never re-read from the fields — see
    /// <see cref="WireUpClient"/> for why that distinction is the whole point of this method.
    /// </para>
    /// <para>
    /// <b>Precondition: the caller holds <c>_clientStateGate</c>.</b> Both checks read state the
    /// rebuild replaces, and the offer stamps a generation the rebuild condemns — read outside the
    /// gate they would be a second check-then-act next to the one the caller already closed. Every
    /// call site below is inside a <c>lock (_clientStateGate)</c> block; the method must not take
    /// the gate itself, or the nesting would stop being visible at the call sites.
    /// </para>
    /// </summary>
    private void SignalFromHandler(TwitchClient source, int generation, TwitchSessionEndReason reason, string detail)
    {
        if (!ReferenceEquals(source, _client))
        {
            logger.LogDebug("Ereignis {Reason} eines bereits ersetzten TwitchClient ignoriert.", reason);
            return;
        }

        if (Interlocked.Read(ref _sessionStartedUtcTicks) == 0)
        {
            logger.LogDebug(
                "Ereignis {Reason} eines Clients ohne Handshake ignoriert — der Fehlversuch meldet sich über seinen Rückgabewert.",
                reason);
            return;
        }

        OfferSignal(generation, reason, detail);
    }

    private TimeSpan? CurrentSessionDuration()
    {
        var ticks = Interlocked.Read(ref _sessionStartedUtcTicks);
        return ticks == 0 ? null : DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
    }

    private void DisconnectInBackground(TwitchClient client, int generation) => _ = Task.Run(async () =>
    {
        try
        {
            await client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            // Never fatal: the replacement is already connected or connecting, and an exception
            // escaping this task would be an unobserved one.
            logger.LogWarning(
                ex,
                "Aufräumen des alten TwitchClient #{Generation} fehlgeschlagen (ignoriert).",
                generation);
        }
    });

    /// <summary>
    /// Binds one client's events to this manager. Every handler that has to know which client it
    /// belongs to is a closure over *this* client and *this* client's generation; not one of them
    /// looks at TwitchLib's <c>sender</c>, which is why <see cref="Bind{TArgs}"/> discards it.
    /// <para>
    /// <b>Why bound identity instead of <c>sender</c>.</b> TwitchLib does not raise all of these
    /// events with the same sender: <c>OnDisconnected</c> and <c>OnReconnected</c> pass the
    /// <see cref="WebSocketClient"/> straight through from TwitchLib.Communication, while every
    /// other event passes the <see cref="TwitchClient"/> itself (verified against
    /// TwitchLib.Client 4.0.1: <c>_client_OnDisconnected</c> and <c>_client_OnReconnected</c>
    /// forward their own <c>sender</c>, <c>_client_OnFatality</c> and the IRC handlers pass
    /// <c>this</c>). A <c>ReferenceEquals(sender, _client)</c> guard was therefore permanently false
    /// for exactly those two — measured on 2026-09-08 on the dev worker: after a killed socket the
    /// "TwitchClient getrennt" line was followed immediately by "Ereignis Disconnected eines bereits
    /// ersetzten TwitchClient ignoriert" although nothing had been replaced, and the rebuild only
    /// started ~1.5 s later off the trailing <c>OnConnectionError</c>. Had that follow-up stayed
    /// away in some loss mode, the only remaining catch would have been the watchdog's 60 s backstop
    /// (risk R1) — the very counting gap #68 exists to close. Capturing the client removes the
    /// assumption about the foreign library rather than correcting it.
    /// </para>
    /// <para>
    /// <b>And why the generation is captured too.</b> Reading <c>_client</c> and
    /// <c>_clientGeneration</c> as two separate steps is a race: a rebuild can swap the client
    /// between them, and the signal would then be stamped with the *new* generation — whereupon the
    /// slot's generation rule (plan 7, PG1) would admit precisely the straggler of the replaced
    /// client that it exists to discard. A generation fixed at wiring time cannot drift.
    /// </para>
    /// <para>
    /// <b>The rule for every handler added here, going forward.</b> Every handler that touches
    /// manager state checks its bound identity first — before any field write and before any log
    /// line, mirroring the guard below — and returns on mismatch; a handler that only logs, or
    /// only records a fact about Twitch itself rather than about this connection's identity (a
    /// received frame, a chat message someone actually sent), does not need the check. Four
    /// defects in this file so far were all the same shape: a handler that either skipped the
    /// check entirely, ran it only after already mutating the state it was meant to guard, or ran
    /// it in a separate step from that mutation — which is the same defect stretched over a thread
    /// switch, and why check and mutation now share one <c>lock (_clientStateGate)</c> block.
    /// </para>
    /// </summary>
    private void WireUpClient(TwitchClient client, int generation)
    {
        client.Initialize(new ConnectionCredentials()); // anonym/read-only

        var connected = Bind<OnConnectedEventArgs>(_ => OnConnected(client, generation));
        var reconnected = Bind<OnConnectedEventArgs>(_ => OnReconnected(client, generation));
        var disconnected = Bind<OnDisconnectedArgs>(_ => OnDisconnected(client, generation));
        var joinConfirmationFailed = Bind<OnFailureToReceiveJoinConfirmationArgs>(OnFailureToReceiveJoinConfirmation);
        var connectionError = Bind<OnConnectionErrorArgs>(e => OnConnectionError(client, generation, e));
        var joinedChannel = Bind<OnJoinedChannelArgs>(e => OnJoinedChannel(client, generation, e));
        var leftChannel = Bind<OnLeftChannelArgs>(OnLeftChannel);
        var messageReceived = Bind<OnMessageReceivedArgs>(OnMessageReceived);
        var sendReceiveData = Bind<OnSendReceiveDataArgs>(OnSendReceiveData);

        client.OnConnected += connected;
        client.OnReconnected += reconnected;
        client.OnDisconnected += disconnected;
        client.OnFailureToReceiveJoinConfirmation += joinConfirmationFailed;
        client.OnConnectionError += connectionError;
        client.OnJoinedChannel += joinedChannel;
        client.OnLeftChannel += leftChannel;
        client.OnMessageReceived += messageReceived;
        client.OnSendReceiveData += sendReceiveData;

        // The detach closure holds the very delegate instances registered above — see UnwireClient.
        _unwireCurrentClient = () =>
        {
            client.OnConnected -= connected;
            client.OnReconnected -= reconnected;
            client.OnDisconnected -= disconnected;
            client.OnFailureToReceiveJoinConfirmation -= joinConfirmationFailed;
            client.OnConnectionError -= connectionError;
            client.OnJoinedChannel -= joinedChannel;
            client.OnLeftChannel -= leftChannel;
            client.OnMessageReceived -= messageReceived;
            client.OnSendReceiveData -= sendReceiveData;
        };
    }

    /// <summary>
    /// Detaches the current client's handlers, through the closure
    /// <see cref="WireUpClient(TwitchClient, int)"/> left behind. It has to go through stored
    /// delegate instances because closures cannot be removed the way method groups can — two lambdas
    /// with the same body are different objects, so a <c>-=</c> against a freshly built one would
    /// silently remove nothing and leave the replaced client wired for the rest of the process.
    /// Getting this right is what keeps decision E4 exact: once this returns, the old client reaches
    /// none of our state, however long its background <c>DisconnectAsync</c> still takes, and its
    /// stragglers during a backoff floor land nowhere.
    /// </summary>
    private void UnwireClient() => _unwireCurrentClient?.Invoke();

    private Task OnConnected(TwitchClient source, int generation)
    {
        // Bound identity, same as every other handler below — but the guard here cannot go through
        // SignalFromHandler: this handler doesn't signal, it *sets* the very fields
        // SignalFromHandler's second check reads (_sessionStartedUtcTicks), so it must decide on
        // its own and before touching anything. The straggler this closes: OpenAndAwaitHandshakeAsync
        // gives up on HandshakeTimeout (10s) while the old client's socket can still be alive
        // underneath, and its late "004" would otherwise land on whatever _handshakeSource and
        // _sessionStartedUtcTicks hold *now* — the successor's — completing a handshake the
        // successor never actually saw and reporting a rebuild as successful against a connection
        // that isn't up.
        // Check and state change share one critical section: passing the check and then being
        // preempted long enough for the rebuild to install its successor would land every write
        // below on that successor — its connection flag set, its handshake satisfied without a
        // "004" of its own.
        TaskCompletionSource? handshake;
        lock (_clientStateGate)
        {
            if (!ReferenceEquals(source, _client))
            {
                logger.LogDebug(
                    "OnConnected eines bereits ersetzten TwitchClient (Client #{Generation}) ignoriert.",
                    generation);
                return Task.CompletedTask;
            }

            SetConnected(true);
            Interlocked.Exchange(ref _sessionStartedUtcTicks, DateTime.UtcNow.Ticks);

            // Captured, not re-read afterwards: what gets completed below must be the source of
            // the attempt that was current when the identity check passed.
            handshake = _handshakeSource;
        }

        logger.LogInformation("TwitchClient verbunden.");

        // No rejoin here (E2). This handler runs synchronously inside TwitchLib's read loop —
        // Handle004 awaits it — so joining 600ms per channel from here blocked the very loop that
        // has to process the join confirmations, and TwitchLib then reported joins as failed whose
        // "366" was already sitting in the socket buffer. The rebuild loop rejoins instead, on its
        // own task, once this handshake is signalled below.
        //
        // Outside the gate: this releases the rebuild loop, whose next step takes the gate itself —
        // there is no reason to make it queue behind this handler.
        handshake?.TrySetResult();
        return Task.CompletedTask;
    }

    private Task OnDisconnected(TwitchClient source, int generation)
    {
        // Identity first, before any state change or log line (same rule as OnConnected and
        // OnJoinedChannel): this handler flips the connection-wide flag and unconfirms every
        // channel, so a straggler from a replaced-but-still-delivering client must never reach
        // either — it would otherwise report an intact, rejoined connection as lost (TryJoinAsync
        // starts deferring every JOIN, TwitchWatchdogPolicy.Decide sees isConnected: false and can
        // trip the backstop, and the rejoin round's K figure goes positive against a connection
        // that never actually failed).
        // ... and inside the same critical section as those changes, not merely ahead of them: a
        // handler preempted after the check writes into the *successor's* state — clearing its
        // confirmations and reporting a connection that is up as lost.
        lock (_clientStateGate)
        {
            if (!ReferenceEquals(source, _client))
            {
                logger.LogDebug(
                    "OnDisconnected eines bereits ersetzten TwitchClient (Client #{Generation}) ignoriert.",
                    generation);
                return Task.CompletedTask;
            }

            // Without a log line here a silent drop (a Twitch-side PING timeout, say) would freeze
            // chat matching for every channel without a trace. Marking the channels unconfirmed
            // lets the rejoin round and EnsureJoinedAsync verify them again.
            SetConnected(false);
            MarkAllChannelsUnconfirmed();
            logger.LogWarning("TwitchClient getrennt.");
            SignalFromHandler(source, generation, TwitchSessionEndReason.Disconnected, "TwitchLib meldet OnDisconnected.");
        }

        return Task.CompletedTask;
    }

    private Task OnFailureToReceiveJoinConfirmation(OnFailureToReceiveJoinConfirmationArgs e)
    {
        // Since the rejoin left the read loop (E2), this line means again what it says: Twitch
        // really did not confirm the join within TwitchLib's 5s window. Before the rebuild, our own
        // throttled rejoin blocked that loop and produced this warning for joins whose confirmation
        // had long arrived. The channel stays in _desiredChannels as unconfirmed, so the round's
        // "K offen" figure counts it and EnsureJoinedAsync retries it.
        logger.LogWarning(
            "Twitch hat den Join für {Channel} nicht bestätigt. Details: {Details}",
            e.Exception.Channel, e.Exception.Details);
        return Task.CompletedTask;
    }

    private Task OnConnectionError(TwitchClient source, int generation, OnConnectionErrorArgs e)
    {
        // Information, not Warning: with NoReconnectionPolicy this is the expected follow-up of a
        // loss, not a fault. TwitchLib raises it from RaiseFatal after its (single, already spent)
        // reconnect attempt — about 2s after OnDisconnected, so with a 0s rebuild delay the client
        // is usually unwired before it arrives and it never reaches us at all. It reaches us only
        // while a floor (flap 5s, tripwire 10s) keeps the old client wired, and then the slot's
        // generation rule discards it.
        logger.LogInformation(
            "TwitchClient meldet Verbindungsfehler für {BotUsername}: {Error}",
            e.BotUsername, e.Error.Message);

        // This handler changes no state of its own, but SignalFromHandler's two checks read state
        // the rebuild replaces — so it runs under the gate like every other signalling path.
        lock (_clientStateGate)
        {
            SignalFromHandler(source, generation, TwitchSessionEndReason.ConnectionError, e.Error.Message);
        }

        return Task.CompletedTask;
    }

    private Task OnReconnected(TwitchClient source, int generation)
    {
        // Identity first, before any state change or log line — same rule as OnDisconnected above,
        // mirror-imaged: a straggler here would report a dead connection as up. "Must never fire"
        // is not a reason to skip this guard; the tripwire has already been silently blind to a
        // wrong sender once (the sender-defekt this whole audit traces back to), and this is the
        // one handler where a false positive is otherwise invisible.
        lock (_clientStateGate)
        {
            if (!ReferenceEquals(source, _client))
            {
                logger.LogDebug(
                    "OnReconnected eines bereits ersetzten TwitchClient (Client #{Generation}) ignoriert.",
                    generation);
                return Task.CompletedTask;
            }

            // Tripwire. With NoReconnectionPolicy this cannot fire: RaiseReconnected sits behind a
            // successful OpenPrivateAsync(isReconnect: true), whose retry loop runs zero times
            // because Reset(true) does not clear the attempt counter — the call raises OnFatality
            // instead and returns false. If this line ever appears, the library changed underneath
            // us and the client may again be running two read loops on one socket (#114); the
            // policy's 10s floor then keeps our rejoin out of the burst TwitchLib would have sent.
            SetConnected(true);
            logger.LogError(
                "TwitchClient reconnected — das darf mit NoReconnectionPolicy nicht auftreten (Issue #114/#68).");
            SignalFromHandler(
                source,
                generation,
                TwitchSessionEndReason.UnexpectedInPlaceReconnect,
                "TwitchLib hat am selben Objekt reconnectet.");
        }

        return Task.CompletedTask;
    }

    private Task OnJoinedChannel(TwitchClient source, int generation, OnJoinedChannelArgs e)
    {
        // Same defect class as OnConnected, on a different field: _desiredChannels is keyed by
        // channel name only, not by client, so a stale confirmation from an abandoned-but-still-
        // delivering socket would mark the channel confirmed for the *current* client without it
        // ever having joined. Unlike OnFailureToReceiveJoinConfirmation below (log only), a
        // wrongly-set true here is load-bearing: EnsureJoinedAsync trusts it and stops retrying,
        // and AllConfirmed/WaitForJoinConfirmationsAsync would call the rejoin round done while the
        // new client's own join for that channel is still outstanding or has actually failed.
        lock (_clientStateGate)
        {
            if (!ReferenceEquals(source, _client))
            {
                logger.LogDebug(
                    "Channel {Channel} gejoint (Client #{Generation}) — bereits ersetzter Client, verworfen.",
                    e.Channel, generation);
                return Task.CompletedTask;
            }

            // Only a confirmed join stops EnsureJoinedAsync from retrying it every minute. Under
            // the gate together with the check, because MarkAllChannelsUnconfirmed runs inside the
            // swap: a confirmation that passed its check just before it would otherwise be written
            // back *after* it and mark a channel confirmed that the successor never joined.
            // TryUpdate deliberately does not insert: a confirmation arriving after a leave must
            // not resurrect the channel as desired.
            _desiredChannels.TryUpdate(e.Channel, true, false);
            logger.LogInformation("Channel {Channel} gejoint.", e.Channel);
        }

        return Task.CompletedTask;
    }

    private Task OnLeftChannel(OnLeftChannelArgs e)
    {
        logger.LogInformation("Channel {Channel} verlassen.", e.Channel);
        return Task.CompletedTask;
    }

    private Task OnSendReceiveData(OnSendReceiveDataArgs e)
    {
        // Any received IRC line proves the socket is alive — including the server PING Twitch sends
        // roughly every five minutes even when every joined channel is silent. This is the liveness
        // signal the watchdog keys off; LastMessageReceivedUtc below stays chat-activity-only.
        // Raised inline from TwitchClient's receive loop for every line, and a synchronous throw
        // here would sever the multicast chain before TwitchLib's own handlers run (verified against
        // TwitchLib.Client 4.0.1, _client_OnMessage) — so this handler must stay allocation-free and
        // exception-free: one branch, one Interlocked store.
        if (e.Direction == SendReceiveDirection.Received)
        {
            Interlocked.Exchange(ref _lastFrameReceivedUtcTicks, DateTime.UtcNow.Ticks);
        }

        return Task.CompletedTask;
    }

    private Task OnMessageReceived(OnMessageReceivedArgs e)
    {
        // Aktualisiert für JEDE Nachricht, nicht nur gematchte — der Watchdog erkennt so
        // auch ein stilles Einfrieren der Verbindung auf Channels ohne Emote-Nutzung. This must
        // happen before any room or bot classification below: a mirrored Shared Chat message or a
        // bot message still proves the socket is alive. Moving either check above these two writes
        // would make the watchdog blind to a channel whose only traffic is mirrored-in or from bots
        // and force spurious reconnects — the exact failure mode fixed on 2026-08-03 (see
        // TwitchWatchdogPolicy). Do not reorder.
        var receivedAtTicks = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref _lastMessageReceivedUtcTicks, receivedAtTicks);
        // Hot path: one indexer assignment, no LINQ and no allocation beyond the dictionary's own
        // first insert per channel.
        _lastMessageByChannelTicks[e.ChatMessage.Channel] = receivedAtTicks;

        // Sentinel for the TwitchLib double-read-loop defect (#114): warn and count, never drop —
        // the line still carries a real message and must be classified and matched like any other.
        // Reads RawIrcMessage, not UndocumentedTags (E6, see IrcLineSpliceRule). Hot path: one
        // call, no allocation on the (overwhelming) non-splice branch.
        var rawIrc = e.ChatMessage.RawIrcMessage;
        if (IrcLineSpliceRule.IsSpliced(rawIrc))
        {
            stats.RecordSplicedIrcLine();
            var tagBlock = IrcLineSpliceRule.TagBlockForLog(rawIrc);

            // No message text here on purpose (data minimisation) — the tag block alone is enough
            // to diagnose the splice. That holds for *foreign* text too: TagBlockForLog redacts the
            // value of every free-text tag (e.g. reply-parent-msg-body, which on a reply carries the
            // parent message verbatim) and, since #246, the value of every identifying tag —
            // display-name, login, user-id, badges — as well, so this line carries the chatter's
            // presence but not their identity.
            logger.LogWarning(
                "Gespleißte IRC-Zeile erkannt (#114) in Channel {Channel}, RoomId {RoomId}: {TagBlock}",
                e.ChatMessage.Channel, e.ChatMessage.RoomId, tagBlock);
        }

        var channelEmotes = emoteMatchCache.GetChannelEmotes(e.ChatMessage.Channel);
        if (channelEmotes.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Classified exactly once per message, after the watchdog bookkeeping above (see the
        // class-level comment on that ordering) and before the token loop, so every emote match in
        // this message shares the same category instead of re-classifying the same chatter per
        // token. Room comes before bot (#73, design doc D2/B2): a message mirrored in from a
        // foreign room, or one whose room cannot be determined, is Shared Chat regardless of who
        // sent it. SharedChatRule.FromTags is null-safe — UndocumentedTags is null for the ordinary
        // message that carries no unknown tag at all, not an error case.
        var origin = SharedChatRule.FromTags(e.ChatMessage.RoomId, e.ChatMessage.UndocumentedTags);
        if (origin == MessageOrigin.Indeterminate)
        {
            stats.RecordIndeterminateSharedChatMessage();
        }

        var isBot = botChatterDetector.IsBot(e.ChatMessage.UserId, e.ChatMessage.Badges);
        var category = UsageCategoryRule.Resolve(origin, isBot);

        // Owns the set and iterates it through its concrete type below, so the struct enumerator
        // applies instead of the boxed one behind IReadOnlySet<string> — see the buffer-taking
        // MatchEmoteIds overload's doc comment. Exactly one HashSet<string> allocated per message,
        // same as before this class existed.
        var matchedThisMessage = new HashSet<string>();
        EmoteNameMatching.MatchEmoteIds(e.ChatMessage.Message, channelEmotes, matchedThisMessage);
        foreach (var emoteId in matchedThisMessage)
        {
            usageCounter.Increment(emoteId, category);
        }

        return Task.CompletedTask;
    }

    private void MarkAllChannelsUnconfirmed()
    {
        foreach (var channelName in _desiredChannels.Keys)
        {
            _desiredChannels.TryUpdate(channelName, false, true);
        }
    }

    private void SetConnected(bool value) => Interlocked.Exchange(ref _connected, value ? 1 : 0);

    private static TwitchClient CreateClient(ILoggerFactory loggerFactory)
    {
        // TwitchClient hands this factory to its own internal ILogger<TwitchClient> (category
        // TwitchLib.Client.TwitchClient) and logs a parse failure with the raw, unredacted IRC line
        // at Error — see RedactingTwitchClientLoggerFactory for the decompiled evidence (#246).
        // Wrapping here, not swapping the factory in DI, keeps every other logger in the process
        // (including this class's own) unaffected.
        var redactingLoggerFactory = new RedactingTwitchClientLoggerFactory(loggerFactory);
        return new TwitchClient(
            client: new WebSocketClient(
                // NoReconnectionPolicy does not mean "never reconnect", it means "exactly one
                // connect attempt per object": verified against TwitchLib.Communication 2.0.1, it
                // is ReconnectionPolicy(reconnectInterval: 0, maxAttempts: 1), and
                // OpenPrivateAsync's Reset(isReconnect: true) returns early *without* clearing
                // _attemptsMade. After the one successful connect the budget is spent, so
                // ReconnectAsync() on such a client can never succeed — its retry loop runs zero
                // times and it raises "Fatal network error." instead. That is deliberate here:
                // every rebuild is a new object (see ReconnectOnceAsync), so the attempt budget is
                // fresh every time and the 2026-07-26 trap — a client whose lifetime budget of ten
                // attempts was silently exhausted, leaving the worker offline for >45min — is
                // structurally impossible rather than merely fixed.
                new ClientOptions(new NoReconnectionPolicy()),
                redactingLoggerFactory.CreateLogger<WebSocketClient>()),
            loggerFactory: redactingLoggerFactory);
    }

    /// <summary>
    /// Wraps a handler that takes only the event arguments into the delegate TwitchLib's events
    /// expect, discarding <c>sender</c> at the single place where that is visible. Which client an
    /// event belongs to is decided by what the handler closed over at wiring time, never by what the
    /// library passes as the sender — see <see cref="WireUpClient(TwitchClient, int)"/>.
    /// </summary>
    private static TwitchLib.Communication.Events.AsyncEventHandler<TArgs> Bind<TArgs>(Func<TArgs, Task> handler) =>
        (_, args) => handler(args);

    private static DateTime? ReadTimestamp(ref long ticksField)
    {
        var ticks = Interlocked.Read(ref ticksField);
        return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Who wanted a JOIN, for the origin log line. Manager-internal on purpose: it is a measuring
    /// instrument for the rejoin round, not part of the contract with the loop.
    /// </summary>
    private enum TwitchJoinSource
    {
        /// <summary>Boot recovery or a Redis <c>JOIN</c> command.</summary>
        Command,

        /// <summary>The periodic resync or a Redis <c>RESYNC</c> — the convergence net.</summary>
        ConvergenceNet,

        /// <summary>The rejoin round of a rebuild.</summary>
        Rejoin
    }
}
