using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWishCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IrcMessageId",
                table: "Wishes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchKind",
                table: "Wishes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestText",
                table: "Wishes",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedName",
                table: "Wishes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RewardId",
                table: "Wishes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingExpiryDays",
                table: "ChannelWishSettings",
                type: "integer",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.CreateTable(
                name: "ChannelWishRewards",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    RewardId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DefaultDurationDays = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRedemptionSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelWishRewards", x => x.Id);
                    table.CheckConstraint("CK_ChannelWishRewards_DefaultDurationDays", "\"DefaultDurationDays\" BETWEEN 1 AND 365");
                    table.ForeignKey(
                        name: "FK_ChannelWishRewards_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WishCaptureGaps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    FromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishCaptureGaps", x => x.Id);
                    table.CheckConstraint("CK_WishCaptureGaps_Reason", "\"Reason\" IN ('disconnect', 'worker_restart', 'not_joined')");
                    table.ForeignKey(
                        name: "FK_WishCaptureGaps_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WishCaptureReceipts",
                columns: table => new
                {
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    IrcMessageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishCaptureReceipts", x => new { x.ChannelId, x.IrcMessageId });
                    table.ForeignKey(
                        name: "FK_WishCaptureReceipts_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WishCaptureState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    LastAliveAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishCaptureState", x => x.Id);
                    table.CheckConstraint("CK_WishCaptureState_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "WishRewardLearnings",
                columns: table => new
                {
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uuid", nullable: true),
                    CandidateRewardId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CandidateText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CandidateLogin = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: true),
                    CandidateSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishRewardLearnings", x => x.ChannelId);
                    table.CheckConstraint("CK_WishRewardLearnings_Candidate", "(\"CandidateId\" IS NULL AND \"CandidateRewardId\" IS NULL AND \"CandidateText\" IS NULL AND \"CandidateLogin\" IS NULL AND \"CandidateSeenAtUtc\" IS NULL) OR (\"CandidateId\" IS NOT NULL AND \"CandidateRewardId\" IS NOT NULL AND \"CandidateText\" IS NOT NULL AND \"CandidateLogin\" IS NOT NULL AND \"CandidateSeenAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WishRewardLearnings_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WishSlotExclusions",
                columns: table => new
                {
                    WishId = table.Column<long>(type: "bigint", nullable: false),
                    SlotId = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishSlotExclusions", x => new { x.WishId, x.SlotId });
                    table.ForeignKey(
                        name: "FK_WishSlotExclusions_Wishes_WishId",
                        column: x => x.WishId,
                        principalTable: "Wishes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Wishes_ChannelId_IrcMessageId",
                table: "Wishes",
                columns: new[] { "ChannelId", "IrcMessageId" },
                unique: true,
                filter: "\"IrcMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Wishes_RewardId",
                table: "Wishes",
                column: "RewardId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Wishes_EmotePlaceholder",
                table: "Wishes",
                sql: "\"Status\" IN ('pending', 'rejected', 'expired') OR (\"SevenTvEmoteId\" <> '' AND \"EmoteName\" <> '' AND \"Alias\" <> '')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Wishes_MatchKind",
                table: "Wishes",
                sql: "\"MatchKind\" IS NULL OR \"MatchKind\" IN ('link', 'name', 'none')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChannelWishSettings_PendingExpiryDays",
                table: "ChannelWishSettings",
                sql: "\"PendingExpiryDays\" BETWEEN 1 AND 90");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelWishRewards_ChannelId_RewardId",
                table: "ChannelWishRewards",
                columns: new[] { "ChannelId", "RewardId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WishCaptureGaps_ChannelId_FromUtc",
                table: "WishCaptureGaps",
                columns: new[] { "ChannelId", "FromUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WishCaptureReceipts_SeenAtUtc",
                table: "WishCaptureReceipts",
                column: "SeenAtUtc");

            migrationBuilder.AddForeignKey(
                name: "FK_Wishes_ChannelWishRewards_RewardId",
                table: "Wishes",
                column: "RewardId",
                principalTable: "ChannelWishRewards",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Wishes_ChannelWishRewards_RewardId",
                table: "Wishes");

            migrationBuilder.DropTable(
                name: "ChannelWishRewards");

            migrationBuilder.DropTable(
                name: "WishCaptureGaps");

            migrationBuilder.DropTable(
                name: "WishCaptureReceipts");

            migrationBuilder.DropTable(
                name: "WishCaptureState");

            migrationBuilder.DropTable(
                name: "WishRewardLearnings");

            migrationBuilder.DropTable(
                name: "WishSlotExclusions");

            migrationBuilder.DropIndex(
                name: "IX_Wishes_ChannelId_IrcMessageId",
                table: "Wishes");

            migrationBuilder.DropIndex(
                name: "IX_Wishes_RewardId",
                table: "Wishes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Wishes_EmotePlaceholder",
                table: "Wishes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Wishes_MatchKind",
                table: "Wishes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ChannelWishSettings_PendingExpiryDays",
                table: "ChannelWishSettings");

            migrationBuilder.DropColumn(
                name: "IrcMessageId",
                table: "Wishes");

            migrationBuilder.DropColumn(
                name: "MatchKind",
                table: "Wishes");

            migrationBuilder.DropColumn(
                name: "RequestText",
                table: "Wishes");

            migrationBuilder.DropColumn(
                name: "RequestedName",
                table: "Wishes");

            migrationBuilder.DropColumn(
                name: "RewardId",
                table: "Wishes");

            migrationBuilder.DropColumn(
                name: "PendingExpiryDays",
                table: "ChannelWishSettings");
        }
    }
}
