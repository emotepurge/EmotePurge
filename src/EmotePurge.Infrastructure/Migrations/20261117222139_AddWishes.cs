using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWishes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelWishSettings",
                columns: table => new
                {
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    DefaultDurationDays = table.Column<int>(type: "integer", nullable: false),
                    ProbationLeadDays = table.Column<int>(type: "integer", nullable: false),
                    ProtectionThresholdPerDay = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    BotModeEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    BotModeActivatedByTwitchUserId = table.Column<string>(type: "text", nullable: true),
                    BotModeActivatedByLogin = table.Column<string>(type: "text", nullable: true),
                    BotModeActivatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BotEditorCheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BotSuspendedReason = table.Column<string>(type: "text", nullable: true),
                    BotActivationRequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BotActivationRequestedByTwitchUserId = table.Column<string>(type: "text", nullable: true),
                    BotActivationRequestedByLogin = table.Column<string>(type: "text", nullable: true),
                    BotActivationError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelWishSettings", x => x.ChannelId);
                    table.CheckConstraint("CK_ChannelWishSettings_DefaultDurationDays", "\"DefaultDurationDays\" BETWEEN 1 AND 365");
                    table.CheckConstraint("CK_ChannelWishSettings_ProbationLeadDays", "\"ProbationLeadDays\" BETWEEN 1 AND 30");
                    table.CheckConstraint("CK_ChannelWishSettings_ProtectionThreshold", "\"ProtectionThresholdPerDay\" IS NULL OR \"ProtectionThresholdPerDay\" > 0");
                    table.ForeignKey(
                        name: "FK_ChannelWishSettings_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Wishes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    SevenTvEmoteId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EmoteName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Alias = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StatusReason = table.Column<string>(type: "text", nullable: true),
                    StatusChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequesterTwitchUserId = table.Column<string>(type: "text", nullable: true),
                    RequesterLogin = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: true),
                    DurationDays = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartsAtAuthoritative = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SlotId = table.Column<string>(type: "text", nullable: true),
                    ExtensionCount = table.Column<int>(type: "integer", nullable: false),
                    Permanent = table.Column<bool>(type: "boolean", nullable: false),
                    MergedIntoWishId = table.Column<long>(type: "bigint", nullable: true),
                    BotOwned = table.Column<bool>(type: "boolean", nullable: false),
                    BotOwnedAddedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BotWriteAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BotAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    BotNextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemovalQueuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemovalQueuedVersion = table.Column<int>(type: "integer", nullable: true),
                    DecisionVersion = table.Column<int>(type: "integer", nullable: false),
                    LinkOrigin = table.Column<string>(type: "text", nullable: true),
                    SlotAddedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SlotConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstActivatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wishes", x => x.Id);
                    table.CheckConstraint("CK_Wishes_DurationDays", "\"DurationDays\" BETWEEN 1 AND 365");
                    table.ForeignKey(
                        name: "FK_Wishes_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Wishes_EmoteSlots_SlotId",
                        column: x => x.SlotId,
                        principalTable: "EmoteSlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Wishes_Wishes_MergedIntoWishId",
                        column: x => x.MergedIntoWishId,
                        principalTable: "Wishes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Wishes_ChannelId_SevenTvEmoteId_Running",
                table: "Wishes",
                columns: new[] { "ChannelId", "SevenTvEmoteId" },
                unique: true,
                filter: "\"Status\" IN ('approved', 'unconfirmed', 'active', 'presented')");

            migrationBuilder.CreateIndex(
                name: "IX_Wishes_ChannelId_Status",
                table: "Wishes",
                columns: new[] { "ChannelId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Wishes_EndedAtUtc_WithRequester",
                table: "Wishes",
                column: "EndedAtUtc",
                filter: "\"RequesterLogin\" IS NOT NULL OR \"RequesterTwitchUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Wishes_MergedIntoWishId",
                table: "Wishes",
                column: "MergedIntoWishId");

            migrationBuilder.CreateIndex(
                name: "IX_Wishes_SlotId_Live",
                table: "Wishes",
                column: "SlotId",
                unique: true,
                filter: "\"SlotId\" IS NOT NULL AND \"Status\" IN ('active', 'presented')");

            migrationBuilder.CreateIndex(
                name: "IX_Wishes_Status_ExpiresAtUtc",
                table: "Wishes",
                columns: new[] { "Status", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelWishSettings");

            migrationBuilder.DropTable(
                name: "Wishes");
        }
    }
}
