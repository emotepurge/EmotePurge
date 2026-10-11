namespace EmotePurge.Infrastructure.Migrations;

/// <summary>
/// The forward half of the emote-slot migration (spec 11.1, plan #371 E17): gives every
/// <c>UsageStats</c> row without a <c>SlotId</c> the slot of its own (EmoteId, EmoteSetId) pair,
/// creating an unsettled seed where the pair has none. The sibling of <c>EmoteSlotRollbackSql</c>,
/// deliberately not the same class: the forward and the backward path must not be confusable in
/// code or in docs/Operations.md.
/// <para>
/// Three consumers run the identical text: <c>AddEmoteSlots</c> Up (M1, on the fresh column every
/// row is NULL and every pair gets a new seed), the M2 Up of child 3 (before <c>SET NOT NULL</c>,
/// for rows the unchanged writers wrote with a NULL slot after M1) and the repair step in
/// docs/Operations.md (child 9) for rows old images wrote after an aborted window. The caller keeps
/// the writers out while it runs: M1 locks <c>UsageStats</c>, M2's ALTER does, and the repair runs
/// with worker and api stopped.
/// </para>
/// <para>
/// A pair whose rows already sit on a slot attaches its NULL rows there — after child 2 that is the
/// settled seed or the authoritative slot the settle bound the set's rows to. If several slots carry
/// rows of the pair, the oldest in D2 order wins (AddedAt ascending with NULL last, then Name
/// ordinal, then Id), the same order as the collapse in <c>EmoteSlotRollbackSql</c>: a NULL row comes
/// from a writer that counts per emote, which is exactly the share D2 assigns to the set's oldest
/// slot. Any other pair gets one new seed — Name and archive state 1:1 from <c>Emotes</c>,
/// <c>AddedAt</c> NULL, <c>SeedUnsettled</c> true, <c>LastSyncedAt</c> the transaction's
/// <c>now()</c> — and all of its NULL rows.
/// </para>
/// <para>
/// Two plain statements, no parameters, no temporary objects, so it can run twice in one
/// transaction. Rows that already have a slot are never touched, and a second run changes 0 rows
/// and creates 0 seeds; the summed row count of both statements is 0 then. The checks belong to the
/// caller: M1 and M2 each verify the result their own way.
/// </para>
/// </summary>
public static class EmoteSlotSeedSql
{
    /// <summary>
    /// Attaches every <c>UsageStats</c> row with <c>SlotId IS NULL</c>: first to the oldest slot
    /// already carrying rows of the same (EmoteId, EmoteSetId), then, for the pairs left over, to one
    /// new unsettled seed per pair. The order of the two statements is the contract — the second only
    /// sees the rows the first could not place.
    /// </summary>
    public const string AttachUnslottedUsageRowsSql =
        """
        UPDATE "UsageStats" u
        SET "SlotId" = t.slot_id
        FROM (
            SELECT DISTINCT ON (c."EmoteId", c."EmoteSetId")
                   c."EmoteId", c."EmoteSetId", s."Id" AS slot_id
            FROM "UsageStats" c
            JOIN "EmoteSlots" s ON s."Id" = c."SlotId" AND s."EmoteId" = c."EmoteId"
            WHERE EXISTS (
                SELECT 1 FROM "UsageStats" n
                WHERE n."SlotId" IS NULL
                  AND n."EmoteId" = c."EmoteId"
                  AND n."EmoteSetId" = c."EmoteSetId")
            ORDER BY c."EmoteId", c."EmoteSetId",
                     s."AddedAt" ASC NULLS LAST, s."Name" COLLATE "C", s."Id"
        ) t
        WHERE u."SlotId" IS NULL
          AND u."EmoteId" = t."EmoteId"
          AND u."EmoteSetId" = t."EmoteSetId";

        WITH seeds AS MATERIALIZED (
            SELECT gen_random_uuid()::text AS slot_id, p."EmoteId", p."EmoteSetId"
            FROM (
                SELECT DISTINCT n."EmoteId", n."EmoteSetId"
                FROM "UsageStats" n
                WHERE n."SlotId" IS NULL
            ) p
        ),
        inserted AS (
            INSERT INTO "EmoteSlots"
                ("Id", "EmoteId", "Name", "AddedAt", "AddedAtFromRest",
                 "IsArchived", "ArchivedAt", "LastSyncedAt", "SeedUnsettled")
            SELECT s.slot_id, e."Id", e."Name", NULL, false,
                   e."IsArchived", e."ArchivedAt", now(), true
            FROM seeds s
            JOIN "Emotes" e ON e."Id" = s."EmoteId"
            RETURNING "Id"
        )
        UPDATE "UsageStats" u
        SET "SlotId" = i."Id"
        FROM inserted i
        JOIN seeds s ON s.slot_id = i."Id"
        WHERE u."SlotId" IS NULL
          AND u."EmoteId" = s."EmoteId"
          AND u."EmoteSetId" = s."EmoteSetId";
        """;
}
