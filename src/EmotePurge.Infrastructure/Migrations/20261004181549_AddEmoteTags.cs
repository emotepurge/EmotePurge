using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmoteTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmoteTags",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmoteTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmoteTags_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmoteTagEntries",
                columns: table => new
                {
                    TagId = table.Column<long>(type: "bigint", nullable: false),
                    SevenTvEmoteId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Alias = table.Column<string>(type: "text", nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmoteTagEntries", x => new { x.TagId, x.SevenTvEmoteId });
                    table.ForeignKey(
                        name: "FK_EmoteTagEntries_EmoteTags_TagId",
                        column: x => x.TagId,
                        principalTable: "EmoteTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmoteTags_ChannelId_CreatedAtUtc",
                table: "EmoteTags",
                columns: new[] { "ChannelId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EmoteTags_ChannelId_NormalizedName",
                table: "EmoteTags",
                columns: new[] { "ChannelId", "NormalizedName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmoteTagEntries");

            migrationBuilder.DropTable(
                name: "EmoteTags");
        }
    }
}
