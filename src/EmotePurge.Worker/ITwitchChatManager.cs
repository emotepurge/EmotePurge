namespace EmotePurge.Worker;

/// <summary>
/// One channel the manager wants to be in, plus what Twitch and the chat traffic say about it.
/// <paramref name="JoinConfirmed"/> is the desired-vs-confirmed distinction the manager already
/// tracks internally — a channel can be desired for hours without Twitch ever confirming the JOIN,
/// and that is precisely the silent failure this roster exists to surface.
/// </summary>
/// <param name="LastMessageUtc">
/// Last chat message seen in *this* channel. Null means none since the worker started, which for a
/// quiet channel is normal and for a busy one is the strongest available hint that the join is dead.
/// </param>
public readonly record struct TwitchRosterEntry(string ChannelName, bool JoinConfirmed, DateTime? LastMessageUtc);

/// <summary>
/// One reconnect signal. <see cref="ClientGeneration"/> identifies the <c>TwitchClient</c> instance
/// whose loss produced it — the number that <see cref="TwitchReconnectSignalSlot"/> uses to tell a
/// late echo of an already-replaced client apart from a genuinely new loss.
/// <see cref="SessionDuration"/> carries the same "no handshake at all" distinction as
/// <see cref="TwitchSessionResult"/>.
/// </summary>
public sealed record TwitchReconnectRequest(
    TwitchSessionEndReason Reason,
    string Detail,
    TimeSpan? SessionDuration,
    DateTime RequestedUtc,
    int ClientGeneration);

/// <summary>
/// The result of exactly one rebuild attempt (steps 1–5 of the recreate sequence).
/// <paramref name="HandshakeCompleted"/> is the only success criterion — an open socket without the
/// IRC handshake ("004") is a failure, not a connection.
/// </summary>
/// <param name="FailureReason">Null on success; otherwise which of the three ways the attempt failed.</param>
/// <param name="Elapsed">How long the attempt itself took, connect plus handshake wait.</param>
/// <param name="ClientGeneration">The number of the client this attempt built, for the log lines.</param>
public readonly record struct TwitchConnectOutcome(
    bool HandshakeCompleted,
    TwitchSessionEndReason? FailureReason,
    TimeSpan Elapsed,
    int ClientGeneration);

/// <summary>
/// The result of one throttled rejoin round.
/// <para>
/// <paramref name="Desired"/> counts the channels still wanted when the round finished, not the
/// snapshot it started from: a LEAVE that arrives mid-round removes its channel from both this
/// figure and <paramref name="Confirmed"/>, so an intentionally abandoned channel can never show up
/// as an unconfirmed one. <paramref name="Open"/> is therefore the number of channels we still want
/// and Twitch has not confirmed — the K of SLO-2, and the only thing worth a warning.
/// </para>
/// </summary>
/// <param name="Aborted">The round ended early: the connection dropped again, or the host is stopping.</param>
public readonly record struct TwitchRejoinOutcome(int Desired, int Confirmed, int Open, bool Aborted);

public interface ITwitchChatManager
{
    void Initialize();

    /// <summary>
    /// The boot attempt: opens the initial client once and waits for the handshake. It does not
    /// retry — on failure it leaves a reconnect signal behind for the rebuild loop in
    /// <see cref="TwitchConnectionWatchdog"/> and returns. Returns immediately once
    /// <paramref name="ct"/> fires.
    /// </summary>
    Task ConnectAsync(CancellationToken ct);

    Task JoinChannelAsync(string channelName);

    /// <summary>
    /// The position in the worker's leave sequence. A caller of <see cref="EnsureJoinedAsync"/> takes
    /// it <em>before</em> it reads the active roster from Postgres and hands it over unchanged — see
    /// <see cref="ChannelLeaveLedger"/> for why that order makes a stale read harmless.
    /// </summary>
    long CaptureLeaveStamp();

