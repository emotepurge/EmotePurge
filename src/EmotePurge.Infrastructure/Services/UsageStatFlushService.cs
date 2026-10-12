using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Writes the buffered usage counts (plan #373, E9): keyed by slot since M2, in two phases.
/// <para>
/// <b>Validity filter</b> (spec 6.1 point 3, AK 16). A key naming a slot is kept only if the slot
/// still exists; its <c>EmoteId</c> and channel come off the slot. A pending key
/// (<see cref="PendingSlotKey"/>, flag off only) is kept only if its emote still exists. Everything
/// else is dropped with one warning line carrying the count — a slot or emote can be hard-deleted
/// (admin purge, an emote cascade) while its count waits in the buffer, and that must not take the
/// rest of the batch down with it.
/// </para>
/// <para>
/// <b>Phase 1</b> writes every slot key in one upsert on the slot index, in its own implicit
/// transaction and without row locks beyond the foreign keys' <c>KEY SHARE</c>. With the flag on it is
/// the only phase. If it fails nothing is written and the call throws, so the caller puts the whole
/// batch back, as before the slot work.
/// </para>
/// <para>
/// <b>Phase 2</b> resolves the pending keys after phase 1 has committed, in a transaction of its own
/// with a 5-second <c>lock_timeout</c>: the channel and emote rows of the pending keys are locked in
/// the convention order (E6), each (emote, set) pair gets its base slot — creating an unsettled seed
/// together with the pair's first row where there is none (E4/E5) — and the resolved keys are upserted.
/// Seed and row commit together or not at all. Running it after phase 1 is what keeps E6a with the
/// flag off: a buffered count of the pair's old slot (a refresh in between turned the pair into a
/// pending key) is written first, and rule (a) then finds it instead of creating a second slot. A
/// failure of phase 2 is caught: phase 1 is already committed, so throwing would make the caller
/// requeue and double-count it. The pending keys come back as <see cref="UsageFlushResult.Requeued"/>
/// instead, and a contended key never holds up another channel's counts. Its known lock cycles (Z9
/// against the flag-off sync, Z10 against the flag-off delta, both emote → channel) cost exactly this
/// requeue.
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

    // Transaction-scoped, so it cannot leak into the pooled connection. Five seconds separate a stuck
    // lock holder from ordinary lock-queue latency; a timeout requeues only phase 2.
    private const string PendingPhaseLockTimeoutSql = "SET LOCAL lock_timeout = '5s'";

    public async Task<UsageFlushResult> FlushAsync(IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> usageCounts, CancellationToken cancellationToken = default)
    {
        if (usageCounts.Count == 0)
        {
            return UsageFlushResult.Empty;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var slotEntries = new List<KeyValuePair<UsageCounterKey, EmoteUsageCounts>>();
        var pendingEntries = new List<PendingEntry>();
        foreach (var entry in usageCounts)
        {
            if (PendingSlotKey.TryGetEmoteId(entry.Key.SlotId, out var emoteId))
            {
                pendingEntries.Add(new PendingEntry(entry.Key, entry.Value, emoteId));
            }
            else
            {
                slotEntries.Add(entry);
            }
        }

        // The validity filter. The owning channel name rides along on the same projection (navigation
        // joins, no GroupBy — rule 10 stays untouched) so the caller can announce exactly the channels
        // that were written without a second query. Stored channel names are already normalized
        // (rule 9).
        var slotIds = slotEntries.Select(e => e.Key.SlotId).Distinct(StringComparer.Ordinal).ToList();
        var slotById = slotIds.Count == 0
            ? new Dictionary<string, SlotOwner>(StringComparer.Ordinal)
            : (await db.EmoteSlots
                .AsNoTracking()
                .Where(s => slotIds.Contains(s.Id))
                .Select(s => new SlotOwner(s.Id, s.EmoteId, s.Emote.Channel.ChannelName))
                .ToListAsync(cancellationToken))
                .ToDictionary(s => s.SlotId, StringComparer.Ordinal);

        var pendingEmoteIds = pendingEntries.Select(e => e.EmoteId).Distinct(StringComparer.Ordinal).ToList();
        var channelOfEmote = pendingEmoteIds.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : (await db.Emotes
                .AsNoTracking()
                .Where(e => pendingEmoteIds.Contains(e.Id))
                .Select(e => new { e.Id, e.Channel.ChannelName })
                .ToListAsync(cancellationToken))
                .ToDictionary(e => e.Id, e => e.ChannelName, StringComparer.Ordinal);

        var phase1 = slotEntries
            .Where(e => slotById.ContainsKey(e.Key.SlotId))
            .Select(e => new FlushRow(slotById[e.Key.SlotId].EmoteId, e.Key.SlotId, e.Key.EmoteSetId, e.Value))
            .ToList();
        var phase2 = pendingEntries.Where(e => channelOfEmote.ContainsKey(e.EmoteId)).ToList();

        var dropped = usageCounts.Count - phase1.Count - phase2.Count;
        if (dropped > 0)
        {
            logger.LogWarning(
                "{Count} buffered usage counts dropped: their slot or emote was deleted in the meantime.",
                dropped);
        }

        var written = new HashSet<string>(StringComparer.Ordinal);
        if (phase1.Count > 0)
        {
            // Phase 1 throws through: nothing is written then, and the caller requeues the batch.
            await UpsertAsync(phase1, today, cancellationToken);
            written.UnionWith(phase1.Select(r => slotById[r.SlotId].ChannelName));
        }

        if (phase2.Count == 0)
        {
            return new UsageFlushResult(written, new Dictionary<UsageCounterKey, EmoteUsageCounts>());
        }

        try
        {
            var resolvedEmoteIds = await WritePendingAsync(phase2, today, cancellationToken);
            written.UnionWith(resolvedEmoteIds.Select(id => channelOfEmote[id]));
            return new UsageFlushResult(written, new Dictionary<UsageCounterKey, EmoteUsageCounts>());
        }
        catch (Exception ex)
        {
            // Deliberately every exception, cancellation included: phase 1 is committed, so the only
            // honest answer is "this part landed, put the rest back".
            logger.LogWarning(
                ex,
                "Usage flush could not resolve {Count} counts without a slot yet; they are handed back for the next flush.",
                phase2.Count);
            return new UsageFlushResult(written, phase2.ToDictionary(e => e.Key, e => e.Counts));
        }
    }

    // Phase 2: returns the emote ids whose rows were written. A pair whose channel or emote vanished
    // between the validity filter and the locks is dropped like a deleted one (E6).
    private async Task<IReadOnlySet<string>> WritePendingAsync(List<PendingEntry> pending, DateOnly today, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(PendingPhaseLockTimeoutSql, cancellationToken);

        var resolved = await BaseSlotRule.LockAndGetOrCreateAsync(
            db, [.. pending.Select(e => e.Pair)], DateTime.UtcNow, cancellationToken);

        var rows = pending
            .Where(e => resolved.SlotIds.ContainsKey(e.Pair))
            .Select(e => new FlushRow(e.EmoteId, resolved.SlotIds[e.Pair], e.Key.EmoteSetId, e.Counts))
            .ToList();

        var vanished = pending.Count - rows.Count;
        if (vanished > 0)
        {
            logger.LogWarning(
                "{Count} buffered usage counts dropped: their emote or channel was deleted before a slot could be created.",
                vanished);
        }

        if (rows.Count > 0)
        {
            await UpsertAsync(rows, today, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return rows.Select(r => r.EmoteId).ToHashSet(StringComparer.Ordinal);
    }

    private async Task UpsertAsync(IEnumerable<FlushRow> rows, DateOnly today, CancellationToken cancellationToken)
    {
        // Summed per resolved cell: ON CONFLICT DO UPDATE may not touch the same row twice in one
        // statement. Phase 2 can in principle resolve two keys to one cell; phase 1 keys are unique
        // already, and grouping them costs nothing.
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

    private sealed record PendingEntry(UsageCounterKey Key, EmoteUsageCounts Counts, string EmoteId)
    {
        public BaseSlotPair Pair => new(EmoteId, Key.EmoteSetId);
    }
}
