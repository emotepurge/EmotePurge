namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// Spaces out the 7TV search a channel without a stored Twitch id costs on every sync, once that
/// search has stopped paying off (design note <c>docs/Konzept-7TV-Such-Budget-2026-10-03.md</c>, 2.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>What counts as a miss:</b> every resolution that spent a search and stored no id — no 7TV
/// account, 7TV unavailable, a rename duplicate whose id another row already holds, a blocked
/// channel. Before this, each of those retried on every 60-second resync tick: 1440 searches a day
/// per stuck channel, from a bucket of 100 a minute whose overdraft locks every consumer out for an
/// hour. With the defaults the delay doubles from one minute to a ceiling of one hour, so a stuck
/// channel settles at 24 searches a day. A search the shared budget refused is not a miss — nothing
/// was asked, and nothing was learnt.
/// </para>
/// <para>
/// <b>In-process on purpose.</b> Only the Worker runs the resolution path, so there is nobody to share
/// this table with, and a restart costs at most one search per stuck channel. Persisting it would buy
/// nothing that is worth a migration. Bounded by the number of channel rows; a success forgets the
/// entry.
/// </para>
/// </remarks>
public sealed class TwitchIdResolutionBackoff(SevenTvSearchBudgetOptions options, TimeProvider timeProvider)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a resolution for this channel may spend a search now. <paramref name="retryIn"/> is the
    /// remaining wait when it may not, and zero when it may.
    /// </summary>
    public bool IsDue(string channelId, out TimeSpan retryIn)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(channelId, out var entry))
            {
                retryIn = TimeSpan.Zero;
                return true;
            }

            var now = timeProvider.GetUtcNow();
            retryIn = entry.NextAttemptAt > now ? entry.NextAttemptAt - now : TimeSpan.Zero;
            return retryIn == TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Records a resolution that spent a search and stored no id. Returns how many misses in a row
    /// this channel now has and how long until its next attempt.
    /// </summary>
    public (int ConsecutiveMisses, TimeSpan Delay) RecordMiss(string channelId)
    {
        lock (_gate)
        {
            var misses = _entries.TryGetValue(channelId, out var previous) ? previous.ConsecutiveMisses + 1 : 1;
            var delay = DelayAfter(misses, options);
            _entries[channelId] = new Entry(misses, timeProvider.GetUtcNow() + delay);
            return (misses, delay);
        }
    }

    /// <summary>Forgets the channel — called when its id has been resolved and stored.</summary>
    public void RecordSuccess(string channelId)
    {
        lock (_gate)
        {
            _entries.Remove(channelId);
        }
    }

    /// <summary>
    /// <c>base × 2^(misses − 1)</c>, capped. Public and pure so the schedule itself is testable
    /// without winding a clock through it.
    /// </summary>
    public static TimeSpan DelayAfter(int consecutiveMisses, SevenTvSearchBudgetOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consecutiveMisses);

        var ceiling = TimeSpan.FromSeconds(options.ResolutionBackoffMaxSeconds);

        // The exponent is capped well below overflow; at 2^30 any sane base is past any sane ceiling.
        var factor = Math.Pow(2, Math.Min(consecutiveMisses - 1, 30));
        var seconds = options.ResolutionBackoffBaseSeconds * factor;
        return seconds >= ceiling.TotalSeconds ? ceiling : TimeSpan.FromSeconds(seconds);
    }

    private readonly record struct Entry(int ConsecutiveMisses, DateTimeOffset NextAttemptAt);
}
