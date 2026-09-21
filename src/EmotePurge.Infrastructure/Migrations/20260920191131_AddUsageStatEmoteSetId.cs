using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <summary>
    /// The one non-additive migration of #200 (spec 4.2). It gives every <c>UsageStats</c> row the
    /// 7TV emote set its counts belong to, swaps the unique index that the chat flush uses as its
    /// <c>ON CONFLICT</c> target, creates the observation log and seeds it, and adds the three
    /// nullable voting columns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs <b>once, by hand, inside a maintenance window</b>. Production never applies migrations
    /// at startup (<c>PendingMigrationGuard</c> aborts on pending ones, S3-34), and this one in
    /// particular must not meet a running old image: the flush writes
    /// <c>ON CONFLICT ("EmoteId", "Date")</c>, PostgreSQL resolves that against a unique index of
    /// exactly those columns, and step 9 below replaces it. An old worker would requeue its batch
    /// five times and then drop it (spec trap F1). Both earlier counter migrations were additive
    /// and said so; this one is the first at this table that is not.
    /// </para>
    /// <para>
    /// Everything runs in one transaction (EF Core's default; Npgsql has transactional DDL). From
    /// step 6 on it holds <c>ACCESS EXCLUSIVE</c> on <c>UsageStats</c> until it commits — which is
    /// why the api is down for the duration. All three abort checks sit in front of that: an abort
    /// costs no lock and leaves nothing behind.
    /// </para>
    /// </remarks>
    public partial class AddUsageStatEmoteSetId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Transaction-scoped, so it cannot leak into the session. Five seconds separate a
            //    forgotten psql session from ordinary lock-queue latency; deliberately no
            //    statement_timeout — the backfill's duration depends on a row count nobody has
            //    measured, and aborting mid-backfill would cost the window, not the data (E11).
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            // 2. The hand-written switch list, rendered into one temp table. Everything below reads
            //    only from here, never from the constant again. ON COMMIT DROP: the table lives
            //    exactly as long as this migration's transaction.
            migrationBuilder.Sql(BuildSwitchAssignmentTableSql());

            // 3. Check 1 — stale or duplicated list. Two shapes, one message, because both mean the
            //    same thing to the operator: the committed list no longer describes reality.
            //    Channels that do not exist in this database are skipped, deliberately: the
            //    Infrastructure test suite migrates a fresh, channel-less container on every start,
            //    and so does every new installation (spec 4.2, added 2026-09-20).
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    v_channel text;
                BEGIN
                    SELECT a.twitch_channel_id INTO v_channel
                    FROM set_switch_assignments a
                    WHERE EXISTS (
                        SELECT 1 FROM "Channels" c WHERE c."TwitchChannelId" = a.twitch_channel_id)
                    GROUP BY a.twitch_channel_id
                    HAVING count(*) > 1
                    LIMIT 1;

                    IF v_channel IS NOT NULL THEN
                        RAISE EXCEPTION
                            'set-switch assignment stale or duplicated for channel %', v_channel;
                    END IF;

                    SELECT a.twitch_channel_id INTO v_channel
                    FROM set_switch_assignments a
                    JOIN "Channels" c ON c."TwitchChannelId" = a.twitch_channel_id
                    WHERE c."ActiveEmoteSetId" IS DISTINCT FROM a.new_emote_set_id
                    LIMIT 1;

                    IF v_channel IS NOT NULL THEN
                        RAISE EXCEPTION
                            'set-switch assignment stale or duplicated for channel %', v_channel;
                    END IF;
                END $$;
                """);

            // 4. Check 2 — empty active set id on a channel that has usage rows. Without it those
            //    rows would silently receive an empty EmoteSetId in step 7a, because the backfill
            //    takes the active id unasked.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    v_channel text;
                BEGIN
                    SELECT c."ChannelName" INTO v_channel
                    FROM "Channels" c
                    WHERE COALESCE(c."ActiveEmoteSetId", '') = ''
                      AND EXISTS (
                          SELECT 1
                          FROM "UsageStats" u
                          JOIN "Emotes" e ON e."Id" = u."EmoteId"
                          WHERE e."ChannelId" = c."Id")
                    LIMIT 1;

                    IF v_channel IS NOT NULL THEN
                        RAISE EXCEPTION
                            'channel % has usage stats but an empty active emote set id', v_channel;
                    END IF;
                END $$;
                """);

            // 5. Check 3 — implausible boundary date. Catches the mistyped month and the mistyped
            //    year: the slips a hand-written date actually makes, and the ones that would push
            //    half a channel's history to the wrong side of the boundary. Only channels that
            //    exist and carry usage rows are considered — a channel without rows has nothing to
            //    misplace.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    v_channel  text;
                    v_boundary date;
                    v_min      date;
                    v_max      date;
                BEGIN
                    SELECT a.twitch_channel_id, a.boundary_utc, r.min_date, r.max_date
                      INTO v_channel, v_boundary, v_min, v_max
                    FROM set_switch_assignments a
                    JOIN "Channels" c ON c."TwitchChannelId" = a.twitch_channel_id
                    JOIN LATERAL (
                        SELECT min(u."Date") AS min_date, max(u."Date") AS max_date
                        FROM "UsageStats" u
                        JOIN "Emotes" e ON e."Id" = u."EmoteId"
                        WHERE e."ChannelId" = c."Id"
                    ) r ON r.min_date IS NOT NULL
                    WHERE a.boundary_utc < r.min_date OR a.boundary_utc > r.max_date
                    LIMIT 1;

                    IF v_channel IS NOT NULL THEN
                        RAISE EXCEPTION
                            'set-switch boundary % for channel % lies outside its usage range %..%',
                            v_boundary, v_channel, v_min, v_max;
                    END IF;
                END $$;
                """);

            // 6. Nullable and without a default — the column must stay empty until the backfill has
            //    touched every row, so that step 8 can catch a row the backfill missed.
            migrationBuilder.Sql(
                """ALTER TABLE "UsageStats" ADD COLUMN "EmoteSetId" text NULL;""");

            // 7a. The default: every row gets its channel's current active set id.
            migrationBuilder.Sql(
                """
                UPDATE "UsageStats" u
                SET "EmoteSetId" = c."ActiveEmoteSetId"
                FROM "Emotes" e
                JOIN "Channels" c ON c."Id" = e."ChannelId"
                WHERE e."Id" = u."EmoteId";
                """);

            // 7b. The correction, and the order is part of the contract: for a channel with a switch
            //     entry, every row strictly before the boundary day goes to the old set. The
            //     boundary day itself keeps the default and therefore goes to the new set — the
            //     known one-day imprecision named in spec 4.2. This rule models exactly one
            //     boundary per channel; a second entry for the same channel is an inadmissible
            //     input, which is what check 1's second shape rejects.
            migrationBuilder.Sql(
                """
                UPDATE "UsageStats" u
                SET "EmoteSetId" = a.old_emote_set_id
                FROM "Emotes" e
                JOIN "Channels" c ON c."Id" = e."ChannelId"
                JOIN set_switch_assignments a ON a.twitch_channel_id = c."TwitchChannelId"
                WHERE e."Id" = u."EmoteId"
                  AND u."Date" < a.boundary_utc;
                """);

            // 8. Fails if the backfill skipped a row. That is the intent: no default, no COALESCE,
            //    no guess.
            migrationBuilder.Sql(
                """ALTER TABLE "UsageStats" ALTER COLUMN "EmoteSetId" SET NOT NULL;""");

            // 9. The index swap. The new index is the flush's ON CONFLICT target; INCLUDE
            //    ("UseCount") stays so the range-sum queries remain index-only scans, and the
            //    EmoteId prefix still serves the set-agnostic readers.
            migrationBuilder.DropIndex(
                name: "IX_UsageStats_EmoteId_Date",
                table: "UsageStats");

            migrationBuilder.CreateIndex(
                name: "IX_UsageStats_EmoteId_EmoteSetId_Date",
                table: "UsageStats",
                columns: new[] { "EmoteId", "EmoteSetId", "Date" },
                unique: true)
                .Annotation("Npgsql:IndexInclude", new[] { "UseCount" });

            // 10. The observation log, its two indexes, and the seed.
            migrationBuilder.CreateTable(
                name: "ChannelEmoteSetObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    SevenTvEmoteSetId = table.Column<string>(type: "text", nullable: false),
                    ObservedFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ObservedToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelEmoteSetObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelEmoteSetObservations_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelEmoteSetObservations_ChannelId",
                table: "ChannelEmoteSetObservations",
                column: "ChannelId",
                unique: true,
                filter: "\"ObservedToUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelEmoteSetObservations_ChannelId_ObservedFromUtc",
                table: "ChannelEmoteSetObservations",
                columns: new[] { "ChannelId", "ObservedFromUtc" });

            // Seed, part one: every active channel with a non-empty active set and *no* switch entry
            // gets one open interval. Inactive channels get no row at all — even when they carry
            // usage rows that step 7 just backfilled. The backfill answers "which set do these
            // numbers belong to", the seed answers "which set did we observe as active", and those
            // are two different questions (spec 4.3).
            migrationBuilder.Sql(
                """
                INSERT INTO "ChannelEmoteSetObservations"
                    ("ChannelId", "SevenTvEmoteSetId", "ObservedFromUtc", "ObservedToUtc", "ClosedBy")
                SELECT c."Id",
                       c."ActiveEmoteSetId",
                       COALESCE(c."TrackingResumedAt", c."CreatedAt"),
                       NULL,
                       NULL
                FROM "Channels" c
                WHERE c."IsBotActive"
                  AND COALESCE(c."ActiveEmoteSetId", '') <> ''
                  AND NOT EXISTS (
                      SELECT 1 FROM set_switch_assignments a
                      WHERE a.twitch_channel_id = c."TwitchChannelId");
                """);

            // Seed, part two: a channel *with* a switch entry gets one row per interval — the first
            // closed at the boundary, the second still open. ClosedBy is 'migration' and never
            // 'set-switch': Down's first guard reads a 'set-switch' row as proof that a switch was
            // observed after this migration ran, and that only holds while the seed cannot produce
            // one. The boundary is rendered as midnight UTC of the boundary day, spelled out with
            // an explicit offset so the session's TimeZone setting cannot move it.
            migrationBuilder.Sql(
                """
                INSERT INTO "ChannelEmoteSetObservations"
                    ("ChannelId", "SevenTvEmoteSetId", "ObservedFromUtc", "ObservedToUtc", "ClosedBy")
                SELECT c."Id",
                       a.old_emote_set_id,
                       COALESCE(c."TrackingResumedAt", c."CreatedAt"),
                       (a.boundary_utc::timestamp AT TIME ZONE 'UTC'),
                       'migration'
                FROM "Channels" c
                JOIN set_switch_assignments a ON a.twitch_channel_id = c."TwitchChannelId"
                WHERE c."IsBotActive"
                  AND COALESCE(c."ActiveEmoteSetId", '') <> '';

                INSERT INTO "ChannelEmoteSetObservations"
                    ("ChannelId", "SevenTvEmoteSetId", "ObservedFromUtc", "ObservedToUtc", "ClosedBy")
                SELECT c."Id",
                       a.new_emote_set_id,
                       (a.boundary_utc::timestamp AT TIME ZONE 'UTC'),
                       NULL,
                       NULL
                FROM "Channels" c
                JOIN set_switch_assignments a ON a.twitch_channel_id = c."TwitchChannelId"
                WHERE c."IsBotActive"
                  AND COALESCE(c."ActiveEmoteSetId", '') <> '';
                """);

            // 11. The three additive, nullable voting columns.
            migrationBuilder.AddColumn<string>(
                name: "EmoteSetId",
                table: "VoteSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameAtCreation",
                table: "VoteSessionEmotes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageUrlAtCreation",
                table: "VoteSessionEmotes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Two independent guards, and neither replaces the other. Guard 1 is the observation log:
        /// any row closed with <c>'set-switch'</c> was written by the live sync path after this
        /// migration ran, because the seed above only ever writes <c>'migration'</c> — so rolling
        /// back would discard a set assignment that nothing can reconstruct. Guard 2 is the old
        /// unique index refusing to be recreated over two set ids at one <c>(EmoteId, Date)</c>.
        /// Guard 2 is a proxy, not a synonym: a switch between two days, or between disjoint sets,
        /// produces no collision at all, and <c>Down</c> would then discard <c>EmoteSetId</c>
        /// silently. Conversely guard 2 catches collisions the log never saw. Clearing guard 1 is a
        /// deliberate act by the operator (removing the <c>'set-switch'</c> rows), and that is the
        /// point: it turns the loss of the set assignment into a decision instead of a side effect.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard 1, ahead of every destructive step — the first DROP COLUMN, the DROP TABLE and
            // the index swap all sit behind it.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "ChannelEmoteSetObservations"
                        WHERE "ClosedBy" = 'set-switch') THEN
                        RAISE EXCEPTION
                            'observed set switch after migration; rolling back would discard EmoteSetId';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_UsageStats_EmoteId_EmoteSetId_Date",
                table: "UsageStats");

            migrationBuilder.DropColumn(
                name: "EmoteSetId",
                table: "UsageStats");

            // Guard 2: fails if a (EmoteId, Date) carried rows under two set ids. The rows are still
            // there — dropping the column above did not merge them — so the unique index is exactly
            // the collision test it was before this migration existed.
            migrationBuilder.CreateIndex(
                name: "IX_UsageStats_EmoteId_Date",
                table: "UsageStats",
                columns: new[] { "EmoteId", "Date" },
                unique: true)
                .Annotation("Npgsql:IndexInclude", new[] { "UseCount" });

            migrationBuilder.DropTable(
                name: "ChannelEmoteSetObservations");

            migrationBuilder.DropColumn(
                name: "EmoteSetId",
                table: "VoteSessions");

            migrationBuilder.DropColumn(
                name: "NameAtCreation",
                table: "VoteSessionEmotes");

            migrationBuilder.DropColumn(
                name: "ImageUrlAtCreation",
                table: "VoteSessionEmotes");
        }

        // Renders SetSwitchAssignments into the one temp table step 2 describes. An empty list is a
        // valid input and yields the table without rows — every check below then passes trivially.
        private static string BuildSwitchAssignmentTableSql()
        {
            var sql = new StringBuilder();
            sql.AppendLine("CREATE TEMP TABLE set_switch_assignments (");
            sql.AppendLine("    twitch_channel_id text NOT NULL,");
            sql.AppendLine("    old_emote_set_id  text NOT NULL,");
            sql.AppendLine("    new_emote_set_id  text NOT NULL,");
            sql.AppendLine("    boundary_utc      date NOT NULL");
            sql.AppendLine(") ON COMMIT DROP;");

            if (SetSwitchAssignments.Entries.Count == 0)
            {
                return sql.ToString();
            }

            sql.AppendLine(
                "INSERT INTO set_switch_assignments "
                    + "(twitch_channel_id, old_emote_set_id, new_emote_set_id, boundary_utc)");
            sql.AppendLine("VALUES");
            sql.AppendLine(
                string.Join(
                    ",\n",
                    SetSwitchAssignments.Entries.Select(
                        entry => "    ("
                            + Literal(entry.TwitchChannelId)
                            + ", "
                            + Literal(entry.OldEmoteSetId)
                            + ", "
                            + Literal(entry.NewEmoteSetId)
                            + ", DATE "
                            + Literal(entry.BoundaryUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                            + ")")));
            sql.AppendLine(";");

            return sql.ToString();
        }

        // The values come from a committed constant, not from input — but a stray apostrophe in a
        // future entry should be a syntax-free literal, not a broken statement.
        private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
    }
}
