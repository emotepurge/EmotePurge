using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class ChannelEmoteSetObservationService(AppDbContext db) : IChannelEmoteSetObservationService
{
    public async Task RecordObservedSetAsync(string channelId, string emoteSetId, CancellationToken cancellationToken = default)
    {
        var openInterval = await FindOpenIntervalAsync(channelId, cancellationToken);

        if (openInterval is null)
        {
            // Nothing open — a first-ever sync, or the last interval was closed by leave/rename/merge
            // and nothing has reopened it since. Tracked only: this rides whatever SaveChangesAsync
            // the caller issues next (SevenTvSyncService.SyncChannelAsync commits the whole sync,
            // including this row, in one call at its own :108).
            db.ChannelEmoteSetObservations.Add(new ChannelEmoteSetObservation
            {
                ChannelId = channelId,
                SevenTvEmoteSetId = emoteSetId,
                ObservedFromUtc = DateTime.UtcNow,
            });
            return;
        }

        if (openInterval.SevenTvEmoteSetId == emoteSetId)
        {
            // Same set, still open — the routine case on every periodic resync where nothing moved.
            return;
        }

        // A genuine switch. Its own transaction and two explicit SaveChangesAsync calls — not one
        // call covering both changes — because EF Core's default command ordering sends inserts of
        // newly Added entities before updates to Modified ones with no relationship between them, and
        // an INSERT that lands before the UPDATE would momentarily hold two open rows for this
        // channel, which IX_ChannelEmoteSetObservations_ChannelId (the partial unique index) rejects.
        // Sequencing the two saves explicitly makes the order the point rather than an accident of
        // EF's batching, and the explicit transaction is what makes an exception between them undo
        // the close as well as the open — proven by ChannelEmoteSetObservationServiceTests' rollback
        // case (AK 16), which injects a failure between the two saves via a SaveChangesInterceptor.
        var now = DateTime.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        openInterval.ObservedToUtc = now;
        openInterval.ClosedBy = ChannelEmoteSetObservationClosedBy.SetSwitch;
        await db.SaveChangesAsync(cancellationToken);

        db.ChannelEmoteSetObservations.Add(new ChannelEmoteSetObservation
        {
            ChannelId = channelId,
            SevenTvEmoteSetId = emoteSetId,
            ObservedFromUtc = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CloseOpenIntervalAsync(string channelId, string closedBy, CancellationToken cancellationToken = default)
    {
        var openInterval = await FindOpenIntervalAsync(channelId, cancellationToken);
        if (openInterval is null)
        {
            return;
        }

        openInterval.ObservedToUtc = DateTime.UtcNow;
        openInterval.ClosedBy = closedBy;
        // Tracked only, deliberately: every call site (LeaveAsync, the rename branches, the
        // surviving side of a merge) already has its own SaveChangesAsync a few lines below this
        // call, and this change is meant to land in that same commit — the audit entry for the leave/
        // rename/merge and the interval it closes are one fact, not two.
    }

    private async Task<ChannelEmoteSetObservation?> FindOpenIntervalAsync(string channelId, CancellationToken cancellationToken)
    {
        return await db.ChannelEmoteSetObservations
            .SingleOrDefaultAsync(o => o.ChannelId == channelId && o.ObservedToUtc == null, cancellationToken);
    }
}
