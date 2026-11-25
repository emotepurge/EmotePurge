using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The pure decision behind <see cref="IChannelJoinHealthService"/> (#516, plan E4). No clock, no I/O:
/// everything arrives as arguments.
/// </summary>
internal static class ChannelJoinHealthPolicy
{
    /// <param name="roster">The roster as read, or null when the key is absent.</param>
    /// <param name="desired">Active channels with their activation time, or null when the read failed.</param>
    /// <param name="nowUtc">Evaluation time.</param>
    /// <param name="apiStartedUtc">Start of the Api process, for the cold-start window.</param>
    /// <param name="rosterTtl">Roster freshness bound; also the cold-start window.</param>
    /// <param name="grace">Overdue threshold.</param>
    public static ChannelJoinHealthReport Evaluate(
        WorkerRosterSnapshot? roster,
        IReadOnlyList<ActiveChannelActivation>? desired,
        DateTime nowUtc,
        DateTime apiStartedUtc,
        TimeSpan rosterTtl,
        TimeSpan grace)
    {
        var graceSeconds = (int)grace.TotalSeconds;

        if (roster is null || nowUtc - roster.GeneratedAtUtc > rosterTtl)
        {
            var coldStart = nowUtc - apiStartedUtc <= rosterTtl;
            return Empty(coldStart ? ChannelJoinHealthStatus.Starting : ChannelJoinHealthStatus.Unavailable, graceSeconds);
        }

        if (roster.SchemaVersion < WorkerRosterSnapshot.CurrentSchemaVersion || roster.Truncated || desired is null)
        {
            return Empty(ChannelJoinHealthStatus.Unavailable, graceSeconds);
        }

        var entries = new List<ChannelJoinHealthEntry>();
        foreach (var row in roster.Channels)
        {
            if (!row.IrcIntent || row.IrcJoinConfirmed)
            {
                continue;
            }

            var seconds = row.IrcUnconfirmedSeconds ?? 0;
            var overdue = seconds > grace.TotalSeconds;
            // Twitch stated these outright; the operator's remedy is the audited Leave, and a monitor
            // that stays red for one deleted channel would hide every later failure (E11).
            var excludedReason = row.IrcJoinFailureReason is TwitchJoinFailureReasons.ChannelSuspended or TwitchJoinFailureReasons.Banned;
            entries.Add(new ChannelJoinHealthEntry(row.ChannelName, seconds, row.IrcJoinFailureReason, overdue, overdue && !excludedReason));
        }

        var withIntent = new HashSet<string>(
            roster.Channels.Where(r => r.IrcIntent).Select(r => r.ChannelName), StringComparer.OrdinalIgnoreCase);
        var missing = new List<ChannelJoinHealthMissingIntent>();
        foreach (var activation in desired)
        {
            if (withIntent.Contains(activation.ChannelName))
            {
                continue;
            }

            var since = activation.ActivatedAtUtc > roster.ProcessStartedUtc ? activation.ActivatedAtUtc : roster.ProcessStartedUtc;
            var waiting = roster.GeneratedAtUtc - since;
            var waitingSeconds = waiting > TimeSpan.Zero ? (int)waiting.TotalSeconds : 0;
            missing.Add(new ChannelJoinHealthMissingIntent(activation.ChannelName, waitingSeconds, waiting > grace));
        }

        var alarming = entries.Count(e => e.CountsForAlarm) + missing.Count(m => m.Overdue);
        var excluded = entries.Count(e => e.Overdue && !e.CountsForAlarm);
        return new ChannelJoinHealthReport(
            alarming > 0 ? ChannelJoinHealthStatus.Failing : ChannelJoinHealthStatus.Healthy,
            graceSeconds,
            entries,
            missing,
            alarming,
            excluded);
    }

    private static ChannelJoinHealthReport Empty(ChannelJoinHealthStatus status, int graceSeconds) =>
        new(status, graceSeconds, [], [], 0, 0);
}
