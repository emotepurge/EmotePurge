using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Creates the authoritative <c>EmoteSlot</c> rows a caller needs for 7TV set entries that were never
/// in the active set — the sibling of <see cref="ArchivedEmoteRowUpsert"/> for slots (spec §18, plan
/// #372 E20). Built for the set-session enqueue and the chat-log backfill; no production caller yet.
/// <para>
/// Only dated entries belong here: the slot is identified by <c>(EmoteId, AddedAt, Name)</c>, an
/// undated one has no authoritative identity. A created slot is archived without an archive date
/// (<c>IsArchived = true</c>, <c>ArchivedAt = null</c>: "never was in the active set"), carries the
/// REST date as its identity (<c>AddedAtFromRest = true</c>) and is not a seed.
/// </para>
/// <para>
/// One race-safe <c>INSERT … ON CONFLICT DO NOTHING</c> against the partial unique index, in key order,
/// so two callers creating overlapping keys wait for each other instead of deadlocking on index
/// locks. A slot of the same key — active or archived — stays exactly as it is. The parent
/// <c>Emote</c> rows must exist; the caller holds their row locks (<see cref="RowLocks"/>) when the
/// result decides anything. Runs immediately as a raw statement and does not join
/// <c>SaveChangesAsync</c>'s implicit transaction.
/// </para>
/// </summary>
internal static class ArchivedSlotUpsert
{
    // Inserted in key order (ORDER BY the index's own columns) for the reason in the type summary.
    // RETURNING reports exactly the rows this statement created; a key a concurrent writer inserted
    // first is a DO NOTHING and is not reported.
    private const string InsertSql = """
        INSERT INTO "EmoteSlots" ("Id", "EmoteId", "Name", "AddedAt", "AddedAtFromRest", "IsArchived", "ArchivedAt", "LastSyncedAt", "SeedUnsettled")
        SELECT input."Id", input."EmoteId", input."Name", input."AddedAt", true, true, NULL, @now, false
        FROM UNNEST(@ids, @emoteIds, @names, @addedAts) AS input("Id", "EmoteId", "Name", "AddedAt")
        ORDER BY input."EmoteId", input."AddedAt", input."Name"
        ON CONFLICT ("EmoteId", "AddedAt", "Name") WHERE "AddedAtFromRest" DO NOTHING
        RETURNING "EmoteId", "Name", "AddedAt", "Id"
        """;

    // The authoritative slot of every key, created above or by anyone else before this statement.
    private const string SelectSql = """
        SELECT s."EmoteId", s."Name", s."AddedAt", s."Id"
        FROM "EmoteSlots" s
        JOIN UNNEST(@emoteIds, @names, @addedAts) AS input("EmoteId", "Name", "AddedAt")
          ON s."EmoteId" = input."EmoteId" AND s."Name" = input."Name" AND s."AddedAt" = input."AddedAt"
        WHERE s."AddedAtFromRest"
        """;

    /// <summary>
    /// Ensures an authoritative slot exists for every distinct key of <paramref name="keys"/> and
    /// returns the slot id of each key plus the keys this call created (a key a concurrent writer
    /// created first is in the first, not in the second). Duplicate keys are collapsed here.
    /// </summary>
    public static async Task<ArchivedSlotUpsertResult> EnsureSlotsAsync(
        AppDbContext db,
        IReadOnlyCollection<ArchivedSlotKey> keys,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var distinct = keys
            .Select(k => k with { AddedAt = ToUtc(k.AddedAt) })
            .Distinct()
            .OrderBy(k => k.EmoteId, StringComparer.Ordinal)
            .ThenBy(k => k.AddedAt)
            .ThenBy(k => k.Name, StringComparer.Ordinal)
            .ToArray();
        if (distinct.Length == 0)
        {
            return new ArchivedSlotUpsertResult(new Dictionary<ArchivedSlotKey, string>(), new HashSet<ArchivedSlotKey>());
        }

        NpgsqlParameter EmoteIds() => new("emoteIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = distinct.Select(k => k.EmoteId).ToArray() };
        NpgsqlParameter Names() => new("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = distinct.Select(k => k.Name).ToArray() };
        NpgsqlParameter AddedAts() => new("addedAts", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = distinct.Select(k => k.AddedAt).ToArray() };

        // Not composed with any LINQ operator, so EF sends the statement as it is (no subquery wrap).
        var created = await db.Database.SqlQueryRaw<SlotKeyRow>(
            InsertSql,
            [
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = ToUtc(now) },
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = distinct.Select(_ => Guid.NewGuid().ToString()).ToArray() },
                EmoteIds(),
                Names(),
                AddedAts(),
            ]).ToListAsync(cancellationToken);

        var existing = await db.Database.SqlQueryRaw<SlotKeyRow>(SelectSql, [EmoteIds(), Names(), AddedAts()])
            .ToListAsync(cancellationToken);

        return new ArchivedSlotUpsertResult(
            existing.ToDictionary(r => r.ToKey(), r => r.Id),
            created.Select(r => r.ToKey()).ToHashSet());
    }

    // Same convention as ArchivedEmoteRowUpsert: Npgsql refuses a non-UTC DateTime for timestamptz.
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private sealed class SlotKeyRow
    {
        public string EmoteId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime AddedAt { get; set; }
        public string Id { get; set; } = string.Empty;

        public ArchivedSlotKey ToKey() => new(EmoteId, Name, AddedAt);
    }
}

/// <summary>The identity of an authoritative slot: the owning <c>Emote.Id</c>, the alias, the 7TV date.</summary>
internal sealed record ArchivedSlotKey(string EmoteId, string Name, DateTime AddedAt);

/// <summary>
/// <see cref="ArchivedSlotUpsert.EnsureSlotsAsync"/>: the slot id of every requested key and the subset
/// of keys this call created.
/// </summary>
internal sealed record ArchivedSlotUpsertResult(
    IReadOnlyDictionary<ArchivedSlotKey, string> SlotIds,
    IReadOnlySet<ArchivedSlotKey> Created);
