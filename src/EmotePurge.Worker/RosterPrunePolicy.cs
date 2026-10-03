namespace EmotePurge.Worker;

/// <summary>
/// Pure decision logic for the periodic resync's prune step (issue #41): which roster entries
/// <see cref="ITwitchChatManager"/> still holds as desired must be left because the database no
/// longer considers the channel active. This is the symmetric counterpart to the Redis LEAVE
/// handler in <c>Worker.cs</c> — a LEAVE command lost to a Redis outage commits
/// <c>IsBotActive = false</c> to the database but never reaches the worker, so
/// <c>_desiredChannels</c> never gets pruned and the worker keeps matching chat, holding the 7TV
/// EventAPI subscription and reporting the channel in the roster indefinitely. JOIN and RESYNC
/// already self-heal — the periodic tick's own convergence net (<c>EnsureJoinedAsync</c>, the
/// unconditional resync) redoes both within one interval — LEAVE has no such net until now.
/// <para>
/// Since issue #59 the candidates are the roster plus <c>ghostCandidates</c> — the names held only
/// in the match cache and the subscription registry. The caller decides per name whether it is a
/// roster entry (leave IRC) or a pure ghost (memory cleanup only).
/// </para>
/// </summary>
public static class RosterPrunePolicy
{
    /// <summary>
    /// A roster entry is pruned only once it has been inactive for two <em>consecutive</em> ticks,
    /// regardless of <see cref="TwitchRosterEntry.JoinConfirmed"/> — <paramref name="previouslyStaleChannels"/>
    /// is the <see cref="RosterPruneResult.StaleChannels"/> this method returned on the caller's
    /// previous tick. This grace period, not <c>JoinConfirmed</c>, is what guards the snapshot race
    /// where the caller's DB read and the roster are not read atomically: a JOIN commits its
    /// database row before it publishes to Redis (<c>ChannelService.JoinAsync</c>), so a channel
    /// that just joined can appear in the roster (unconfirmed, added the instant the worker
    /// receives the command) moments after this tick's <paramref name="activeChannels"/> snapshot
    /// was taken but before it reflects the new row. At the periodic resync's default 60s interval,
    /// two consecutive ticks give 60-120s of grace — comfortably more than a Twitch JOIN round-trip
    /// needs to clear the window — before the entry is treated as genuinely gone.
    /// <para>
    /// Requiring <c>JoinConfirmed</c> forever, as an earlier version of this policy did, closed that
    /// race but opened a permanent one: an entry that never confirms — a banned bot account, a
    /// deleted channel, a JOIN that hangs — would never become eligible, no matter how many ticks
    /// pass, because nothing outside a real Twitch JOIN ever flips <c>JoinConfirmed</c> to true. The
    /// two-consecutive-ticks rule closes that hole: an unconfirmed entry is pruned exactly like a
    /// confirmed one once it has stayed inactive across two ticks.
    /// </para>
    /// </summary>
    public static RosterPruneResult DetermineChannelsToPrune(
        IReadOnlyList<string> activeChannels,
        IReadOnlyList<TwitchRosterEntry> roster,
        IReadOnlyCollection<string> previouslyStaleChannels,
        IReadOnlyCollection<string>? ghostCandidates = null)
    {
        var active = new HashSet<string>(activeChannels, StringComparer.OrdinalIgnoreCase);
        var previouslyStale = new HashSet<string>(previouslyStaleChannels, StringComparer.OrdinalIgnoreCase);
        var toPrune = new List<string>();
        var stillStale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Roster entries first, then the in-memory-only names: a channel that left the roster (its
        // LEAVE ran) but whose cache or registry entry was re-created afterwards by a sync that was
        // in flight across a rename or deactivation (issue #59) is invisible to a roster-only walk.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = roster.Select(e => e.ChannelName).Concat(ghostCandidates ?? []);

        foreach (var name in candidates)
        {
            if (!seen.Add(name) || active.Contains(name))
            {
                continue;
            }

            if (previouslyStale.Contains(name))
            {
                toPrune.Add(name);
            }
            else
            {
                stillStale.Add(name);
            }
        }

        return new RosterPruneResult(toPrune, stillStale);
    }
}

/// <summary>Result of one <see cref="RosterPrunePolicy.DetermineChannelsToPrune"/> call.</summary>
/// <param name="ChannelsToPrune">Channels the caller should now leave.</param>
/// <param name="StaleChannels">
/// Meant to be fed back in as the next tick's <c>previouslyStaleChannels</c> argument — it is the
/// caller's only state to keep.
/// </param>
public sealed record RosterPruneResult(IReadOnlyList<string> ChannelsToPrune, IReadOnlySet<string> StaleChannels);
