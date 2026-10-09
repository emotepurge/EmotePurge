using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class LiveCoverageService(
    AppDbContext db,
    IExcludedChannelFilter excludedChannelFilter,
    IBroadcasterChannelLockService broadcasterChannelLocks) : ILiveCoverageService
{
    private const int MinutesPerDay = 1440;

    public async Task<int> AddLiveMinutesAsync(
        IReadOnlyCollection<(string Login, string UserId)> liveChannels, DateOnly dateUtc, int minutes, CancellationToken cancellationToken = default)
    {
        if (liveChannels.Count == 0 || minutes <= 0)
        {
            return 0;
        }

        // The env list and the broadcaster lock (#245, plan P15), checked on Helix's user id rather
        // than on the row's stored id: the roster already drops a row whose stored id is blocked, but a
        // row created during a Helix outage carries no id yet, and Helix's answer is the truth of the
        // moment. One batch lookup for the lock. No id is written back here — that stays the sync's
        // and the identity reconcile's job.
        var userIds = liveChannels.Select(c => c.UserId).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        var lockedIds = userIds.Count == 0
            ? []
            : (await broadcasterChannelLocks.Locks
                .Where(l => userIds.Contains(l.TwitchChannelId))
                .Select(l => l.TwitchChannelId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var normalized = liveChannels
            .Where(c => !excludedChannelFilter.IsExcluded(c.UserId) && !lockedIds.Contains(c.UserId))
            .Select(c => ChannelName.Normalize(c.Login))
            .Distinct()
            .ToList();
        if (normalized.Count == 0)
        {
            return 0;
        }

        var channelIds = await db.Channels
            .Where(c => normalized.Contains(c.ChannelName))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (channelIds.Count == 0)
        {
            return 0;
        }

        // Read-modify-write instead of an upsert statement: one worker is the only writer, one
        // call per poll tick, a handful of rows — the simple form wins over ON CONFLICT here.
        var existing = await db.ChannelLiveDays
            .Where(d => d.Date == dateUtc && channelIds.Contains(d.ChannelId))
            .ToDictionaryAsync(d => d.ChannelId, cancellationToken);

        foreach (var channelId in channelIds)
        {
            if (existing.TryGetValue(channelId, out var row))
            {
                row.LiveMinutes = Math.Min(MinutesPerDay, row.LiveMinutes + minutes);
            }
            else
            {
                db.ChannelLiveDays.Add(new ChannelLiveDay
                {
                    ChannelId = channelId,
                    Date = dateUtc,
                    LiveMinutes = Math.Min(MinutesPerDay, minutes)
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return channelIds.Count;
    }
}
