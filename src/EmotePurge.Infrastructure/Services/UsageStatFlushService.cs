using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Writes the buffered usage counts (plan #373, E9), keyed by slot since M2.
/// <para>
/// <b>Validity filter</b> (spec 6.1 point 3, AK 16). A key is kept only if it names a slot that still
/// exists; its <c>EmoteId</c> and channel come off the slot. Everything else is dropped with one warning
/// line carrying the count — a slot can be hard-deleted (admin purge, an emote cascade) while its count
/// waits in the buffer, and that must not take the rest of the batch down with it. The filter also
/// drops a key that names no slot at all.
/// </para>
/// <para>
/// <b>Write:</b> every kept key in one upsert on the slot index, in its own implicit transaction and
/// without row locks beyond the foreign keys' <c>KEY SHARE</c>. If it fails nothing is written and the
/// call throws, so the caller puts the whole batch back.
/// </para>
/// </summary>
public class UsageStatFlushService(AppDbContext db, ILogger<UsageStatFlushService> logger) : IUsageStatFlushService
{
    // Atomic upsert rather than read-then-insert. A version that decided per row between += and Add
    // based on a prior SELECT was only correct while there was exactly one writer: the final flush in
    // UsageFlushWorker.StopAsync can overlap a regular 30s tick, and both would then see "row missing"
    // and insert it — one loses on the unique index and its entire batch is discarded. Holds for all
    // three columns: "UseCount", "BotUseCount" and "SharedChatUseCount" are addended independently.
    //
    // The conflict target is the slot index (D18): since M2 a cell is (SlotId, EmoteSetId, Date), and
    // two slots of one emote write two rows on the same day (AK 14). EmoteId is still written — it is
    // the cascade path and the key of every emote-wide reader.
    //
    // No "only rows with Human > 0" filter: a batch where an emote came only from bots still gets a row
    // (UseCount = 0, BotUseCount = n) — bot usage is preserved, not discarded (E1). Same for a batch
    // where an emote came only from a foreign room during a Shared Chat session (#73, B6).
    private const string UpsertSql = """
        INSERT INTO "UsageStats" ("EmoteId", "SlotId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
        SELECT input."EmoteId", input."SlotId", input."EmoteSetId", @date, input."UseCount", input."BotUseCount", input."SharedChatUseCount"
        FROM UNNEST(@emoteIds, @slotIds, @emoteSetIds, @useCounts, @botUseCounts, @sharedChatUseCounts)
            AS input("EmoteId", "SlotId", "EmoteSetId", "UseCount", "BotUseCount", "SharedChatUseCount")
        ON CONFLICT ("SlotId", "EmoteSetId", "Date")
        DO UPDATE SET
            "UseCount" = "UsageStats"."UseCount" + EXCLUDED."UseCount",
            "BotUseCount" = "UsageStats"."BotUseCount" + EXCLUDED."BotUseCount",
            "SharedChatUseCount" = "UsageStats"."SharedChatUseCount" + EXCLUDED."SharedChatUseCount";
        """;

    public async Task<UsageFlushResult> FlushAsync(IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> usageCounts, CancellationToken cancellationToken = default)
    {
        if (usageCounts.Count == 0)
        {
            return UsageFlushResult.Empty;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // The validity filter. The owning channel name rides along on the same projection (navigation
        // joins, no GroupBy — rule 10 stays untouched) so the caller can announce exactly the channels
        // that were written without a second query. Stored channel names are already normalized
        // (rule 9).
        var slotIds = usageCounts.Keys.Select(k => k.SlotId).Distinct(StringComparer.Ordinal).ToList();
        var slotById = (await db.EmoteSlots
                .AsNoTracking()
                .Where(s => slotIds.Contains(s.Id))
                .Select(s => new SlotOwner(s.Id, s.EmoteId, s.Emote.Channel.ChannelName))
                .ToListAsync(cancellationToken))
            .ToDictionary(s => s.SlotId, StringComparer.Ordinal);

        var rows = usageCounts
            .Where(e => slotById.ContainsKey(e.Key.SlotId))
            .Select(e => new FlushRow(slotById[e.Key.SlotId].EmoteId, e.Key.SlotId, e.Key.EmoteSetId, e.Value))
            .ToList();

        var dropped = usageCounts.Count - rows.Count;
        if (dropped > 0)
        {
            logger.LogWarning(
                "{Count} buffered usage counts dropped: their slot no longer exists.",
                dropped);
        }

        if (rows.Count == 0)
        {
            return UsageFlushResult.Empty;
        }

        // Throws through: nothing is written then, and the caller requeues the batch.
        await UpsertAsync(rows, today, cancellationToken);
        return new UsageFlushResult(rows.Select(r => slotById[r.SlotId].ChannelName).ToHashSet(StringComparer.Ordinal));
    }

    private async Task UpsertAsync(IEnumerable<FlushRow> rows, DateOnly today, CancellationToken cancellationToken)
    {
        // Summed per cell: ON CONFLICT DO UPDATE may not touch the same row twice in one statement.
        // The buffer's keys are unique already, and grouping them costs nothing.
        var cells = rows
            .GroupBy(r => (r.SlotId, r.EmoteSetId))
            .Select(g => new FlushRow(
                g.First().EmoteId,
                g.Key.SlotId,
                g.Key.EmoteSetId,
                new EmoteUsageCounts(g.Sum(r => r.Counts.Human), g.Sum(r => r.Counts.Bot), g.Sum(r => r.Counts.SharedChat))))
            .ToList();

        await db.Database.ExecuteSqlRawAsync(
            UpsertSql,
            [
                new NpgsqlParameter("date", NpgsqlDbType.Date) { Value = today },
                new NpgsqlParameter("emoteIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cells.Select(c => c.EmoteId).ToArray() },
                new NpgsqlParameter("slotIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cells.Select(c => c.SlotId).ToArray() },
                new NpgsqlParameter("emoteSetIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cells.Select(c => c.EmoteSetId).ToArray() },
                new NpgsqlParameter("useCounts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = cells.Select(c => c.Counts.Human).ToArray() },
                new NpgsqlParameter("botUseCounts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = cells.Select(c => c.Counts.Bot).ToArray() },
                new NpgsqlParameter("sharedChatUseCounts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = cells.Select(c => c.Counts.SharedChat).ToArray() },
            ],
            cancellationToken);
    }

    private sealed record SlotOwner(string SlotId, string EmoteId, string ChannelName);

    private sealed record FlushRow(string EmoteId, string SlotId, string EmoteSetId, EmoteUsageCounts Counts);
}
