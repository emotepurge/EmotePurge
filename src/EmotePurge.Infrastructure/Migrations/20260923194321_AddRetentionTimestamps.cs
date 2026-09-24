using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <summary>
    /// Two columns the data-retention job (docs/superpowers/plans/2026-09-23-datenaufbewahrung-243-244.md)
    /// reads its cutoffs from, each backfilled to "now" for existing rows rather than left NULL.
    /// <list type="bullet">
    /// <item><c>Users.LastSeenAtUtc</c> (Befund 1): without the backfill, an existing user who logs
    /// in rarely but has been active every week under a 14-day sliding cookie would look identical
    /// to a genuine 12-month dropout on the first enforcement run, because both would carry only an
    /// old <c>LastLogin</c> — the dry run could not tell them apart, and the sharp run would delete
    /// the active one. Stamping every existing row to the migration instant means retention starts
    /// measuring "seen" from here on; nobody existing is newly due before 30 days (token clear) or
    /// 365 days (account deletion) after this migration.</item>
    /// <item><c>Channels.DeactivatedAtUtc</c> (Befund 2): the column records "since when we measure
    /// inactivity", not the true historical leave date, which no existing row can prove — the audit
    /// log that could answer that only exists since 2026-07-31, and the gap is weeks, not months.
    /// Backfilling already-inactive channels to "now" means the 180-day channel purge starts its
    /// clock here too, instead of purging channels the very next run because their true leave date
    /// (unknown, therefore untouched before this column existed) reads as "forever".</item>
    /// </list>
    /// Both trade a few weeks to months of extra grace for existing rows against the alternative of
    /// a separate "no retention before deploy + N days" constant, which would say the same thing
    /// twice in two mechanisms. Operator-approved 2026-09-23 (plan, "Entscheidungen des Betreibers",
    /// point 1).
    /// </summary>
    public partial class AddRetentionTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeactivatedAtUtc",
                table: "Channels",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill: every existing user counts as "seen" as of this migration (Befund 1 above).
            migrationBuilder.Sql("""UPDATE "Users" SET "LastSeenAtUtc" = now();""");

            // Backfill: every already-inactive channel counts as "deactivated" as of this migration
            // (Befund 2 above). Active channels keep NULL — LeaveAsync stamps them going forward.
            migrationBuilder.Sql(
                """UPDATE "Channels" SET "DeactivatedAtUtc" = now() WHERE "IsBotActive" = false;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSeenAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DeactivatedAtUtc",
                table: "Channels");
        }
    }
}
