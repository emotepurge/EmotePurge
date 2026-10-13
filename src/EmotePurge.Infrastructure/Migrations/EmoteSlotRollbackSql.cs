namespace EmotePurge.Infrastructure.Migrations;

/// <summary>
/// The collapse statements of the emote-slot rollback procedure (spec section 12.2). They run before
/// the M2-Down / M3-Down migrations and make the old unique keys valid again: (EmoteId, EmoteSetId,
/// Date) on UsageStats and (RunId, EmoteId) on ChatLogBackfillRunEmotes. docs/Operations.md shows
/// the identical text. The ordering (AddedAt ascending with NULL last, then Name ordinal, then the
/// slot id) is the D2 order of <c>EmoteSlotSeedSql</c> (E17) and must stay in sync with it.
/// <para>
/// A row whose SlotId is NULL or points at no slot sorts as if its slot had no AddedAt and no Name,
/// that is after every real slot. Each constant is one statement, needs no parameters, and a second
/// run changes nothing.
/// </para>
/// </summary>
public static class EmoteSlotRollbackSql
{
    /// <summary>
    /// Per (EmoteId, EmoteSetId, Date) group with more than one row: the row of the oldest slot
    /// receives the sums of the three counters and Source 0 if any row of the group has it, the
    /// other rows are deleted. Winners and sums come from one snapshot (CTE), so the delete does not
    /// depend on the update.
    /// </summary>
    public const string CollapseUsageStatsSql =
        """
        WITH ranked AS (
            SELECT u."Id",
                   ROW_NUMBER() OVER w AS rn,
                   COUNT(*) OVER g AS cnt,
                   SUM(u."UseCount") OVER g AS use_sum,
                   SUM(u."BotUseCount") OVER g AS bot_sum,
                   SUM(u."SharedChatUseCount") OVER g AS shared_sum,
                   MIN(u."Source") OVER g AS min_source
            FROM "UsageStats" u
            LEFT JOIN "EmoteSlots" s ON s."Id" = u."SlotId"
            WINDOW g AS (PARTITION BY u."EmoteId", u."EmoteSetId", u."Date"),
                   w AS (g ORDER BY s."AddedAt" ASC NULLS LAST, s."Name" COLLATE "C" ASC NULLS LAST,
                                    u."SlotId" ASC NULLS LAST, u."Id")
        ),
        updated AS (
            UPDATE "UsageStats" t
            SET "UseCount" = r.use_sum,
                "BotUseCount" = r.bot_sum,
                "SharedChatUseCount" = r.shared_sum,
                "Source" = r.min_source
            FROM ranked r
            WHERE t."Id" = r."Id" AND r.rn = 1 AND r.cnt > 1
            RETURNING t."Id"
        )
        DELETE FROM "UsageStats" t
        USING ranked r
        WHERE t."Id" = r."Id" AND r.rn > 1;
        """;

    /// <summary>
    /// Per (RunId, EmoteId) with more than one snapshot row: keeps the row of the oldest slot and
    /// deletes the rest. SlotId has no foreign key here, so a missing slot sorts last. The survivor
    /// takes <c>CreatedRow</c> = true if any row of the group has it: that flag is the emote's creation
    /// provenance, not the slot's, and the backfill rollback cleanup (docs/Operations.md, "Rolling
    /// back", step 3) on the older image reads it from the one row left (hand-off #371 to #375).
    /// Winners and flags come from one snapshot (CTE), so the delete does not depend on the update.
    /// </summary>
    public const string CollapseBackfillSnapshotSql =
        """
        WITH ranked AS (
            SELECT e.ctid AS row_id,
                   ROW_NUMBER() OVER w AS rn,
                   BOOL_OR(e."CreatedRow") OVER g AS any_created_row
            FROM "ChatLogBackfillRunEmotes" e
            LEFT JOIN "EmoteSlots" s ON s."Id" = e."SlotId"
            WINDOW g AS (PARTITION BY e."RunId", e."EmoteId"),
                   w AS (g ORDER BY s."AddedAt" ASC NULLS LAST, s."Name" COLLATE "C" ASC NULLS LAST,
                                    e."SlotId" ASC NULLS LAST)
        ),
        updated AS (
            UPDATE "ChatLogBackfillRunEmotes" t
            SET "CreatedRow" = true
            FROM ranked r
            WHERE t.ctid = r.row_id AND r.rn = 1 AND r.any_created_row AND NOT t."CreatedRow"
            RETURNING t."RunId"
        )
        DELETE FROM "ChatLogBackfillRunEmotes" t
        USING ranked r
        WHERE t.ctid = r.row_id AND r.rn > 1;
        """;

    /// <summary>
    /// Step 3b of the chat-log backfill rollback (docs/Operations.md, "Rolling back"), run after the
    /// placeholder cleanup (step 3) and before the imported rows are deleted (step 4): deletes the
    /// slots a backfill enqueue created (archived with <c>ArchivedAt</c> null, "was never in", and
    /// marked <c>CreatedSlot</c> in a run's snapshot) that carry no live usage. A slot with a
    /// <c>Source = 0</c> row stays, which also covers the hand-off of #373: a flush may have reused a
    /// seedless enqueue slot for live counting. A seed the enqueue merely settled is never
    /// <c>CreatedSlot</c> and stays. Deleting a slot cascades to its imported (<c>Source = 1</c>) rows,
    /// which step 4 would delete anyway. Needs the snapshot rows, like step 3. Idempotent, no parameters.
    /// </summary>
    public const string DeleteBackfillCreatedSlotsSql =
        """
        DELETE FROM "EmoteSlots" s
        WHERE s."IsArchived" AND s."ArchivedAt" IS NULL
          AND EXISTS (SELECT 1 FROM "ChatLogBackfillRunEmotes" r WHERE r."SlotId" = s."Id" AND r."CreatedSlot")
          AND NOT EXISTS (SELECT 1 FROM "UsageStats" u WHERE u."SlotId" = s."Id" AND u."Source" = 0);
        """;
}
