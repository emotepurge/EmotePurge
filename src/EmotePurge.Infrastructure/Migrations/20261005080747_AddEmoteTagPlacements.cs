using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmoteTagPlacements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastEnteredSetAtUtc",
                table: "Emotes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmoteSetLeaveObservations",
                columns: table => new
                {
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    SevenTvEmoteId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SevenTvEmoteSetId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmoteSetLeaveObservations", x => new { x.ChannelId, x.SevenTvEmoteId, x.SevenTvEmoteSetId });
                    table.ForeignKey(
                        name: "FK_EmoteSetLeaveObservations_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmoteTagActivations",
                columns: table => new
                {
                    TagId = table.Column<long>(type: "bigint", nullable: false),
                    SevenTvEmoteSetId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActivatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmoteTagActivations", x => new { x.TagId, x.SevenTvEmoteSetId });
                    table.ForeignKey(
                        name: "FK_EmoteTagActivations_EmoteTags_TagId",
                        column: x => x.TagId,
                        principalTable: "EmoteTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmoteTagOperations",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SevenTvEmoteSetId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RegisteredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmoteTagOperations", x => x.OperationId);
                    table.ForeignKey(
                        name: "FK_EmoteTagOperations_EmoteTags_TagId",
                        column: x => x.TagId,
                        principalTable: "EmoteTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmoteTagPlacements",
                columns: table => new
                {
                    TagId = table.Column<long>(type: "bigint", nullable: false),
                    SevenTvEmoteId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SevenTvEmoteSetId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PlacedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmoteTagPlacements", x => new { x.TagId, x.SevenTvEmoteId, x.SevenTvEmoteSetId });
                    table.ForeignKey(
                        name: "FK_EmoteTagPlacements_EmoteTagEntries_TagId_SevenTvEmoteId",
                        columns: x => new { x.TagId, x.SevenTvEmoteId },
                        principalTable: "EmoteTagEntries",
                        principalColumns: new[] { "TagId", "SevenTvEmoteId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EmoteTagPlacements_EmoteTags_TagId",
                        column: x => x.TagId,
                        principalTable: "EmoteTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmoteTagOperations_TagId_SevenTvEmoteSetId",
                table: "EmoteTagOperations",
                columns: new[] { "TagId", "SevenTvEmoteSetId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmoteTagPlacements_SevenTvEmoteSetId_SevenTvEmoteId",
                table: "EmoteTagPlacements",
                columns: new[] { "SevenTvEmoteSetId", "SevenTvEmoteId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmoteSetLeaveObservations");

            migrationBuilder.DropTable(
                name: "EmoteTagActivations");

            migrationBuilder.DropTable(
                name: "EmoteTagOperations");

            migrationBuilder.DropTable(
                name: "EmoteTagPlacements");

            migrationBuilder.DropColumn(
                name: "LastEnteredSetAtUtc",
                table: "Emotes");
        }
    }
}
