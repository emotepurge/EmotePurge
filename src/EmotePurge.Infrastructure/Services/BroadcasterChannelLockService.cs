using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class BroadcasterChannelLockService(AppDbContext db) : IBroadcasterChannelLockService
{
    public IQueryable<BroadcasterChannelLock> Locks => db.BroadcasterChannelLocks.AsNoTracking();

    public async Task<DateTime?> GetLockedAtUtcAsync(string? twitchChannelId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(twitchChannelId))
        {
            return null;
        }

        return await db.BroadcasterChannelLocks
            .AsNoTracking()
            .Where(l => l.TwitchChannelId == twitchChannelId)
            .Select(l => (DateTime?)l.LockedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task LockAsync(string twitchChannelId, DateTime lockedAtUtc, CancellationToken cancellationToken = default)
    {
        var existing = await db.BroadcasterChannelLocks
            .FirstOrDefaultAsync(l => l.TwitchChannelId == twitchChannelId, cancellationToken);
        if (existing is null)
        {
            db.BroadcasterChannelLocks.Add(new BroadcasterChannelLock
            {
                TwitchChannelId = twitchChannelId,
                LockedAtUtc = lockedAtUtc,
            });
        }
        else
        {
            existing.LockedAtUtc = lockedAtUtc;
        }
    }

    public async Task<bool> UnlockAsync(string twitchChannelId, CancellationToken cancellationToken = default)
    {
        var existing = await db.BroadcasterChannelLocks
            .FirstOrDefaultAsync(l => l.TwitchChannelId == twitchChannelId, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        db.BroadcasterChannelLocks.Remove(existing);
        return true;
    }
}
