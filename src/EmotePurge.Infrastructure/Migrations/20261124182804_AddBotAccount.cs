using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBotAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BotAddAckAtUtc",
                table: "Wishes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BotAttemptVersion",
                table: "Wishes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BotConfirmDeadlineAtUtc",
                table: "Wishes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BotRemoveAtUtc",
                table: "Wishes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BotRemoveEvidence",
                table: "Wishes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BotRepairAtUtc",
                table: "Wishes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BotSetId",
                table: "Wishes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BotConsentSevenTvUserId",
                table: "ChannelWishSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BotEditorCheckedIdentityVersion",
                table: "ChannelWishSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BotInvitationAttemptAtUtc",
                table: "ChannelWishSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BotInvitationRetryAtUtc",
                table: "ChannelWishSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BotAccountCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    SealedToken = table.Column<string>(type: "text", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SetAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false, defaultValue: "none"),
                    BotSevenTvUserId = table.Column<string>(type: "text", nullable: true),
                    LastVerifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "text", nullable: true),
                    KillSwitch = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    PausedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CredentialVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    RunnerEpoch = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    IdentityVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    KillSwitchVersion = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    TokenKeyFingerprint = table.Column<string>(type: "text", nullable: true),
                    RenewalRequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RenewalRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    RenewalRunStartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RenewalRunRequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RenewalRunCredentialVersion = table.Column<int>(type: "integer", nullable: true),
                    RenewalRunKillSwitchVersion = table.Column<long>(type: "bigint", nullable: true),
                    LastRenewalSucceededAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRenewalAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RenewalFailureCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LastRenewalErrorCode = table.Column<string>(type: "text", nullable: true),
                    RenewerSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RenewerSecretHash = table.Column<string>(type: "text", nullable: true),
                    RenewerSecretSetAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TwitchSessionExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRenewalRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastRenewalRunOutcome = table.Column<string>(type: "text", nullable: true),
                    TwitchSessionState = table.Column<string>(type: "text", nullable: true),
                    LastVerifiedCredentialVersion = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotAccountCredentials", x => x.Id);
                    table.CheckConstraint("CK_BotAccountCredentials_Singleton", "\"Id\" = 1");
                    table.CheckConstraint("CK_BotAccountCredentials_Status", "\"Status\" IN ('none', 'unverified', 'valid', 'invalid', 'expired')");
                    table.CheckConstraint("CK_BotAccountCredentials_TwitchSessionState", "\"TwitchSessionState\" IS NULL OR \"TwitchSessionState\" IN ('valid', 'malformed', 'lost')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BotAccountCredentials");

            migrationBuilder.DropColumn(
                name: "BotAddAckAtUtc",
                table: "Wishes");

            migrationBuilder.DropColumn(
                name: "BotAttemptVersion",
                table: "Wishes");

            migrationBuilder.DropColumn(
                name: "BotConfirmDeadlineAtUtc",
                table: "Wishes");

            migrationBuilder.DropColumn(
                name: "BotRemoveAtUtc",
                table: "Wishes");

            migrationBuilder.DropColumn(
                name: "BotRemoveEvidence",
                table: "Wishes");

            migrationBuilder.DropColumn(
                name: "BotRepairAtUtc",
                table: "Wishes");

            migrationBuilder.DropColumn(
                name: "BotSetId",
                table: "Wishes");

            migrationBuilder.DropColumn(
                name: "BotConsentSevenTvUserId",
                table: "ChannelWishSettings");

            migrationBuilder.DropColumn(
                name: "BotEditorCheckedIdentityVersion",
                table: "ChannelWishSettings");

            migrationBuilder.DropColumn(
                name: "BotInvitationAttemptAtUtc",
                table: "ChannelWishSettings");

            migrationBuilder.DropColumn(
                name: "BotInvitationRetryAtUtc",
                table: "ChannelWishSettings");
        }
    }
}
