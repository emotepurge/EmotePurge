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
            // and nothing has reopened it since.
            //
            // Re-checked here rather than trusted: SevenTvPeriodicResyncWorker takes its channel list
            // once per tick and SyncChannelAsync never re-reads IsBotActive mid-tick, so a leave that
            // commits after the tick's snapshot and before this line runs would otherwise leave an
            // open interval on a channel nothing is tracking anymore — and nothing would ever close
            // it, since a later rejoin onto the same set just takes the "same set, do nothing" branch
            // below instead of opening a fresh row.
            //
            // This is not a hopeful TOCTOU check: every deactivation (ChannelDeactivation.cs, shared by
            // LeaveAsync and the identity reconcile's objection gate) commits the close of an open
            // interval and the IsBotActive flip in the *same* SaveChangesAsync. That
            // atomicity is what makes this safe without a lock — any snapshot that already shows "no
            // open row" here necessarily also shows IsBotActive = false, because the two facts are
            // written together and can never become visible one without the other.
            if (!await IsChannelActiveAsync(channelId, cancellationToken))
            {
                return;
            }

            // Tracked only: this rides whatever SaveChangesAsync the caller issues next
            // (SevenTvSyncService.SyncChannelAsync commits the whole sync, including this row, in one
            // call at its own :108).
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

        // Re-checked here, inside the transaction and immediately before the first write: the read
        // above that decided this was a genuine switch happened before this transaction began, and
        // can already be stale by now. Same atomicity argument as the open branch — LeaveAsync closes
        // this exact interval and flips IsBotActive in one SaveChangesAsync, so if that flag now
        // reads false, the interval below is already closed, and closing it again here would stomp
        // Leave's ClosedBy with SetSwitch before opening a new interval that nothing is watching.
        // This closes the "leave commits, then this method runs" ordering: Postgres serializes the
        // two UPDATEs a leave-then-switch race would otherwise issue against the very same interval
        // row, so leave's commit becomes visible to this read once it has happened.
        //
        // Known residual, not fixed here: the opposite ordering — this check passes, the transaction
        // closes the old interval and opens and commits a new one, and only *then* does a concurrent
        // leave run — can still leave that brand-new interval open on a channel that has since left.
        // Preventing that would need a lock spanning this whole transaction, which spec decision E10
        // rejected for a neighbouring race at this severity, and the close cannot be reconstructed
        // honestly after the fact (the leave time lives only in the audit log, and spec 4.3 refuses
        // to derive an observation interval from it). Accepted, not chased.
        if (!await IsChannelActiveAsync(channelId, cancellationToken))
        {
            return;
        }

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

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ChannelEmoteSetObservationInterval>>> ListIntervalsByChannelAsync(
        string channelId, CancellationToken cancellationToken = default)
    {
        // AsNoTracking + a scalar projection: this is a pure read for a response payload, never a
        // write path, and ordering by ObservedFromUtc before grouping is what makes each set's list
        // "ascending" (spec 6.1) without a second sort once grouped — LINQ-to-Objects preserves a
        // source's order within each group.
        var rows = await db.ChannelEmoteSetObservations
            .AsNoTracking()
            .Where(o => o.ChannelId == channelId)
            .OrderBy(o => o.ObservedFromUtc)
            .Select(o => new { o.SevenTvEmoteSetId, o.ObservedFromUtc, o.ObservedToUtc })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.SevenTvEmoteSetId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ChannelEmoteSetObservationInterval>)group
                    .Select(row => new ChannelEmoteSetObservationInterval(row.ObservedFromUtc, row.ObservedToUtc))
                    .ToList());
    }

    private async Task<ChannelEmoteSetObservation?> FindOpenIntervalAsync(string channelId, CancellationToken cancellationToken)
    {
        return await db.ChannelEmoteSetObservations
            .SingleOrDefaultAsync(o => o.ChannelId == channelId && o.ObservedToUtc == null, cancellationToken);
    }

    // A scalar projection, deliberately: unlike a full-entity query, this always issues a fresh SQL
    // SELECT rather than risking a return of some already-tracked Channel instance's (possibly
    // stale) in-memory value, which is exactly the freshness both call sites above depend on.
    // SingleOrDefaultAsync rather than SingleAsync so a channel id that somehow does not resolve
    // reads as "not active" instead of throwing — every real caller passes an id it just loaded, but
    // failing closed here costs nothing and asks for nothing.
    private async Task<bool> IsChannelActiveAsync(string channelId, CancellationToken cancellationToken)
    {
        return await db.Channels
            .Where(c => c.Id == channelId)
            .Select(c => c.IsBotActive)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
