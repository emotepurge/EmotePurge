namespace EmotePurge.Worker;

/// <summary>
/// Why a Twitch session ended (or why an attempt to build one failed), reported by
/// <see cref="TwitchChatManager"/> and the watchdog tick to <see cref="TwitchReconnectBackoffPolicy"/>.
/// </summary>
public enum TwitchSessionEndReason
{
    /// <summary>TwitchLib's own <c>OnDisconnected</c> on the current, handshaken client.</summary>
    Disconnected,

    /// <summary>TwitchLib's <c>OnConnectionError</c> ("Fatal network error.") on its own.</summary>
    ConnectionError,

    /// <summary>Watchdog tick: no IRC frame for 15 minutes on an otherwise "connected" client.</summary>
    FrameStale,

    /// <summary>Watchdog tick: not connected and no attempt in flight — must never fire.</summary>
    DisconnectedBackstop,

    /// <summary>The very first connect attempt at boot failed before any session existed.</summary>
    InitialConnectFailed,

    /// <summary>A rebuild attempt's <c>ConnectAsync</c> returned <c>false</c>.</summary>
    ConnectFailed,

    /// <summary>A rebuild attempt's connect threw.</summary>
    ConnectFaulted,

    /// <summary>The socket opened but no handshake ("004") arrived within 10 s.</summary>
    HandshakeTimeout,

    /// <summary>
    /// A rebuild attempt's <c>ConnectAsync</c> did not return within 30 s. TwitchLib's own
    /// <c>TimeOutEstablishConnection</c> covers only the socket open, not the IRC handshake sends
    /// that follow inside the same call, and those wait on a token nothing cancels while the
    /// rebuild loop is blocked in them.
    /// </summary>
    ConnectTimeout,

    /// <summary>
    /// A <c>JoinChannelAsync</c> did not return within 10 s — the same uncancellable send, reached
    /// through TwitchLib's join queue. Unlike the others this one is raised on a client that has
    /// a live session, so it is paced as that session's end rather than as a failed attempt;
    /// discarding the client is what releases the stuck send.
    /// </summary>
    JoinSendTimeout,

    /// <summary>
    /// A PART of ours stayed unechoed past <see cref="ChannelJoinTracker.PartEchoGrace"/> after holding
    /// back a JOIN echo of a channel that is wanted again and still unconfirmed (#516). TwitchLib took
    /// that echo and lists the channel as joined, so it drops every plain JOIN for it and no further
    /// echo can confirm it on this client; a fresh client starts with an empty channel list and rejoins
    /// it. Raised on a live session and paced as that session's end.
    /// </summary>
    PartEchoMissing,

    /// <summary>Tripwire: TwitchLib reconnected in place although <c>NoReconnectionPolicy</c> makes that impossible.</summary>
    UnexpectedInPlaceReconnect,

    /// <summary>The debug trigger (Task 6) injected a synthetic <c>RECONNECT</c>.</summary>
    DebugTrigger
}

/// <summary>
/// One session's or attempt's outcome, the input to <see cref="TwitchReconnectBackoffPolicy.NextDelay"/>.
/// <see cref="SessionDuration"/> is <c>null</c> when there was no handshake at all — a failed
/// rebuild attempt, not a session — and that distinction, not <see cref="Reason"/>, is what the
/// policy uses to tell a session apart from an attempt.
/// </summary>
public sealed record TwitchSessionResult(TwitchSessionEndReason Reason, TimeSpan? SessionDuration);