    /// <summary>
    /// Joins only if the channel isn't already joined-and-confirmed. Driven by the periodic 7TV
    /// resync (and the RESYNC command) as a convergence net for lost Redis commands and joins Twitch
    /// never confirmed. Does nothing when the channel was left after <paramref name="leaveStamp"/>
    /// was captured: the caller's roster read predates that LEAVE, so its "active" is stale.
    /// </summary>
    Task EnsureJoinedAsync(string channelName, long leaveStamp);

    Task LeaveChannelAsync(string channelName);

    // Für den Watchdog (s. TwitchConnectionWatchdog): erkennt stille Verbindungsabbrüche,
    // bei denen TwitchLib selbst kein OnDisconnected feuert.
    bool IsConnected { get; }

    DateTime? LastMessageReceivedUtc { get; }

    // Any received IRC line, not just chat: includes Twitch's ~5-minute server PING, so it stays
    // fresh on a healthy connection even when every joined channel is silent. This is what the
    // watchdog measures — LastMessageReceivedUtc above remains the chat-activity figure for the
    // health display.
    DateTime? LastFrameReceivedUtc { get; }

    // Fallback reference point for the watchdog: LastMessageReceivedUtc stays null until the very
    // first chat message, which used to make a worker that never connected undetectable.
    DateTime? ConnectAttemptedUtc { get; }

    // Every desired channel with its confirmation and traffic state, for the admin roster. Ordered
    // by name so the published snapshot is stable across ticks and diffs cleanly in a log.
    IReadOnlyList<TwitchRosterEntry> GetRoster();

    /// <summary>
    /// Deposits a "the connection needs rebuilding" signal, stamped with the current client's
    /// generation. The watchdog tick uses it for its two remaining verdicts; TwitchLib's own event
    /// handlers deposit the same signal internally with the generation of the client that raised
    /// them. Never blocks and never rebuilds anything itself (design decision E1).
    /// </summary>
    void RequestReconnect(TwitchSessionEndReason reason, string detail);

    /// <summary>
    /// Waits for the next reconnect signal, consumes it, and condemns the client generation it
    /// carries — every later signal from that same, now-replaced client is discarded rather than
    /// queued (see <see cref="TwitchReconnectSignalSlot"/>). A signal deposited before the wait
    /// started is delivered immediately, which is what makes a failed boot connect reach the loop.
    /// </summary>
    Task<TwitchReconnectRequest> WaitForReconnectRequestAsync(CancellationToken ct);

    /// <summary>
    /// Exactly one rebuild attempt: detach and background-clean the old client, build a fresh one,
    /// open it, wait for the handshake. Never retries and never sleeps — the pacing belongs to the
    /// loop and its backoff policy, not here.
    /// </summary>
    Task<TwitchConnectOutcome> ReconnectOnceAsync(CancellationToken ct);

    /// <summary>
    /// Step 6 of the sequence, run by the loop rather than by an event handler (design decision E2):
    /// a throttled JOIN round over the desired channels, followed by a bounded wait for Twitch's
    /// confirmations. Does not log a closing line — only the caller knows how long ago the
    /// connection was lost.
    /// </summary>
    Task<TwitchRejoinOutcome> RejoinDesiredChannelsAsync(CancellationToken ct);

    /// <summary>
    /// Debug-only trigger for the <c>RECONNECT</c> path (Entscheidung 7.5, Task 6): feeds
    /// <c>:tmi.twitch.tv RECONNECT</c> into the current client via TwitchLib's own
    /// <c>OnReadLineTestAsync</c> — the same case its real read loop hits for Twitch's own
    /// <c>RECONNECT</c> line. Proves the policy reaction (Plan Task 9, S2), not the thread
    /// context: this call runs on the caller's thread, not inside a dying read loop, so it does
    /// not exercise E1 (TwitchLib's handlers doing nothing in that loop) — that is verified by
    /// reading the handlers, not by this call. The manager checks no gate here; that lives
    /// solely in the command dispatcher in <see cref="Worker"/>.
    /// </summary>
    Task SimulateServerReconnectAsync();
}
