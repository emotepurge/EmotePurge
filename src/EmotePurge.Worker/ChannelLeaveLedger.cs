namespace EmotePurge.Worker;

/// <summary>
/// Remembers, per channel, when the worker last left it — counted in leaves, not in clock time — so
/// that a convergence JOIN built on an older read of the active roster cannot undo a newer LEAVE.
/// <para>
/// The race it closes (#245 live run, 2026-10-08): the periodic resync reads the active channels
/// from Postgres, then walks them for seconds to minutes (throttled JOINs, one 7TV request per
/// channel). A deactivation committed in between — the identity reconcile's broadcaster lock, an
/// unresolvable login, the excluded-channel list, a user's leave — publishes its LEAVE after the
/// commit, the worker drops the channel's intent, and the walk then reaches the channel and
/// re-creates that intent from its stale list. The bot sat in the chat of a locked channel until the
/// roster prune parted it two ticks later.
/// </para>
/// <para>
/// The ordering argument: a caller takes <see cref="Stamp"/> <em>before</em> it reads the active
/// roster. Every LEAVE the worker processes was published after its deactivation committed. So a
/// LEAVE recorded before the stamp belongs to a commit the roster read already sees (the channel is
/// not in the list), and a LEAVE recorded after the stamp is exactly the one the list may predate —
/// which <see cref="LeftSince"/> reports. A lost LEAVE leaves no entry, and stays the roster prune's
/// case, as before.
/// </para>
/// <para>
/// Not thread-safe on its own: <see cref="TwitchChatManager"/> calls it only under the same latch
/// that guards its desired-channel set, because "has it been left?" and "record the intent" must be
/// one step. Pure and clock-free, so it is tested on its own in the container-free worker tests.
/// </para>
/// </summary>
public sealed class ChannelLeaveLedger
{
    // Keyed case-insensitively like TwitchChatManager's desired set: a LEAVE for `HandOfBlood` must
    // block a convergence JOIN for `handofblood`.
    private readonly Dictionary<string, long> _lastLeaveByChannel = new(StringComparer.OrdinalIgnoreCase);

    private long _leaveCount;

    /// <summary>
    /// The current position in the leave sequence. Take it before reading the roster that the
    /// later <see cref="LeftSince"/> check is meant to protect.
    /// </summary>
    public long Stamp => _leaveCount;

    public void RecordLeave(string channelName)
    {
        _leaveCount++;
        _lastLeaveByChannel[channelName] = _leaveCount;
    }

    /// <summary>
    /// Drops the channel's entry once a fresh, roster-checked JOIN wants it again: that intent is
    /// newer than any stale read still in flight, and the entry would otherwise outlive the channel's
    /// return. This is also what bounds the ledger to the channels currently left.
    /// </summary>
    public void Forget(string channelName) => _lastLeaveByChannel.Remove(channelName);

    /// <summary>True when the channel was left after <paramref name="stamp"/> was taken.</summary>
    public bool LeftSince(string channelName, long stamp) =>
        _lastLeaveByChannel.TryGetValue(channelName, out var leftAt) && leftAt > stamp;
}
