using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <summary>
    /// <c>Emotes.IsPlaceholder</c> (chat-log backfill spec, child 1, D37): marks a row created for an
    /// emote this channel has never been observed to have in its active set, so the sync's REST leave
    /// detection stops reading "missing from the active set" as a leave for it.
    /// <para>
    /// The backfill marks the one combination only the set-session ballot's insert produces:
    /// archived, without an archive date, with an entry stamp. Every archive the sync or
    /// <c>EmoteService</c> performs stamps <c>ArchivedAt</c>, and rows older than the
    /// <c>LastEnteredSetAtUtc</c> column carry a null stamp, so neither can match. The statement is
    /// idempotent and is re-run by hand once after the production redeploy (docs/Operations.md,
    /// "Emote placeholder marker"): the previous image keeps creating ballot rows without the marker
    /// until it is replaced.
    /// </para>
    /// <para>
    /// Never reverted in production (D42): an image without the marker would treat every retained
    /// placeholder as a credible leave again. <c>Down</c> exists for local work and simply drops the
    /// column.
    /// </para>
    /// </summary>
    public partial class AddEmotePlaceholderMarker : Migration
    {
        /// <summary>
        /// The marker backfill, shared with the migration test and identical to the statement in
        /// docs/Operations.md. Only ever sets <c>true</c>, and only on rows not marked yet.
        /// </summary>
        public const string BackfillSql =
            """
            UPDATE "Emotes" SET "IsPlaceholder" = true
            WHERE "IsArchived" AND "ArchivedAt" IS NULL AND "LastEnteredSetAtUtc" IS NOT NULL
              AND NOT "IsPlaceholder";
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPlaceholder",
                table: "Emotes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPlaceholder",
                table: "Emotes");
        }
    }
}
