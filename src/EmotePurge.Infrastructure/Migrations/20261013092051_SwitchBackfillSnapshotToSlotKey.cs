using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <summary>
    /// M3 of the emote-slot epic (#365, spec 11.1, child #375): the backfill snapshot is keyed by the
    /// slot. <c>ChatLogBackfillRunEmotes.SlotId</c> becomes NOT NULL and the primary key moves from
    /// <c>(RunId, EmoteId)</c> to <c>(RunId, SlotId)</c>, so a run can hold one snapshot row per set
    /// entry — two aliases of one emote are two rows. The index <c>(RunId, SevenTvEmoteId)</c> stays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs <b>by hand in its own short window with the first main deploy after #375</b> (plan #375
    /// §9, operator decision R1 of 2026-10-13): api and worker stopped, backup, this migration over the
    /// tunnel, both new images up. Everything runs in one transaction; from its second statement on it
    /// holds ACCESS EXCLUSIVE on <c>ChatLogBackfillRunEmotes</c> with a 5-second lock timeout, and an
    /// abort leaves nothing behind. Safe in both directions for the images: an image from before #375
    /// (child 3) writes one snapshot row per emote with a non-empty slot, which fits NOT NULL and the
    /// new key, and a new image refuses to start against a database without M3.
    /// </para>
    /// <para>
    /// Rows without a slot can only come from runs enqueued before #373 (M1 left a snapshot row NULL
    /// when its emote had no usage in the run's set, M1 E9). A queued, running or paused run with such a
    /// row makes Up refuse (the M2 check (e) again — an old image could have enqueued one in between);
    /// cancel the run and repeat. A finished run's row is attached to the slot of its emote that carries
    /// <c>UsageStats</c> rows of the run's set — the oldest in D2 order (<c>AddedAt</c> ascending with
    /// NULL last, then <c>Name</c> bytewise, then <c>Id</c>), i.e. where the run's imported rows live —
    /// and keeps <c>CreatedSlot = false</c>; with no such slot the run never wrote anything for that
    /// emote and the row is deleted (plan #375 E11, operator decision R5). No seed is invented (D12).
    /// The price: a deleted row's <c>CreatedRow</c> is gone, so a placeholder it marked stays behind in
    /// the rollback cleanup like the placeholders of a run retention already removed. A notice reports
    /// both counts; on production the table is empty at the window.
    /// </para>
    /// <para>
    /// Down (D31) refuses while a run holds more than one row of an emote — one per slot, possible only
    /// after an enqueue with the slot reconcile flag on; with the flag off every run has one row per
    /// emote and Down is lossless. The way out is <see cref="EmoteSlotRollbackSql.CollapseBackfillSnapshotSql"/>
    /// first. Rows Up deleted do not come back.
    /// </para>
    /// </remarks>
    public partial class SwitchBackfillSnapshotToSlotKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // HAND-WRITTEN (1 of 2) - carry over verbatim when this migration is re-scaffolded.
            // Transaction-scoped lock timeout, then the table lock before anything reads the snapshot.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.Sql("""LOCK TABLE "ChatLogBackfillRunEmotes" IN ACCESS EXCLUSIVE MODE;""");

            // HAND-WRITTEN (2 of 2) - carry over verbatim when this migration is re-scaffolded.
            // (1) No open run with a row without a slot. (2) The NULL rule for finished runs: attach to
            // the oldest slot (D2 order) of the emote that carries rows of the run's set, else delete.
            // (3) Post-check: no slot twice in one run (the ALTER below fails on a NULL left over).
            // Each failure is loud and leaves nothing applied.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    v_count bigint;
                    v_attached bigint;
                    v_deleted bigint;
                BEGIN
                    SELECT count(DISTINCT r."Id") INTO v_count
                    FROM "ChatLogBackfillRuns" r
                    JOIN "ChatLogBackfillRunEmotes" re ON re."RunId" = r."Id"
                    WHERE r."Status" IN ('queued', 'running', 'paused') AND re."SlotId" IS NULL;
                    IF v_count > 0 THEN
                        RAISE EXCEPTION
                            'SwitchBackfillSnapshotToSlotKey check failed: % queued, running or paused backfill run(s) have a snapshot row without a slot. Cancel those runs and run the migration again. Nothing was applied.',
                            v_count;
                    END IF;

                    UPDATE "ChatLogBackfillRunEmotes" re
                    SET "SlotId" = t.slot_id
                    FROM (
                        SELECT DISTINCT ON (n."RunId", n."EmoteId")
                               n."RunId", n."EmoteId", s."Id" AS slot_id
                        FROM "ChatLogBackfillRunEmotes" n
                        JOIN "ChatLogBackfillRuns" r ON r."Id" = n."RunId"
                        JOIN "EmoteSlots" s ON s."EmoteId" = n."EmoteId"
                        WHERE n."SlotId" IS NULL
                          AND EXISTS (
                              SELECT 1 FROM "UsageStats" u
                              WHERE u."SlotId" = s."Id" AND u."EmoteSetId" = r."EmoteSetId")
                        ORDER BY n."RunId", n."EmoteId",
                                 s."AddedAt" ASC NULLS LAST, s."Name" COLLATE "C", s."Id"
                    ) t
                    WHERE re."SlotId" IS NULL
                      AND re."RunId" = t."RunId"
                      AND re."EmoteId" = t."EmoteId";
                    GET DIAGNOSTICS v_attached = ROW_COUNT;

                    DELETE FROM "ChatLogBackfillRunEmotes" WHERE "SlotId" IS NULL;
                    GET DIAGNOSTICS v_deleted = ROW_COUNT;

                    RAISE NOTICE
                        'SwitchBackfillSnapshotToSlotKey: % snapshot row(s) of finished runs without a slot attached to the slot carrying their emote''s rows in the run''s set, % deleted (no such slot).',
                        v_attached, v_deleted;

                    SELECT count(*) INTO v_count
                    FROM (
                        SELECT 1 FROM "ChatLogBackfillRunEmotes"
                        GROUP BY "RunId", "SlotId"
                        HAVING count(*) > 1
                    ) d;
                    IF v_count > 0 THEN
                        RAISE EXCEPTION
                            'SwitchBackfillSnapshotToSlotKey check failed: % (run, slot) pair(s) have more than one snapshot row. Nothing was applied.',
                            v_count;
                    END IF;
                END
                $$;
                """);

            // No defaultValue: a NULL left over must fail the ALTER, never become an empty slot id.
            migrationBuilder.AlterColumn<string>(
                name: "SlotId",
                table: "ChatLogBackfillRunEmotes",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.DropPrimaryKey(
                name: "PK_ChatLogBackfillRunEmotes",
                table: "ChatLogBackfillRunEmotes");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ChatLogBackfillRunEmotes",
                table: "ChatLogBackfillRunEmotes",
                columns: new[] { "RunId", "SlotId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // HAND-WRITTEN (1 of 2) - carry over verbatim when this migration is re-scaffolded.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.Sql("""LOCK TABLE "ChatLogBackfillRunEmotes" IN ACCESS EXCLUSIVE MODE;""");

            // HAND-WRITTEN (2 of 2) - carry over verbatim when this migration is re-scaffolded.
            // The guard (D31): the old primary key must be valid again before it is rebuilt.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    v_pairs bigint;
                BEGIN
                    SELECT count(*) INTO v_pairs
                    FROM (
                        SELECT 1 FROM "ChatLogBackfillRunEmotes"
                        GROUP BY "RunId", "EmoteId"
                        HAVING count(*) > 1
                    ) d;
                    IF v_pairs > 0 THEN
                        RAISE EXCEPTION
                            'SwitchBackfillSnapshotToSlotKey Down refused: % (run, emote) pair(s) have more than one snapshot row (one per slot). Run the collapse statement EmoteSlotRollbackSql.CollapseBackfillSnapshotSql (docs/Operations.md) in a transaction first. Nothing was reverted.',
                            v_pairs;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropPrimaryKey(
                name: "PK_ChatLogBackfillRunEmotes",
                table: "ChatLogBackfillRunEmotes");

            migrationBuilder.AlterColumn<string>(
                name: "SlotId",
                table: "ChatLogBackfillRunEmotes",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ChatLogBackfillRunEmotes",
                table: "ChatLogBackfillRunEmotes",
                columns: new[] { "RunId", "EmoteId" });
        }
    }
}
