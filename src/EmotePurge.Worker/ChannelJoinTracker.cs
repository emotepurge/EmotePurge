using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

/// <summary>What <see cref="ChannelJoinTracker.RecordFailure"/> did with a reported join failure.</summary>
public enum JoinFailureOutcome
{
    /// <summary>Not booked: the channel is unknown or already confirmed.</summary>
    Ignored,

    /// <summary>The channel's reason is the same as before, or a weaker one that does not replace it.</summary>
    Repeated,

    /// <summary>The channel's reason is new or different; worth one log line.</summary>
    Changed
}

/// <summary>One channel in a <see cref="ChannelJoinTracker.Snapshot"/>.</summary>
public readonly record struct ChannelJoinState(string Name, bool Confirmed, double UnconfirmedSeconds, string? Reason);

/// <summary>
/// The worker's per-channel IRC join bookkeeping: which channels are wanted, which are confirmed,
/// how long each unconfirmed one has been waiting, and why its last attempt failed.
/// <para>
/// The wait is an accrual clock: only time during which the client is connected and the channel is
/// unconfirmed counts. Disconnected time never counts, a disconnect freezes the sum, and only a
/// confirmation resets it to zero. A channel that was confirmed before a disconnect starts again at
/// zero. The tracker has no clock of its own — callers pass <c>now</c> — so it is deterministic to test.
/// </para>
/// <para>
/// An attempt window opens when a JOIN is on the wire and closes on our own PART on the wire (or a
/// disconnect). Our own JOIN echo confirms only inside an open window, so a late echo of an old
/// attempt cannot confirm a freshly re-wanted channel. A timeout or a limit or ban notice does not
/// close the window: a late echo still confirms, as it always did. A suspended notice does
/// (<see cref="EndAttempt"/>): TwitchLib drops that attempt at once and may put the next JOIN for the
/// same channel on the wire right after it, which must count as a new attempt.
/// </para>
/// <para>
/// Our own PART on the wire is also tracked on its own, apart from the wish and the attempt window,
/// until Twitch echoes it (or the connection goes). Twitch answers in send order, so a JOIN echo that
/// arrives while our PART is still pending answers a JOIN sent before that PART: it belongs to the
/// membership the PART ends and neither confirms nor consumes the newer attempt. The PART echo then
/// ends that membership without touching the newer wish, and the newer JOIN's echo confirms.
/// </para>
/// <para>
/// Thread-safe through one private lock, never held across an await and never calling out — the
/// innermost latch in <see cref="TwitchChatManager"/>.
/// </para>
/// </summary>
public sealed class ChannelJoinTracker
{
    /// <summary>
    /// How long a PART of ours counts as pending. Twitch echoes it within milliseconds (measured
    /// 2026-11-25), so a PART still unechoed after this is presumed never to be echoed (we were no
    /// member when it was sent) and stops holding back JOIN echoes.
    /// </summary>
    public static readonly TimeSpan PartEchoGrace = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    // Channels with one of our PARTs on the wire whose echo has not arrived yet. Independent of
    // _entries: the leave drops the wish before the PART is sent, and a re-activation adds a new one.
    // The value is the time the PART went on the wire; a pending PART older than PartEchoGrace is
    // treated as never echoed.
    private readonly Dictionary<string, DateTimeOffset> _pendingParts = new(StringComparer.OrdinalIgnoreCase);

    private bool _connected;

    /// <summary>Adds a wanted channel. Returns true when it was new; an existing one is untouched.</summary>
    public bool RecordIntent(string name, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(name))
            {
                return false;
            }

