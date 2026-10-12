using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <summary>
    /// M2 of the emote-slot epic (#365, spec 11.1, child #373): the non-additive key switch.
    /// <c>UsageStats.SlotId</c> becomes NOT NULL, the slot index <c>(SlotId, EmoteSetId, Date)</c>
    /// becomes the unique key (the flush's <c>ON CONFLICT</c> target) and the emote index
    /// <c>(EmoteId, EmoteSetId, Date)</c> stays as a non-unique covering index (D18).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs <b>by hand in a short window with the first main deploy after #373</b> (plan #373, E3 —
    /// split from the spec 11.2 window by operator decision of 2026-10-12): api and worker stopped,
    /// backup, this migration over the tunnel, both new images up together. Never a pre-#373 image
    /// against a database with this migration: the old flush's conflict target no longer exists.
    /// Everything runs in one transaction; from its second statement on it holds ACCESS EXCLUSIVE on
    /// <c>UsageStats</c>, and an abort leaves nothing behind.
    /// </para>
    /// <para>
    /// Order (hand-off from #371, binding): lock, then <see cref="EmoteSlotSeedSql.AttachUnslottedUsageRowsSql"/>
    /// for the rows the unchanged writers wrote with a NULL slot after M1, then the checks, then
    /// <c>SET NOT NULL</c> and the index swap. The lock comes first because the constant's statements
    /// run under READ COMMITTED and the ALTER would only lock afterwards. Since the rows were unique per
    /// <c>(EmoteId, EmoteSetId, Date)</c> before and every slot belongs to exactly one emote (check b),
    /// building the unique slot index cannot fail.
    /// </para>
    /// <para>
    /// Down (D31) refuses while any <c>(EmoteId, EmoteSetId, Date)</c> has more than one row — two
    /// slots of one emote counted on the same day, possible only after a phase with the slot reconcile
    /// flag on; with the flag off at most one slot per pair carries rows (E6a) and Down is lossless.
    /// The way out is <see cref="EmoteSlotRollbackSql.CollapseUsageStatsSql"/> first. Down keeps the
    /// slot ids and every seed created since (harmless; a new Up finds nothing to attach).
    /// </para>
    /// </remarks>
    public partial class SwitchUsageStatsToSlotKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // HAND-WRITTEN (1 of 3) - carry over verbatim when this migration is re-scaffolded.
            // Transaction-scoped lock timeout, then the table lock before anything reads UsageStats.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.Sql("""LOCK TABLE "UsageStats" IN ACCESS EXCLUSIVE MODE;""");

            // HAND-WRITTEN (2 of 3) - carry over verbatim when this migration is re-scaffolded.
            // The shared attach constant, unchanged: rows of a pair that already sits on a slot go
            // there (oldest in D2 order), every other pair gets one new unsettled seed.
            migrationBuilder.Sql(EmoteSlotSeedSql.AttachUnslottedUsageRowsSql);

            // HAND-WRITTEN (3 of 3) - carry over verbatim when this migration is re-scaffolded.
            // The checks (plan #373 §5.1, E12), each failing loudly with its own message: (a) no row
            // without a slot; (b) no row on a slot of another emote; (c) at most one unsettled seed
            // with rows per (emote, set) pair; (d) no unsettled seed with rows of two sets (D12,
            // E6a); (e) no queued, running or paused backfill run with a snapshot row without a slot
            // (M1 E9 and enqueues from before #373; the block would drop those emotes).
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    v_count bigint;
                BEGIN
                    SELECT count(*) INTO v_count FROM "UsageStats" WHERE "SlotId" IS NULL;
                    IF v_count > 0 THEN
                        RAISE EXCEPTION
                            'SwitchUsageStatsToSlotKey check (a) failed: % usage row(s) still have no slot after the attach step. Nothing was applied.',
                            v_count;
                    END IF;

                    SELECT count(*) INTO v_count
                    FROM "UsageStats" u
                    JOIN "EmoteSlots" s ON s."Id" = u."SlotId"
                    WHERE s."EmoteId" <> u."EmoteId";
                    IF v_count > 0 THEN
                        RAISE EXCEPTION
                            'SwitchUsageStatsToSlotKey check (b) failed: % usage row(s) sit on a slot of another emote. Nothing was applied.',
                            v_count;
                    END IF;

                    SELECT count(*) INTO v_count
                    FROM (
                        SELECT u."EmoteId", u."EmoteSetId"
                        FROM "UsageStats" u
                        JOIN "EmoteSlots" s ON s."Id" = u."SlotId" AND s."SeedUnsettled"
                        GROUP BY u."EmoteId", u."EmoteSetId"
                        HAVING count(DISTINCT u."SlotId") > 1
                    ) p;
                    IF v_count > 0 THEN
                        RAISE EXCEPTION
                            'SwitchUsageStatsToSlotKey check (c) failed: % (emote, emote set) pair(s) carry rows on more than one unsettled seed. Nothing was applied.',
                            v_count;
                    END IF;

                    SELECT count(*) INTO v_count
                    FROM (
                        SELECT u."SlotId"
                        FROM "UsageStats" u
                        JOIN "EmoteSlots" s ON s."Id" = u."SlotId" AND s."SeedUnsettled"
                        GROUP BY u."SlotId"
                        HAVING count(DISTINCT u."EmoteSetId") > 1
                    ) m;
                    IF v_count > 0 THEN
                        RAISE EXCEPTION
                            'SwitchUsageStatsToSlotKey check (d) failed: % unsettled seed(s) carry rows of more than one emote set. Nothing was applied.',
                            v_count;
                    END IF;

                    SELECT count(DISTINCT r."Id") INTO v_count
                    FROM "ChatLogBackfillRuns" r
                    JOIN "ChatLogBackfillRunEmotes" re ON re."RunId" = r."Id"
                    WHERE r."Status" IN ('queued', 'running', 'paused') AND re."SlotId" IS NULL;
                    IF v_count > 0 THEN
                        RAISE EXCEPTION
                            'SwitchUsageStatsToSlotKey check (e) failed: % queued, running or paused backfill run(s) have a snapshot row without a slot. Cancel those runs and run the migration again. Nothing was applied.',
                            v_count;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "SlotId",
                table: "UsageStats",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.DropIndex(
                name: "IX_UsageStats_EmoteId_EmoteSetId_Date",
                table: "UsageStats");

            migrationBuilder.DropIndex(
                name: "IX_UsageStats_SlotId_EmoteSetId_Date",
                table: "UsageStats");

            migrationBuilder.CreateIndex(
                name: "IX_UsageStats_EmoteId_EmoteSetId_Date",
                table: "UsageStats",
                columns: new[] { "EmoteId", "EmoteSetId", "Date" })
                .Annotation("Npgsql:IndexInclude", new[] { "UseCount" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageStats_SlotId_EmoteSetId_Date",
                table: "UsageStats",
                columns: new[] { "SlotId", "EmoteSetId", "Date" },
                unique: true)
                .Annotation("Npgsql:IndexInclude", new[] { "UseCount" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // HAND-WRITTEN (1 of 2) - carry over verbatim when this migration is re-scaffolded.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.Sql("""LOCK TABLE "UsageStats" IN ACCESS EXCLUSIVE MODE;""");

            // HAND-WRITTEN (2 of 2) - carry over verbatim when this migration is re-scaffolded.
            // The guard (D31): the old unique key must be valid again before it is rebuilt.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    v_cells bigint;
                BEGIN
                    SELECT count(*) INTO v_cells
                    FROM (
                        SELECT 1 FROM "UsageStats"
                        GROUP BY "EmoteId", "EmoteSetId", "Date"
                        HAVING count(*) > 1
                    ) d;
                    IF v_cells > 0 THEN
                        RAISE EXCEPTION
                            'SwitchUsageStatsToSlotKey Down refused: % (emote, emote set, date) cell(s) have more than one usage row (one per slot). Run the collapse statement EmoteSlotRollbackSql.CollapseUsageStatsSql (docs/Operations.md) in a transaction first. Nothing was reverted.',
                            v_cells;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_UsageStats_EmoteId_EmoteSetId_Date",
                table: "UsageStats");

            migrationBuilder.DropIndex(
                name: "IX_UsageStats_SlotId_EmoteSetId_Date",
                table: "UsageStats");

            migrationBuilder.AlterColumn<string>(
                name: "SlotId",
                table: "UsageStats",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "IX_UsageStats_EmoteId_EmoteSetId_Date",
                table: "UsageStats",
                columns: new[] { "EmoteId", "EmoteSetId", "Date" },
                unique: true)
                .Annotation("Npgsql:IndexInclude", new[] { "UseCount" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageStats_SlotId_EmoteSetId_Date",
                table: "UsageStats",
                columns: new[] { "SlotId", "EmoteSetId", "Date" })
                .Annotation("Npgsql:IndexInclude", new[] { "UseCount" });
        }
    }
}
