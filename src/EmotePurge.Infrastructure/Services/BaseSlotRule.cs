using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The base slot of an (emote, emote set) pair (slot plan #373, E5/E6): the one slot a writer that
/// counts per emote — the usage flush resolving a <see cref="PendingSlotKey"/>, the backfill enqueue
/// binding a snapshot entry, the match map with the flag off — puts the pair's usage on. Shared by all
/// of them so the pair never splits across slots (E6a: with the flag off at most one slot per pair
/// carries rows; at most one unsettled seed per pair; a seed carries rows of one set only).
/// <para>
/// <b>The rule</b>, for the pair (E, X):
/// (a) the oldest slot of E that carries <c>UsageStats</c> rows of X;
/// (b) else the oldest unsettled seed of E that a snapshot row of a backfill run of the same set X
/// points at and that carries no rows of another set (a row-less enqueue seed is set-bound this way);
/// (c) else a new unsettled seed with the M1 field rules of <c>EmoteSlotSeedSql</c>: <c>Name</c>,
/// <c>IsArchived</c> and <c>ArchivedAt</c> 1:1 from the emote, <c>AddedAt</c> null,
/// <c>AddedAtFromRest</c> false, <c>SeedUnsettled</c> true, <c>LastSyncedAt</c> the caller's now.
/// "Oldest" is the D2 order (<see cref="D2Order"/>): <c>AddedAt</c> ascending with null last, then
/// <c>Name</c>, then <c>Id</c>, both ordinal — decided in memory, never by an <c>ORDER BY "Name"</c>
/// in SQL, whose result would follow the column collation (the Alpine/ICU trap). The existence of
/// rows is checked per slot candidate through the slot index <c>(SlotId, EmoteSetId, Date)</c>, never
/// by scanning all rows of the pair: the map reads this every resync tick for every channel.
/// </para>
/// <para>
/// <b>Entries.</b> <see cref="FindAsync"/> is rule (a) alone, read-only and lock-free (the map).
/// <see cref="GetOrCreateAsync"/> is (a)–(c) for a caller that already holds the locks of E6 (the
/// enqueue, whose lock order puts the user row between channel and emotes).
/// <see cref="LockAndGetOrCreateAsync"/> takes those locks itself and then does the same (flush
/// phase 2). Every entry is set-based: many pairs per call, a constant number of statements.
/// </para>
/// <para>
/// <b>Lock contract (E6).</b> Get-or-create runs in an open transaction with the <c>Channels</c> rows
/// and then the <c>Emotes</c> rows of the pairs locked through <see cref="RowLocks"/>
/// (<c>FOR NO KEY UPDATE</c>, sorted, one statement each) and reads (a)/(b) only after that. There is
/// no unique index for seeds (spec 4.1); the emote lock plus the re-read is the concurrency control
/// between two creators of the same pair. That only works if the creator also binds its new seed
/// <b>in the same transaction</b> — with the pair's first usage row (flush) or with a snapshot row of a
/// run of the set (enqueue) — because a seed with neither is invisible to the next caller's (a)/(b),
/// which would then create a second one.
/// </para>
/// <para>
/// Raw statements against the caller's transaction; nothing is tracked, nothing joins a later
/// <c>SaveChangesAsync</c>.
/// </para>
/// </summary>
internal static class BaseSlotRule
{
    /// <summary>
    /// The D2 order of the slots of one emote: <c>AddedAt</c> ascending with null last, then
    /// <c>Name</c> ordinal, then <c>Id</c> ordinal. The first element is the base slot. The same order
    /// as <c>EmoteSlotSeedSql</c> and <c>EmoteSlotRollbackSql</c> (there with <c>COLLATE "C"</c>).
    /// </summary>
    public static readonly IComparer<BaseSlotCandidate> D2Order = Comparer<BaseSlotCandidate>.Create(CompareD2);

    // (a): every slot of E that carries at least one row of X. EXISTS probes the slot index
    // (SlotId, EmoteSetId, Date) once per candidate; the candidates come from the slots' EmoteId index.
    private const string WithRowsOfSetSql = """
        SELECT input."EmoteId", input."EmoteSetId", s."Id" AS "SlotId", s."Name", s."AddedAt"
        FROM UNNEST(@emoteIds, @setIds) AS input("EmoteId", "EmoteSetId")
        JOIN "EmoteSlots" s ON s."EmoteId" = input."EmoteId"
        WHERE EXISTS (
            SELECT 1 FROM "UsageStats" u
            WHERE u."SlotId" = s."Id" AND u."EmoteSetId" = input."EmoteSetId")
        """;

    // (b): unsettled seeds of E that a snapshot row of a run of set X points at, any run status. The
    // NOT EXISTS keeps a seed that (against E6a) already carries rows of another set from being bound
    // to X; it deliberately tests "another set" and not "any row": a flush phase 1 may commit rows of X
    // onto the seed between statement (a) and this one, and the seed is still the right answer then.
    private const string ReferencedSeedsSql = """
        SELECT input."EmoteId", input."EmoteSetId", s."Id" AS "SlotId", s."Name", s."AddedAt"
        FROM UNNEST(@emoteIds, @setIds) AS input("EmoteId", "EmoteSetId")
        JOIN "EmoteSlots" s ON s."EmoteId" = input."EmoteId" AND s."SeedUnsettled"
        WHERE EXISTS (
            SELECT 1 FROM "ChatLogBackfillRunEmotes" re
            JOIN "ChatLogBackfillRuns" r ON r."Id" = re."RunId"
            WHERE re."EmoteId" = s."EmoteId" AND re."SlotId" = s."Id" AND r."EmoteSetId" = input."EmoteSetId")
          AND NOT EXISTS (
            SELECT 1 FROM "UsageStats" u
            WHERE u."SlotId" = s."Id" AND u."EmoteSetId" <> input."EmoteSetId")
        """;

    // (c): one new unsettled seed per pair, M1 field rules. The ids come from the caller so two pairs of
    // the same emote (two sets) stay apart; an emote that is gone inserts nothing and is not returned.
    private const string InsertSeedsSql = """
        INSERT INTO "EmoteSlots" ("Id", "EmoteId", "Name", "AddedAt", "AddedAtFromRest", "IsArchived", "ArchivedAt", "LastSyncedAt", "SeedUnsettled")
        SELECT input."Id", e."Id", e."Name", NULL, false, e."IsArchived", e."ArchivedAt", @now, true
        FROM UNNEST(@ids, @emoteIds) AS input("Id", "EmoteId")
        JOIN "Emotes" e ON e."Id" = input."EmoteId"
        RETURNING "Id" AS "Value"
        """;

    /// <summary>
    /// Rule (a) alone, for the match map: the base slot of every pair that already has one. A pair
    /// missing from the result has no slot carrying rows of its set (the map hands out a
    /// <see cref="PendingSlotKey"/> for it). Takes no lock, needs no transaction, writes nothing.
    /// </summary>
    public static async Task<IReadOnlyDictionary<BaseSlotPair, string>> FindAsync(
        AppDbContext db, IReadOnlyCollection<BaseSlotPair> pairs, CancellationToken cancellationToken)
    {
        var distinct = Distinct(pairs);
        if (distinct.Length == 0)
        {
            return new Dictionary<BaseSlotPair, string>();
        }

        return PickOldest(await QueryCandidatesAsync(db, WithRowsOfSetSql, distinct, cancellationToken));
    }

    /// <summary>
    /// Rules (a)–(c) for every distinct pair: returns the base slot id of each pair and the pairs whose
    /// seed this call created. Requires an open transaction and the caller's locks of E6 on the
    /// channel rows and then the emote rows of the pairs (see the type summary); the caller must bind
    /// every created seed in the same transaction. A pair whose emote does not exist is missing from
    /// the result.
    /// </summary>
    public static async Task<BaseSlotResult> GetOrCreateAsync(
        AppDbContext db, IReadOnlyCollection<BaseSlotPair> pairs, DateTime now, CancellationToken cancellationToken)
    {
        RequireTransaction(db);
        var distinct = Distinct(pairs);
        var slotIds = new Dictionary<BaseSlotPair, string>();
        var created = new HashSet<BaseSlotPair>();
        if (distinct.Length == 0)
        {
            return new BaseSlotResult(slotIds, created);
        }

        foreach (var (pair, slotId) in PickOldest(await QueryCandidatesAsync(db, WithRowsOfSetSql, distinct, cancellationToken)))
        {
            slotIds[pair] = slotId;
        }

        var open = distinct.Where(p => !slotIds.ContainsKey(p)).ToArray();
        if (open.Length > 0)
        {
            foreach (var (pair, slotId) in PickOldest(await QueryCandidatesAsync(db, ReferencedSeedsSql, open, cancellationToken)))
            {
                slotIds[pair] = slotId;
            }

            open = [.. open.Where(p => !slotIds.ContainsKey(p))];
        }

        if (open.Length > 0)
        {
            var ids = open.Select(_ => Guid.NewGuid().ToString()).ToArray();
            // Not composed with any LINQ operator, so EF sends the statement as it is.
            var inserted = (await db.Database.SqlQueryRaw<string>(
                    InsertSeedsSql,
                    [
                        new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = ToUtc(now) },
                        new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ids },
                        new NpgsqlParameter("emoteIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = open.Select(p => p.EmoteId).ToArray() },
                    ])
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < open.Length; i++)
            {
                if (inserted.Contains(ids[i]))
                {
                    slotIds[open[i]] = ids[i];
                    created.Add(open[i]);
                }
            }
        }

        return new BaseSlotResult(slotIds, created);
    }

    /// <summary>
    /// <see cref="GetOrCreateAsync"/> behind the locks of E6, taken here: the <c>Channels</c> rows of the
    /// pairs' emotes, then their <c>Emotes</c> rows, each through <see cref="RowLocks"/>. A pair whose
    /// channel is missing from the channel lock (a purge committed) or whose emote is missing from the
    /// emote lock (deleted) is dropped — missing from the result, never created — the same way the
    /// flush drops a key of a deleted emote. Requires an open transaction; the caller binds every
    /// created seed in it.
    /// </summary>
    public static async Task<BaseSlotResult> LockAndGetOrCreateAsync(
        AppDbContext db, IReadOnlyCollection<BaseSlotPair> pairs, DateTime now, CancellationToken cancellationToken)
    {
        RequireTransaction(db);
        var emoteIds = pairs.Select(p => p.EmoteId).Distinct(StringComparer.Ordinal).ToArray();
        if (emoteIds.Length == 0)
        {
            return new BaseSlotResult(new Dictionary<BaseSlotPair, string>(), new HashSet<BaseSlotPair>());
        }

        // An emote's channel never changes (the identity merge moves days and sessions, not emotes), so
        // reading it before the lock is safe; what can change is whether the rows still exist.
        var channelOfEmote = await db.Emotes
            .AsNoTracking()
            .Where(e => emoteIds.Contains(e.Id))
            .Select(e => new { e.Id, e.ChannelId })
            .ToListAsync(cancellationToken);
        var lockedChannels = (await db.LockChannelsByIdsAsync(
                channelOfEmote.Select(e => e.ChannelId).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var lockedEmotes = (await db.LockEmotesByIdsAsync(
                channelOfEmote.Where(e => lockedChannels.Contains(e.ChannelId)).Select(e => e.Id).ToArray(), cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        return await GetOrCreateAsync(db, [.. pairs.Where(p => lockedEmotes.Contains(p.EmoteId))], now, cancellationToken);
    }

    private static async Task<List<CandidateRow>> QueryCandidatesAsync(
        AppDbContext db, string sql, BaseSlotPair[] pairs, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<CandidateRow>(
                sql,
                [
                    new NpgsqlParameter("emoteIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = pairs.Select(p => p.EmoteId).ToArray() },
                    new NpgsqlParameter("setIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = pairs.Select(p => p.EmoteSetId).ToArray() },
                ])
            .ToListAsync(cancellationToken);

    private static Dictionary<BaseSlotPair, string> PickOldest(IEnumerable<CandidateRow> candidates) =>
        candidates
            .GroupBy(c => new BaseSlotPair(c.EmoteId, c.EmoteSetId))
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => new BaseSlotCandidate(c.SlotId, c.Name, c.AddedAt)).Min(D2Order)!.SlotId);

    private static BaseSlotPair[] Distinct(IReadOnlyCollection<BaseSlotPair> pairs) => [.. pairs.Distinct()];

    private static int CompareD2(BaseSlotCandidate? x, BaseSlotCandidate? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null || y is null)
        {
            return x is null ? -1 : 1;
        }

        var byDate = (x.AddedAt, y.AddedAt) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            var (a, b) => a.Value.CompareTo(b.Value),
        };
        if (byDate != 0)
        {
            return byDate;
        }

        var byName = StringComparer.Ordinal.Compare(x.Name, y.Name);
        return byName != 0 ? byName : StringComparer.Ordinal.Compare(x.SlotId, y.SlotId);
    }

    private static void RequireTransaction(AppDbContext db)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Creating a base slot needs an open transaction holding the channel and emote row locks.");
        }
    }

    // Same convention as ArchivedSlotUpsert: Npgsql refuses a non-UTC DateTime for timestamptz.
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private sealed class CandidateRow
    {
        public string EmoteId { get; set; } = string.Empty;
        public string EmoteSetId { get; set; } = string.Empty;
        public string SlotId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime? AddedAt { get; set; }
    }
}

/// <summary>The pair a base slot belongs to: the owning <c>Emote.Id</c> and the 7TV emote set the usage is counted against.</summary>
internal sealed record BaseSlotPair(string EmoteId, string EmoteSetId);

/// <summary>A slot as the D2 order sees it.</summary>
internal sealed record BaseSlotCandidate(string SlotId, string Name, DateTime? AddedAt);

/// <summary>
/// <see cref="BaseSlotRule.GetOrCreateAsync"/>: the base slot id of every resolved pair and the subset of
/// pairs whose seed this call created (rule (c)). A requested pair missing from <see cref="SlotIds"/> was
/// dropped (its emote or channel is gone).
/// </summary>
internal sealed record BaseSlotResult(
    IReadOnlyDictionary<BaseSlotPair, string> SlotIds,
    IReadOnlySet<BaseSlotPair> Created);
