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
    /// deletes the rest. SlotId has no foreign key here, so a missing slot sorts last.
    /// </summary>
    public const string CollapseBackfillSnapshotSql =
        """
        WITH ranked AS (
            SELECT e.ctid AS row_id,
                   ROW_NUMBER() OVER (
                       PARTITION BY e."RunId", e."EmoteId"
                       ORDER BY s."AddedAt" ASC NULLS LAST, s."Name" COLLATE "C" ASC NULLS LAST,
                                e."SlotId" ASC NULLS LAST) AS rn
            FROM "ChatLogBackfillRunEmotes" e
            LEFT JOIN "EmoteSlots" s ON s."Id" = e."SlotId"
        )
        DELETE FROM "ChatLogBackfillRunEmotes" t
        USING ranked r
        WHERE t.ctid = r.row_id AND r.rn > 1;
        """;
}
