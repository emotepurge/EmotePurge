using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The writes that settle an unsettled seed slot onto an existing target slot (plan #372 2.4 "target
/// exists", plan #373 E10/E11a), shared by the REST reconcile and the backfill enqueue (plan #375 E10).
/// Pattern of <see cref="ArchivedSlotUpsert"/>: the caller owns the transaction and holds the channel
/// and emote row locks before calling; nothing here locks or opens a transaction of its own.
/// </summary>
internal static class SeedSettleWrites
{
    /// <summary>
    /// The usage half of a settle onto an existing target (plan #372 2.4 "target exists", plan #373
    /// E10): the seed's rows of one set — <paramref name="emoteSetId"/>, the active set for the REST
    /// settle, the previewed set for the backfill enqueue (plan #375 2.3, D12) — go to the target as a
    /// merge, not a plain re-key. A row whose (set, day) cell the target already has is added onto the
    /// target's row — all three counters,
    /// <c>Source</c> live if either row is live (the rule of
    /// <c>EmoteSlotRollbackSql.CollapseUsageStatsSql</c>) — and deleted; every other row just changes its
    /// <c>SlotId</c> and keeps its id. A plain re-key would hit the unique slot index
    /// <c>(SlotId, EmoteSetId, Date)</c> on such a cell, fail the save and with it every following tick
    /// of the channel. No live path is known to produce the collision (the flag-on map counts no emote
    /// with an open seed, E7); the merge is the belt, and the snapshot rebind (E11a) relies on it for
    /// imported rows. Runs behind the settle's locks (channel, then the emote rows), so the backfill
    /// block and flush phase 2 cannot write these slots meanwhile. Returns the rows re-keyed and the
    /// rows merged away.
    /// </summary>
    public static async Task<(int Moved, int Merged)> MoveSeedUsageAsync(
        AppDbContext db, SlotUsageRebind rebind, string emoteSetId, CancellationToken cancellationToken)
    {
        // One statement: the CTE adds each colliding seed row onto its target row and hands back the
        // seed row's id, the outer DELETE removes exactly those. The slot index makes the join 1:1.
        var merged = await db.Database.ExecuteSqlAsync(
            $"""
             WITH merged AS (
                 UPDATE "UsageStats" t
                 SET "UseCount" = t."UseCount" + s."UseCount",
                     "BotUseCount" = t."BotUseCount" + s."BotUseCount",
                     "SharedChatUseCount" = t."SharedChatUseCount" + s."SharedChatUseCount",
                     "Source" = CASE WHEN t."Source" = {(int)UsageStatSource.Live} OR s."Source" = {(int)UsageStatSource.Live}
                                     THEN {(int)UsageStatSource.Live} ELSE t."Source" END
                 FROM "UsageStats" s
                 WHERE s."SlotId" = {rebind.FromSeedId} AND s."EmoteSetId" = {emoteSetId}
                   AND t."SlotId" = {rebind.ToSlotId} AND t."EmoteSetId" = {emoteSetId}
                   AND t."Date" = s."Date"
                 RETURNING s."Id"
             )
             DELETE FROM "UsageStats" d
             USING merged m
             WHERE d."Id" = m."Id"
             """,
            cancellationToken);

        var moved = await db.UsageStats
            .Where(u => u.SlotId == rebind.FromSeedId && u.EmoteSetId == emoteSetId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.SlotId, rebind.ToSlotId), cancellationToken);
        return (moved, merged);
    }

    /// <summary>
    /// The snapshot half of a settle onto an existing target (plan #373 E11a): every backfill snapshot
    /// row that points at the seed now points at the target, with <c>CreatedSlot</c> false — the run
    /// created the seed, not the target, and the rollback cleanup must never delete the target. The
    /// snapshot key is <c>(RunId, SlotId)</c> (M3), so the move is key-safe: a run that already has a
    /// row for the target keeps that row exactly as it is (its <c>CreatedSlot</c>, <c>EmoteId</c> and
    /// <c>Name</c>) and the seed's row of that run is deleted instead of moved. The surviving rows keep
    /// their <c>(EmoteId, Name)</c>, the key the backfill worker follows when it re-reads a changed
    /// snapshot. The backfill block takes the emote rows <c>FOR SHARE</c> before it reads a snapshot
    /// slot, so it either reads the target after this commit or has committed its rows on the seed
    /// before, and <see cref="MoveSeedUsageAsync"/> has just moved them. Returns the rows moved and the
    /// rows deleted.
    /// </summary>
    public static async Task<(int Moved, int Dropped)> MoveSeedSnapshotEntriesAsync(
        AppDbContext db, SlotUsageRebind rebind, CancellationToken cancellationToken)
    {
        var dropped = await db.ChatLogBackfillRunEmotes
            .Where(e => e.SlotId == rebind.FromSeedId
                && db.ChatLogBackfillRunEmotes.Any(t => t.RunId == e.RunId && t.SlotId == rebind.ToSlotId))
            .ExecuteDeleteAsync(cancellationToken);

        var moved = await db.ChatLogBackfillRunEmotes
            .Where(e => e.SlotId == rebind.FromSeedId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.SlotId, rebind.ToSlotId)
                    .SetProperty(e => e.CreatedSlot, false),
                cancellationToken);
        return (moved, dropped);
    }
}
