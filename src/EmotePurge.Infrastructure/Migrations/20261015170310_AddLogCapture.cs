using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLogCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LogCaptureStatus",
                columns: table => new
                {
                    Source = table.Column<short>(type: "smallint", nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ProcessStartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastFlushUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CaptureEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastIncompleteAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CleanShutdownAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogCaptureStatus", x => new { x.Source, x.InstanceId });
                });

            migrationBuilder.CreateTable(
                name: "LogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CaptureId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<short>(type: "smallint", nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ProcessStartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Level = table.Column<short>(type: "smallint", nullable: false),
                    Category = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EventId = table.Column<int>(type: "integer", nullable: true),
                    EventName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Message = table.Column<string>(type: "text", nullable: false),
                    ExceptionType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Exception = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "jsonb", nullable: true),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogFilters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    NameKey = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Filter = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogFilters", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_CaptureId",
                table: "LogEntries",
                column: "CaptureId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_Source_ProcessStartedUtc",
                table: "LogEntries",
                columns: new[] { "Source", "ProcessStartedUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_TimestampUtc_Errors",
                table: "LogEntries",
                column: "TimestampUtc",
                filter: "\"Level\" >= 4");

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_TimestampUtc_Id",
                table: "LogEntries",
                columns: new[] { "TimestampUtc", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_LogFilters_NameKey",
                table: "LogFilters",
                column: "NameKey",
                unique: true);

            // One-time seed (not HasData): a filter the operator deletes stays deleted. NameKey values are
            // literals equal to LogFilterNames.NormalizeKey(Name); migrations never call application code.
            var seededAtUtc = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);
            migrationBuilder.InsertData(
                table: "LogFilters",
                columns: new[] { "Name", "NameKey", "Filter", "SortOrder", "CreatedAtUtc", "UpdatedAtUtc" },
                values: new object[,]
                {
                    { "Start nach Deploy", "start nach deploy", "instance:latest uptime<15m", 1, seededAtUtc, seededAtUtc },
                    { "Twitch-Reconnects", "twitch-reconnects", "source:worker cat:TwitchConnectionWatchdog,TwitchChatManager,TwitchLib.* -\"angestoßen (Quelle\" -\" gejoint.\"", 2, seededAtUtc, seededAtUtc },
                    { "7TV-Sync/EventAPI", "7tv-sync/eventapi", "source:worker cat:SevenTv*", 3, seededAtUtc, seededAtUtc },
                    { "Alle Fehler", "alle fehler", "level>=error", 4, seededAtUtc, seededAtUtc },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LogCaptureStatus");

            migrationBuilder.DropTable(
                name: "LogEntries");

            migrationBuilder.DropTable(
                name: "LogFilters");
        }
    }
}
