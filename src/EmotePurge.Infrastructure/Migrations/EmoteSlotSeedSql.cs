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
/// docs/Operations.md (child 9) for rows old images wrote after an aborted window. The caller holds
/// ACCESS EXCLUSIVE on <c>UsageStats</c> (with a <c>lock_timeout</c>) for the whole run, or has
/// every writer stopped: the statements run under READ COMMITTED, so a row inserted between them
/// would get a second seed for a pair that already has a slot. M1 takes the lock first thing; M2
/// must take it before the constant, because its ALTER only locks afterwards; the repair runs in
/// one transaction with worker and api stopped.
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
/// Plain statements, no parameters, no temporary objects, so it can run twice in one transaction.
/// Rows that already have a slot are never touched, and a second run changes 0 rows and creates 0
/// seeds; the summed row count of the statements is 0 then. The checks belong to the caller: M1 and
/// M2 each verify the result their own way.
/// </para>
/// <para>
/// The text starts with <c>ANALYZE "UsageStats"</c> because the planner's estimate for
/// <c>"SlotId" IS NULL</c> is wrong in every consumer's state: on M1's fresh column there are no
/// statistics (the default guess is 0.5% of the table, all rows are NULL), and before M2 or the
/// repair the statistics typically date from after M1 and say no row is NULL while the old writers
/// have added many. Both attach statements join two inputs filtered on that predicate; with an
/// estimate of a few rows on each side the planner picks a nested loop with a join filter, which
/// is quadratic in the actual counts — measured on a dev-DB copy (120k rows, 4.3k pairs), the
/// second statement exceeded EF's 30-second command timeout, and the first did as well on a
/// simulated repair state. ANALYZE reads a fixed sample (300 × <c>default_statistics_target</c>
/// rows), so its cost does not grow with the table, and it is allowed inside the caller's
/// transaction. The seed statement additionally joins the materialized seed list on the pair with
/// plain equality conditions instead of the data-modifying CTE's output, so it no longer depends
/// on an estimate of what the INSERT returns. If the migration role does not own the table,
/// ANALYZE only warns and the statements run on whatever statistics exist.
/// </para>
/// </summary>
public static class EmoteSlotSeedSql
{
    /// <summary>
    /// Attaches every <c>UsageStats</c> row with <c>SlotId IS NULL</c>: first to the oldest slot
    /// already carrying rows of the same (EmoteId, EmoteSetId), then, for the pairs left over, to one
    /// new unsettled seed per pair. The order of the two attach statements is the contract — the
    /// second only sees the rows the first could not place. Every seed the second statement lists is
    /// inserted (the INSERT reads the seed list without a filter, and <c>Emotes</c> is already joined
    /// there), so a row only ever points at a slot that exists or is inserted by the same statement;
    /// the foreign key's check at the end of the statement sees those inserted slots.
    /// </summary>
    public const string AttachUnslottedUsageRowsSql =
        """
        ANALYZE "UsageStats";

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
            SELECT gen_random_uuid()::text AS slot_id, e."Id" AS "EmoteId", p."EmoteSetId",
                   e."Name", e."IsArchived", e."ArchivedAt"
            FROM (
                SELECT DISTINCT n."EmoteId", n."EmoteSetId"
                FROM "UsageStats" n
                WHERE n."SlotId" IS NULL
            ) p
            JOIN "Emotes" e ON e."Id" = p."EmoteId"
        ),
        inserted AS (
            INSERT INTO "EmoteSlots"
                ("Id", "EmoteId", "Name", "AddedAt", "AddedAtFromRest",
                 "IsArchived", "ArchivedAt", "LastSyncedAt", "SeedUnsettled")
            SELECT s.slot_id, s."EmoteId", s."Name", NULL, false,
                   s."IsArchived", s."ArchivedAt", now(), true
            FROM seeds s
        )
        UPDATE "UsageStats" u
        SET "SlotId" = s.slot_id
        FROM seeds s
        WHERE u."SlotId" IS NULL
          AND u."EmoteId" = s."EmoteId"
          AND u."EmoteSetId" = s."EmoteSetId";
        """;
}