/// <summary>
/// Reconnect pacing for the self-driven Twitch rebuild loop (issue #68), same shape as
/// <see cref="SevenTv.SevenTvBackoffPolicy"/> but with different numbers and one extra rule. Four
/// things a reader of this file needs, once, instead of re-deriving them at the next outage:
/// <para>
/// <b>Why 30 s and not 7TV's 60 s.</b> The cap is the maximum *additional* counting gap after a
/// Twitch outage has already ended: once Twitch is back, the next attempt notices within one capped
/// interval plus its own duration. A 7TV outage costs no counting at all (the periodic REST resync
/// covers it), so 7TV can afford to wait longer between attempts; a Twitch outage costs live chat
/// every second it lasts, so the ceiling stays where TwitchLib's own reconnect policy already sat
/// without ever drawing Twitch's attention.
/// </para>
/// <para>
/// <b>Why the failure streak counts attempts, not sessions.</b> The first draft of this policy
/// treated any session shorter than 60 s as a failure, to guard against session churn. The
/// adversarial review of the design (2026-09-08, finding G1) showed that rule would cost sustained
/// flapping 34–47 s per event instead of today's ~14 s, and that its premise — Twitch treats fast
/// reconnects as abuse — was itself superseded by the 2026-07-30 fix for the ten-attempts trap in
/// the previous default policy. A session that came up and went straight back down is not a failed
/// attempt; it is a session, and it resets the failure streak like any other.
/// </para>
/// <para>
/// <b>Why the flap dampening is a floor, not a second backoff.</b> Without any damping, a
/// connection Twitch tears down immediately after every handshake would loop at roughly the 2 s
/// signal-to-004 period — about 1,800 rebuilds and log lines per hour. A flat 5 s floor from the
/// third consecutive short session onward bounds that to ≤ 720/h. It never grows on its own and it
/// is lifted the moment a session holds for 60 s; it stacks with the exponential backoff only as a
/// lower bound (a maximum of the two), never as its own exponent.
/// </para>
/// <para>
/// <b>Why 5 s is caution, not calibration.</b> Whether and at what rate Twitch actually rejects fast
/// anonymous reconnects has never been measured (concept 7.8); the 2026-07-27 "abuse detection"
/// read of an earlier outage was itself explained away by the ten-attempts trap above. The floor
/// exists to protect our own log and our own event loop from a tight cycle, not because Twitch is
/// known to mind.
/// </para>
/// Clock-free like every other policy in this project — elapsed time and durations are passed
/// in, never read from a clock — and with injectable jitter for deterministic tests:
/// <c>jitter() == 0.5</c> is the neutral case and yields the raw, un-jittered value.
/// </summary>
public sealed class TwitchReconnectBackoffPolicy(Func<double>? jitter = null)
{
    /// <summary>Base of the exponential failure backoff, doubled per consecutive failed attempt.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>Cap on any returned delay, applied after jitter — a real ceiling, never 36 s.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>A session at or above this duration is "long": it clears the flap dampening.</summary>
    public static readonly TimeSpan FlapSessionThreshold = TimeSpan.FromSeconds(60);

    /// <summary>Floor applied from the third consecutive short session onward.</summary>
    public static readonly TimeSpan FlapFloor = TimeSpan.FromSeconds(5);

    /// <summary>Floor applied when a session ends via the <see cref="TwitchSessionEndReason.UnexpectedInPlaceReconnect"/> tripwire.</summary>
    public static readonly TimeSpan StolperdrahtFloor = TimeSpan.FromSeconds(10);

    /// <summary>Caps the failure streak's exponent so it can never grow unboundedly.</summary>
    public const int MaxFailureStreak = 5;

    /// <summary>How many consecutive short sessions activate the flap floor.</summary>
    public const int FlapDampeningThreshold = 3;

    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;
    private int _failureStreak;
    private int _shortSessionStreak;

    public TimeSpan NextDelay(TwitchSessionResult result) =>
        result.SessionDuration is { } duration ? SessionEnded(duration, result.Reason) : FailedAttempt();

    private TimeSpan SessionEnded(TimeSpan duration, TwitchSessionEndReason reason)
    {
        // A session — however it ended, however long it lasted — is not a failed attempt (G1): it
        // always resets the failure streak.
        _failureStreak = 0;

        _shortSessionStreak = duration >= FlapSessionThreshold ? 0 : _shortSessionStreak + 1;

        var delay = _shortSessionStreak >= FlapDampeningThreshold ? FlapFloor : TimeSpan.Zero;

        if (reason == TwitchSessionEndReason.UnexpectedInPlaceReconnect && delay < StolperdrahtFloor)
        {
            delay = StolperdrahtFloor;
        }

        return delay;
    }

    private TimeSpan FailedAttempt()
    {
        // A failed attempt is not a session: it neither counts toward nor resets the short-session
        // streak (plan 1.3, point 4/11) — only Reset via a completed session does that.
        _failureStreak = Math.Min(_failureStreak + 1, MaxFailureStreak);

        // Uncapped on purpose (plan 2.4: "Jitter ± 20 % auf den Rohwert, danach auf 30 s gekappt").
        // Capping the raw value here first would shrink the jittered band itself: at the fifth
        // failure the raw value is 32s, so the correct band is 25.6s-30s (32 * 0.8, then capped by
        // Jittered) — capping to 30s before jitter narrowed it to 24s-30s (30 * 0.8), 1.6s too
        // aggressive at the lower bound. No overflow risk: the exponent is bounded by
        // MaxFailureStreak (5) at 4, and BaseDelay.Ticks (2s = 20,000,000 ticks) << 4 is
        // 320,000,000 — far below long's ~9.2 * 10^18 range.
        var exponent = _failureStreak - 1;
        var raw = TimeSpan.FromTicks(BaseDelay.Ticks << exponent);
        var delay = Jittered(raw);

        if (_shortSessionStreak >= FlapDampeningThreshold && delay < FlapFloor)
        {
            delay = FlapFloor;
        }

        return delay;
    }

    private TimeSpan Jittered(TimeSpan raw) =>
        TimeSpan.FromTicks(Math.Min((long)(raw.Ticks * (0.8 + 0.4 * _jitter())), MaxDelay.Ticks));
}
