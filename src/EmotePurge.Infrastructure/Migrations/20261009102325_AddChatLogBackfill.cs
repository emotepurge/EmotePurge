using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChatLogBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "UsageStats",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ChatLogBackfillProviderState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    CooldownUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRateLimitedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatLogBackfillProviderState", x => x.Id);
                    table.CheckConstraint("CK_ChatLogBackfillProviderState_SingleRow", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "ChatLogBackfillRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RequestedMonths = table.Column<int>(type: "integer", nullable: false),
                    WindowFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    WindowTo = table.Column<DateOnly>(type: "date", nullable: false),
                    WeeksTotal = table.Column<int>(type: "integer", nullable: false),
                    WeeksDone = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    EmoteSetId = table.Column<string>(type: "text", nullable: false),
                    EmoteSetName = table.Column<string>(type: "text", nullable: true),
                    ArchiveBaseUrl = table.Column<string>(type: "text", nullable: false),
                    RequestedByTwitchUserId = table.Column<string>(type: "text", nullable: false),
                    RequestedByLogin = table.Column<string>(type: "text", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PausedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PauseCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    BlockAttempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    BytesReceived = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    MessagesRead = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    ErrorCode = table.Column<string>(type: "text", nullable: true),
                    ErrorHttpStatus = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatLogBackfillRuns", x => x.Id);
                    table.CheckConstraint("CK_ChatLogBackfillRuns_RequestedMonths", "\"RequestedMonths\" IN (1, 3, 6)");
                    table.CheckConstraint("CK_ChatLogBackfillRuns_Status", "\"Status\" IN ('queued', 'running', 'paused', 'completed', 'failed', 'cancelled')");
                    table.CheckConstraint("CK_ChatLogBackfillRuns_Window", "\"WindowFrom\" < \"WindowTo\"");
                    table.ForeignKey(
                        name: "FK_ChatLogBackfillRuns_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatLogBackfillCoverage",
                columns: table => new
                {
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    EmoteSetId = table.Column<string>(type: "text", nullable: false),
                    ArchiveHost = table.Column<string>(type: "text", nullable: false),
                    RunId = table.Column<long>(type: "bigint", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatLogBackfillCoverage", x => new { x.ChannelId, x.Day });
                    table.ForeignKey(
                        name: "FK_ChatLogBackfillCoverage_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatLogBackfillCoverage_ChatLogBackfillRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ChatLogBackfillRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ChatLogBackfillRunEmotes",
                columns: table => new
                {
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    EmoteId = table.Column<string>(type: "text", nullable: false),
                    SevenTvEmoteId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    AddedToSetDay = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedRow = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatLogBackfillRunEmotes", x => new { x.RunId, x.EmoteId });
                    table.ForeignKey(
                        name: "FK_ChatLogBackfillRunEmotes_ChatLogBackfillRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ChatLogBackfillRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // HAND-WRITTEN (1 of 2) - carry over verbatim when this migration is re-scaffolded.
            // The provider-state row is part of the schema, not data: the worker reads it before every
            // archive request and PauseAsync only ever updates it, so it must exist after Up.
            migrationBuilder.Sql(
                """
                INSERT INTO "ChatLogBackfillProviderState" ("Id", "CooldownUntilUtc", "LastRateLimitedAtUtc", "UpdatedAtUtc")
                VALUES (1, NULL, NULL, now());
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ChatLogBackfillCoverage_RunId",
                table: "ChatLogBackfillCoverage",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatLogBackfillRunEmotes_RunId_SevenTvEmoteId",
                table: "ChatLogBackfillRunEmotes",
                columns: new[] { "RunId", "SevenTvEmoteId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatLogBackfillRuns_ChannelId_Active",
                table: "ChatLogBackfillRuns",
                column: "ChannelId",
                unique: true,
                filter: "\"Status\" IN ('queued', 'running', 'paused')");

            migrationBuilder.CreateIndex(
                name: "IX_ChatLogBackfillRuns_RequestedByTwitchUserId",
                table: "ChatLogBackfillRuns",
                column: "RequestedByTwitchUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatLogBackfillRuns_Status_Id",
                table: "ChatLogBackfillRuns",
                columns: new[] { "Status", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // HAND-WRITTEN (2 of 2) - carry over verbatim when this migration is re-scaffolded.
            // Refuses while imported data exists (D40): dropping the column would silently delete
            // imported usage rows, and dropping the coverage table would forget which days were
            // processed. The error aborts before any statement below runs, and PostgreSQL's
            // transactional DDL leaves the schema exactly as it was. The provider-state row is state,
            // not data, and is dropped without a guard.
            // SHARE mode blocks concurrent writers (a flush inserting a Source = 1 row cannot exist, but
            // a backfill commit could) between the count below and the drops, without blocking readers.
            migrationBuilder.Sql("""LOCK TABLE "UsageStats", "ChatLogBackfillCoverage" IN SHARE MODE;""");

            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    v_imported bigint;
                    v_coverage bigint;
                BEGIN
                    SELECT count(*) INTO v_imported FROM "UsageStats" WHERE "Source" = 1;
                    SELECT count(*) INTO v_coverage FROM "ChatLogBackfillCoverage";
                    IF v_imported > 0 OR v_coverage > 0 THEN
                        RAISE EXCEPTION
                            'Refusing to revert AddChatLogBackfill: % imported usage row(s) and % coverage day(s) exist. Delete them first (see the Rollback Plan in docs/superpowers/specs/2026-10-09-chat-log-backfill-spec.md).',
                            v_imported, v_coverage;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropTable(
                name: "ChatLogBackfillCoverage");

            migrationBuilder.DropTable(
                name: "ChatLogBackfillProviderState");

            migrationBuilder.DropTable(
                name: "ChatLogBackfillRunEmotes");

            migrationBuilder.DropTable(
                name: "ChatLogBackfillRuns");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "UsageStats");
        }
    }
}