            _entries[name] = new Entry { RunningSince = _connected ? now : null };
            return true;
        }
    }

    /// <summary>The handshake finished: every unconfirmed clock runs from now on.</summary>
    public void SetConnected(DateTimeOffset now)
    {
        lock (_gate)
        {
            _connected = true;
            foreach (var entry in _entries.Values)
            {
                if (!entry.Confirmed && entry.RunningSince is null)
                {
                    entry.RunningSince = now;
                }
            }
        }
    }

    /// <summary>
    /// The connection is gone or being rebuilt: running clocks stop with their time added, every
    /// channel becomes unconfirmed (a formerly confirmed one restarts at zero), every attempt closes and
    /// no PART is pending any more (the old connection's echoes never reach the new one).
    /// </summary>
    public void SetDisconnected(DateTimeOffset now)
    {
        lock (_gate)
        {
            _connected = false;
            foreach (var entry in _entries.Values)
            {
                if (entry.Confirmed)
                {
                    entry.Confirmed = false;
                    entry.AccruedSeconds = 0;
                }
                else
                {
                    entry.Freeze(now);
                }

                entry.RunningSince = null;
                entry.AttemptOpen = false;
            }

            _pendingParts.Clear();
        }
    }

    /// <summary>A JOIN for the channel is on the wire. Only a wanted, unconfirmed channel opens an attempt.</summary>
    public void BeginAttempt(string name)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(name, out var entry) && !entry.Confirmed)
            {
                entry.AttemptOpen = true;
                entry.HasConcreteReason = false;
            }
        }
    }

    /// <summary>
    /// Our own PART for the channel is on the wire: the attempt window closes, and the PART stays
    /// pending until its echo (<see cref="MarkParted"/>) or a disconnect.
    /// </summary>
    public void RecordPartSent(string name, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(name, out var entry))
            {
                entry.AttemptOpen = false;
            }

            _pendingParts[name] = now;
        }
    }

    /// <summary>
    /// Twitch ended the current attempt with a final answer (a suspended channel): the window closes,
    /// the booked reason stays, and the next JOIN on the wire opens a new attempt whose own outcome
    /// may replace it.
    /// </summary>
    public void EndAttempt(string name)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(name, out var entry))
            {
                entry.AttemptOpen = false;
            }
        }
    }

    /// <summary>
    /// Our own JOIN echo. Confirms only a wanted channel with an open attempt and no pending PART of
    /// ours (never inserts); returns whether it did. An echo while a PART is pending answers a JOIN
    /// sent before that PART and leaves the newer attempt open. A PART pending for longer than
    /// <see cref="PartEchoGrace"/> no longer counts, so a PART Twitch never echoes cannot block forever;
    /// a confirmation drops such an expired marker, so a later PART from Twitch is read as Twitch's own
    /// and withdraws the confirmation instead of being taken for the old PART's echo.
    /// </summary>
    public bool MarkConfirmed(string name, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (IsPartPending(name, now)
                || !_entries.TryGetValue(name, out var entry) || entry.Confirmed || !entry.AttemptOpen)
            {
                return false;
            }

            // Only an expired marker can still be here (a live one returned above).
            _pendingParts.Remove(name);
            entry.Confirmed = true;
            entry.AccruedSeconds = 0;
            entry.RunningSince = null;
            entry.Reason = null;
            entry.HasConcreteReason = false;
            entry.AttemptOpen = false;
            return true;
        }
    }

    /// <summary>
    /// Our own PART echo. The echo of a PART we sent ends only the membership that PART left: the
    /// pending PART clears and the wish (a newer one, if the channel was re-activated) is untouched.
    /// Any other PART echo (Twitch parted us) withdraws the confirmation of a wanted, confirmed
    /// channel (clock at zero, running if connected) so the convergence retry applies again;
    /// otherwise nothing changes.
    /// </summary>
    public void MarkParted(string name, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_pendingParts.Remove(name))
            {
                return;
            }

            if (_entries.TryGetValue(name, out var entry) && entry.Confirmed)
            {
                entry.Confirmed = false;
                entry.AccruedSeconds = 0;
                entry.RunningSince = _connected ? now : null;
                entry.AttemptOpen = false;
            }
        }
    }

    /// <summary>
    /// Drops a pending PART of an unconfirmed channel once it is older than <see cref="PartEchoGrace"/>
    /// and reports whether it did, so it reports each such PART once. The convergence net uses it to
    /// learn that a JOIN echo it may have ignored will not come again (TwitchLib lists the channel as
    /// joined and drops a plain JOIN), and requests a client rebuild; the rebuild's disconnect clears
    /// every other pending PART as well.
    /// </summary>
    public bool TakeExpiredPart(string name, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_pendingParts.TryGetValue(name, out var sentAt) || now - sentAt < PartEchoGrace
                || (_entries.TryGetValue(name, out var entry) && entry.Confirmed))
            {
                return false;
            }

            _pendingParts.Remove(name);
            return true;
        }
    }

    /// <summary>
    /// Books a failure for a wanted, unconfirmed channel. A concrete reason replaces any reason;
    /// <see cref="TwitchJoinFailureReasons.NoConfirmation"/> replaces one only while the current
    /// attempt has no concrete reason yet.
    /// </summary>
    public JoinFailureOutcome RecordFailure(string name, string reason)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out var entry) || entry.Confirmed)
            {
                return JoinFailureOutcome.Ignored;
            }

            var concrete = reason != TwitchJoinFailureReasons.NoConfirmation;
            if (!concrete && entry.HasConcreteReason)
            {
                return JoinFailureOutcome.Repeated;
            }

            if (concrete)
            {
                entry.HasConcreteReason = true;
            }

            if (entry.Reason == reason)
            {
                return JoinFailureOutcome.Repeated;
            }

            entry.Reason = reason;
            return JoinFailureOutcome.Changed;
        }
    }

    /// <summary>Drops the channel (leave); a later intent starts fresh. Returns whether it existed.</summary>
    public bool Remove(string name)
    {
        lock (_gate)
        {
            return _entries.Remove(name);
        }
    }

    public bool Contains(string name)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(name);
        }
    }

    /// <summary>Whether the channel is wanted and has an open attempt window.</summary>
    public bool IsAttemptOpen(string name)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(name, out var entry) && entry.AttemptOpen;
        }
    }

    public bool IsConfirmed(string name)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(name, out var entry) && entry.Confirmed;
        }
    }

    /// <summary>The wanted channel names, in no particular order.</summary>
    public IReadOnlyList<string> Names()
    {
        lock (_gate)
        {
            return [.. _entries.Keys];
        }
    }

    /// <summary>All wanted channels at time <paramref name="now"/>, ordered by name (ordinal).</summary>
    public IReadOnlyList<ChannelJoinState> Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            return
            [
                .. _entries
                    .Select(pair => new ChannelJoinState(
                        pair.Key, pair.Value.Confirmed, pair.Value.UnconfirmedSecondsAt(now), pair.Value.Reason))
                    .OrderBy(state => state.Name, StringComparer.Ordinal)
            ];
        }
    }

    // Precondition: the caller holds _gate. A clock that jumped backwards keeps the PART pending.
    private bool IsPartPending(string name, DateTimeOffset now) =>
        _pendingParts.TryGetValue(name, out var sentAt) && now - sentAt < PartEchoGrace;

    private sealed class Entry
    {
        public bool Confirmed { get; set; }

        public double AccruedSeconds { get; set; }

        public DateTimeOffset? RunningSince { get; set; }

        public string? Reason { get; set; }

        public bool AttemptOpen { get; set; }

        public bool HasConcreteReason { get; set; }

        public void Freeze(DateTimeOffset now)
        {
            AccruedSeconds += RunningDelta(now);
        }

        public double UnconfirmedSecondsAt(DateTimeOffset now) =>
            Confirmed ? 0 : AccruedSeconds + RunningDelta(now);

        // A clock that jumped backwards contributes zero, never a negative span.
        private double RunningDelta(DateTimeOffset now) =>
            RunningSince is { } since ? Math.Max(0, (now - since).TotalSeconds) : 0;
    }
}
