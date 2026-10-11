using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <summary>
    /// M1 of the emote-slot epic (#365, spec 11.1, child #371): the additive half. Creates
    /// <c>EmoteSlots</c>, gives <c>UsageStats</c> a nullable <c>SlotId</c> and fills it with one
    /// unsettled seed per (EmoteId, EmoteSetId) pair that already has usage rows, adds the snapshot's
    /// <c>SlotId</c>/<c>CreatedSlot</c> and the ballot's <c>NamesAtCreation</c>. No writer changes;
    /// the key switch is M2 (child 3), the snapshot's M3 (child 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs <b>by hand, inside the maintenance window</b> together with M2 and M3 (spec 11.2);
    /// production never migrates at startup. Everything runs in one transaction (EF Core's default;
    /// Npgsql has transactional DDL), and from the first statement on it holds <c>ACCESS
    /// EXCLUSIVE</c> on <c>UsageStats</c>, so the seed sees the final row set. An abort leaves no
    /// table, no column and no history entry behind.
    /// </para>
    /// <para>
    /// The seed is <see cref="EmoteSlotSeedSql.AttachUnslottedUsageRowsSql"/>, not a copy of it: on
    /// the fresh column every row is NULL and no slot exists, so every pair gets a new seed with
    /// Name and archive state 1:1 from <c>Emotes</c>, <c>AddedAt</c> NULL and
    /// <c>SeedUnsettled</c> true. The same constant repairs rows that the old images keep writing
    /// with a NULL slot after M1 — on production only after an aborted window, when they run on M1
    /// (spec 11.2 step 4) — and M2's Up runs it again before <c>SET NOT NULL</c>.
    /// </para>
    /// <para>
    /// The rollback target is the migration directly before this one as <c>dotnet ef migrations
    /// list</c> shows it — today <c>20261011113207_AddSystemLoadSampling</c>. Spec 12.2 step 3 still
    /// names <c>20261009102325_AddChatLogBackfill</c>, which predates #380 and would revert the load
    /// sampling as well (plan #371, E1).
    /// </para>
    /// </remarks>
    public partial class AddEmoteSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // HAND-WRITTEN (1 of 4) - carry over verbatim when this migration is re-scaffolded.
            // Transaction-scoped, so it cannot leak into the session: five seconds separate a
            // forgotten psql session from ordinary lock-queue latency. The ALTER below would take
            // ACCESS EXCLUSIVE anyway; taking it first means no flush writes between the column and
            // the seed, so the seed sees the final row set.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.Sql("""LOCK TABLE "UsageStats" IN ACCESS EXCLUSIVE MODE;""");

            migrationBuilder.CreateTable(
                name: "EmoteSlots",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    EmoteId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AddedAtFromRest = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SeedUnsettled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmoteSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmoteSlots_Emotes_EmoteId",
                        column: x => x.EmoteId,
                        principalTable: "Emotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmoteSlots_EmoteId_AddedAt_Name_Authoritative",
                table: "EmoteSlots",
                columns: new[] { "EmoteId", "AddedAt", "Name" },
                unique: true,
                filter: "\"AddedAtFromRest\"");

            migrationBuilder.CreateIndex(
                name: "IX_EmoteSlots_EmoteId_Name",
                table: "EmoteSlots",
                columns: new[] { "EmoteId", "Name" });

            migrationBuilder.AddColumn<string>(
                name: "SlotId",
                table: "UsageStats",
                type: "text",
                nullable: true);

            // HAND-WRITTEN (2 of 4) - carry over verbatim when this migration is re-scaffolded.
            // The seed: one unsettled seed per (EmoteId, EmoteSetId) pair, each row on its own
            // pair's seed. Shared with M2 and the repair step; see the class remarks.
            migrationBuilder.Sql(EmoteSlotSeedSql.AttachUnslottedUsageRowsSql);

            // HAND-WRITTEN (3 of 4) - carry over verbatim when this migration is re-scaffolded.
            // The abort check, in front of the foreign key. On a consistent database it cannot fire
            // (the FK to Emotes and the lock above rule out every shape), so it has no test case of
            // its own — it is the belt, not a path. Checks (c) and (d) only hold on the fresh column;
            // M2 formulates its own check after its run of the constant. Together (a), (c) and (d)
            // mean every pair has exactly one seed and every seed exactly one pair.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    v_unslotted bigint;
                    v_foreign   bigint;
                    v_multi_set bigint;
                    v_seeds     bigint;
                    v_pairs     bigint;
                BEGIN
                    SELECT count(*) INTO v_unslotted FROM "UsageStats" WHERE "SlotId" IS NULL;

                    SELECT count(*) INTO v_foreign
                    FROM "UsageStats" u
                    WHERE u."SlotId" IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM "EmoteSlots" s
                          WHERE s."Id" = u."SlotId" AND s."EmoteId" = u."EmoteId");

                    SELECT count(*) INTO v_multi_set
                    FROM (
                        SELECT "SlotId" FROM "UsageStats"
                        WHERE "SlotId" IS NOT NULL
                        GROUP BY "SlotId"
                        HAVING count(DISTINCT "EmoteSetId") > 1
                    ) m;

                    SELECT count(*) INTO v_seeds FROM "EmoteSlots";

                    SELECT count(*) INTO v_pairs
                    FROM (SELECT DISTINCT "EmoteId", "EmoteSetId" FROM "UsageStats") p;

                    IF v_unslotted > 0 OR v_foreign > 0 OR v_multi_set > 0 OR v_seeds <> v_pairs THEN
                        RAISE EXCEPTION
                            'AddEmoteSlots seed check failed: % usage row(s) without a slot, % row(s) on a missing slot or a slot of another emote, % slot(s) carrying more than one emote set, % seed(s) for % (emote, emote set) pair(s). Nothing was applied.',
                            v_unslotted, v_foreign, v_multi_set, v_seeds, v_pairs;
                    END IF;
                END
                $$;
                """);

            // After the attach, so validating the foreign key is one pass over a filled column.
            migrationBuilder.AddForeignKey(
                name: "FK_UsageStats_EmoteSlots_SlotId",
                table: "UsageStats",
                column: "SlotId",
                principalTable: "EmoteSlots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.CreateIndex(
                name: "IX_UsageStats_SlotId_EmoteSetId_Date",
                table: "UsageStats",
                columns: new[] { "SlotId", "EmoteSetId", "Date" })
                .Annotation("Npgsql:IndexInclude", new[] { "UseCount" });

            migrationBuilder.AddColumn<string>(
                name: "SlotId",
                table: "ChatLogBackfillRunEmotes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CreatedSlot",
                table: "ChatLogBackfillRunEmotes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // HAND-WRITTEN (4 of 4) - carry over verbatim when this migration is re-scaffolded.
            // A snapshot row gets the slot that carries its emote's rows in the run's set — after the
            // seed exactly that pair's seed, and the check above guarantees every row of the pair sits
            // on it, so whichever row the join picks yields the same value. A snapshot row without
            // usage in the run's set has no seed and stays NULL (plan #371, E9); on production the
            // table is empty at the window (spec 11.2 step 3a).
            migrationBuilder.Sql(
                """
                UPDATE "ChatLogBackfillRunEmotes" re
                SET "SlotId" = u."SlotId"
                FROM "ChatLogBackfillRuns" r
                JOIN "UsageStats" u ON u."EmoteSetId" = r."EmoteSetId"
                WHERE r."Id" = re."RunId"
                  AND u."EmoteId" = re."EmoteId";
                """);

            migrationBuilder.AddColumn<string[]>(
                name: "NamesAtCreation",
                table: "VoteSessionEmotes",
                type: "text[]",
                nullable: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// No guard: before M2 every row still has its <c>(EmoteId, EmoteSetId, Date)</c> key, so
        /// dropping the slot column loses only the seed assignment, which a new Up recreates with new
        /// ids. After M2 this Down is only reachable through M2's Down.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UsageStats_EmoteSlots_SlotId",
                table: "UsageStats");

            migrationBuilder.DropTable(
                name: "EmoteSlots");

            migrationBuilder.DropIndex(
                name: "IX_UsageStats_SlotId_EmoteSetId_Date",
                table: "UsageStats");

            migrationBuilder.DropColumn(
                name: "NamesAtCreation",
                table: "VoteSessionEmotes");

            migrationBuilder.DropColumn(
                name: "SlotId",
                table: "UsageStats");

            migrationBuilder.DropColumn(
                name: "CreatedSlot",
                table: "ChatLogBackfillRunEmotes");

            migrationBuilder.DropColumn(
                name: "SlotId",
                table: "ChatLogBackfillRunEmotes");
        }
    }
}
