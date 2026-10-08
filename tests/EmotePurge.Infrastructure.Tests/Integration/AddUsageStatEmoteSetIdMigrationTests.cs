using System.Text;
using EmotePurge.Infrastructure.Migrations;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The one non-additive migration of #200 (spec 4.2), exercised end to end against the ephemeral
// container: three abort checks, the run against an empty database, the two-stage backfill, the
// index swap, the seed, and Down's two independent guards.
//
// Every case builds its input through the *database*, not through a fixture of the switch list: it
// creates a channel carrying the constant's TwitchChannelId and gives it the rows and the
// ActiveEmoteSetId that trigger the branch under test (spec 4.2, "Die Tests bringen ihre Fälle
// selbst mit"). No case changes SetSwitchAssignments — the dates below are all derived from the
// entry's own BoundaryUtc, so they stay correct when the operator replaces the placeholder with the
// real switch day.
//
// Each case gets its own database on the shared container, migrated to the *previous* migration
// first, because the seed has to be written against the old schema — the current AppDbContext model
// already knows columns that do not exist yet at that point. That is why the seeding below is raw
// SQL rather than entity inserts.
[Collection("Postgres")]
public class AddUsageStatEmoteSetIdMigrationTests(PostgresFixture fixture)
{
    private const string PreviousMigration = "20260907080507_AddUsageStatSharedChatUseCount";
    private const string ThisMigration = "20260920191131_AddUsageStatEmoteSetId";

    private const string OtherChannelSetId = "01OTHERCHANNELACTIVESET000";
    private const string UnrelatedSetId = "01SOMEOTHERSETENTIRELY0000";

    private static readonly SetSwitchAssignment Entry = SetSwitchAssignments.Entries.Single();

    [Fact]
    public async Task Up_RunsThroughOnAnEmptyDatabase_AlthoughTheListNamesAChannelThatIsNotThere()
    {
        // The state PostgresFixture creates on every suite start, and the state of every new
        // installation: not a single channel exists, so the switch entry matches nothing. All three
        // checks must pass trivially rather than read the missing channel as an error.
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);

        await MigrateAsync(db, ThisMigration);

        Assert.True(await ColumnExistsAsync(db, "UsageStats", "EmoteSetId"));
        Assert.Empty(await QueryAsync(db, """SELECT "Id"::text AS "Value" FROM "ChannelEmoteSetObservations";"""));
    }

    [Fact]
    public async Task Up_Aborts_WhenTheEntrysNewSetIdIsNoLongerTheChannelsActiveSet()
    {
        // Check 1: the committed list has been overtaken — the channel moved on to some other set
        // since the entry was written down.
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "switch-channel", "switchchannel", Entry.TwitchChannelId, UnrelatedSetId);

        var message = await MigrateAndCaptureFailureAsync(db, ThisMigration);

        Assert.Contains("set-switch assignment stale or duplicated for channel", message);
        Assert.Contains(Entry.TwitchChannelId, message);
        await AssertNothingWasAppliedAsync(db);
    }

    [Fact]
    public async Task Up_Aborts_WhenAChannelWithUsageRowsHasAnEmptyActiveEmoteSetId()
    {
        // Check 2: without it those rows would receive an empty EmoteSetId from step 7a, because
        // the backfill takes the active id unasked. Deliberately a channel *without* a switch
        // entry, so check 1 cannot fire first.
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "blank-channel", "blankchannel", "11112222", activeEmoteSetId: "");
        await InsertEmoteAsync(db, "blank-emote", "blank-channel", "Stare");
        await InsertUsageStatAsync(db, "blank-emote", Entry.BoundaryUtc, useCount: 3);

        var message = await MigrateAndCaptureFailureAsync(db, ThisMigration);

        Assert.Contains("has usage stats but an empty active emote set id", message);
        Assert.Contains("blankchannel", message);
        await AssertNothingWasAppliedAsync(db);
    }

    [Fact]
    public async Task Up_Aborts_WhenTheBoundaryLiesOutsideTheChannelsUsageRange()
    {
        // Check 3: the mistyped month or year. Here every usage row of the switch channel lies
        // after the boundary, so the boundary is below min("Date") — the shape that would otherwise
        // push the channel's whole history to the wrong side.
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "switch-channel", "switchchannel", Entry.TwitchChannelId, Entry.NewEmoteSetId);
        await InsertEmoteAsync(db, "switch-emote", "switch-channel", "Stare");
        await InsertUsageStatAsync(db, "switch-emote", Entry.BoundaryUtc.AddDays(10), useCount: 1);
        await InsertUsageStatAsync(db, "switch-emote", Entry.BoundaryUtc.AddDays(20), useCount: 2);

        var message = await MigrateAndCaptureFailureAsync(db, ThisMigration);

        Assert.Contains("lies outside its usage range", message);
        Assert.Contains(Entry.TwitchChannelId, message);
        Assert.Contains(Entry.BoundaryUtc.AddDays(10).ToString("yyyy-MM-dd"), message);
        Assert.Contains(Entry.BoundaryUtc.AddDays(20).ToString("yyyy-MM-dd"), message);
        await AssertNothingWasAppliedAsync(db);
    }

    [Fact]
    public async Task Up_BackfillsEveryRowExactlyOnce_WithTheBoundaryDayGoingToTheNewSet()
    {
        // AK 7, both halves in one case: the channel *with* the entry splits at the boundary — and
        // the boundary day itself goes to the new id, the known one-day imprecision — while a
        // channel *without* an entry gets its active id on every row, before and after that same
        // date.
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);

        await InsertChannelAsync(db, "switch-channel", "switchchannel", Entry.TwitchChannelId, Entry.NewEmoteSetId);
        await InsertEmoteAsync(db, "switch-emote", "switch-channel", "Stare");
        await InsertUsageStatAsync(db, "switch-emote", Entry.BoundaryUtc.AddDays(-2), useCount: 1);
        await InsertUsageStatAsync(db, "switch-emote", Entry.BoundaryUtc, useCount: 2);
        await InsertUsageStatAsync(db, "switch-emote", Entry.BoundaryUtc.AddDays(2), useCount: 3);

        await InsertChannelAsync(db, "plain-channel", "plainchannel", "99998888", OtherChannelSetId);
        await InsertEmoteAsync(db, "plain-emote", "plain-channel", "Clap");
        await InsertUsageStatAsync(db, "plain-emote", Entry.BoundaryUtc.AddDays(-2), useCount: 4);
        await InsertUsageStatAsync(db, "plain-emote", Entry.BoundaryUtc.AddDays(2), useCount: 5);

        await MigrateAsync(db, ThisMigration);

        var assignments = await QueryAsync(
            db,
            """
            SELECT e."Name" || '@' || u."Date"::text || '=' || u."EmoteSetId" AS "Value"
            FROM "UsageStats" u
            JOIN "Emotes" e ON e."Id" = u."EmoteId"
            ORDER BY 1;
            """);

        Assert.Equal(
            [
                $"Clap@{Entry.BoundaryUtc.AddDays(-2):yyyy-MM-dd}={OtherChannelSetId}",
                $"Clap@{Entry.BoundaryUtc.AddDays(2):yyyy-MM-dd}={OtherChannelSetId}",
                $"Stare@{Entry.BoundaryUtc.AddDays(-2):yyyy-MM-dd}={Entry.OldEmoteSetId}",
                $"Stare@{Entry.BoundaryUtc:yyyy-MM-dd}={Entry.NewEmoteSetId}",
                $"Stare@{Entry.BoundaryUtc.AddDays(2):yyyy-MM-dd}={Entry.NewEmoteSetId}",
            ],
            assignments);
    }

    [Fact]
    public async Task Up_ReplacesTheTwoColumnUniqueIndexWithTheThreeColumnOne()
    {
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);

        await MigrateAsync(db, ThisMigration);

        var definitions = await QueryAsync(
            db,
            """
            SELECT indexname || ' :: ' || indexdef AS "Value"
            FROM pg_indexes
            WHERE tablename = 'UsageStats'
            ORDER BY 1;
            """);

        Assert.DoesNotContain(definitions, d => d!.StartsWith("IX_UsageStats_EmoteId_Date ", StringComparison.Ordinal));

        var swapped = Assert.Single(
            definitions,
            d => d!.StartsWith("IX_UsageStats_EmoteId_EmoteSetId_Date ", StringComparison.Ordinal));
        Assert.Contains("CREATE UNIQUE INDEX", swapped);
        Assert.Contains("\"EmoteId\", \"EmoteSetId\", \"Date\"", swapped);
        Assert.Contains("INCLUDE (\"UseCount\")", swapped);
    }

    [Fact]
    public async Task Up_SeedsOneOpenIntervalPerActiveChannel_AndTwoForTheChannelWithTheEntry()
    {
        // AK 9: the channel with the entry gets both of its intervals, the first closed with
        // 'migration' at the boundary; a plain active channel gets one open interval starting at
        // COALESCE(TrackingResumedAt, CreatedAt); an inactive channel gets none — even though it
        // carries usage rows that the backfill did assign. Seed and backfill answer two different
        // questions (spec 4.3).
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);

        var createdAt = Entry.BoundaryUtc.AddDays(-30).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var resumedAt = Entry.BoundaryUtc.AddDays(-10).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        await InsertChannelAsync(
            db, "switch-channel", "switchchannel", Entry.TwitchChannelId, Entry.NewEmoteSetId, createdAt: createdAt);
        await InsertEmoteAsync(db, "switch-emote", "switch-channel", "Stare");
        await InsertUsageStatAsync(db, "switch-emote", Entry.BoundaryUtc.AddDays(-2), useCount: 1);
        await InsertUsageStatAsync(db, "switch-emote", Entry.BoundaryUtc.AddDays(2), useCount: 2);

        await InsertChannelAsync(
            db, "plain-channel", "plainchannel", "99998888", OtherChannelSetId,
            createdAt: createdAt, trackingResumedAt: resumedAt);

        await InsertChannelAsync(
            db, "idle-channel", "idlechannel", "77776666", UnrelatedSetId,
            isBotActive: false, createdAt: createdAt);
        await InsertEmoteAsync(db, "idle-emote", "idle-channel", "Clap");
        await InsertUsageStatAsync(db, "idle-emote", Entry.BoundaryUtc.AddDays(1), useCount: 3);

        await MigrateAsync(db, ThisMigration);

        var observations = await QueryAsync(
            db,
            """
            SELECT "ChannelId" || ' | ' || "SevenTvEmoteSetId"
                   || ' | ' || to_char("ObservedFromUtc" AT TIME ZONE 'UTC', 'YYYY-MM-DD')
                   || ' | ' || COALESCE(to_char("ObservedToUtc" AT TIME ZONE 'UTC', 'YYYY-MM-DD'), 'open')
                   || ' | ' || COALESCE("ClosedBy", '-') AS "Value"
            FROM "ChannelEmoteSetObservations"
            ORDER BY 1;
            """);

        Assert.Equal(
            [
                $"plain-channel | {OtherChannelSetId} | {resumedAt:yyyy-MM-dd} | open | -",
                $"switch-channel | {Entry.OldEmoteSetId} | {createdAt:yyyy-MM-dd} | {Entry.BoundaryUtc:yyyy-MM-dd} | migration",
                $"switch-channel | {Entry.NewEmoteSetId} | {Entry.BoundaryUtc:yyyy-MM-dd} | open | -",
            ],
            observations);
    }

    [Fact]
    public async Task Up_NeverSeedsAnObservationClosedBySetSwitch()
    {
        // The invariant Down's first guard hangs on: 'set-switch' must come from the live sync path
        // alone, so that a row carrying it means "after the deploy" by construction. Until
        // 2026-09-20 this was an obligation in prose with no test behind it.
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);

        await InsertChannelAsync(db, "switch-channel", "switchchannel", Entry.TwitchChannelId, Entry.NewEmoteSetId);
        await InsertChannelAsync(db, "plain-channel", "plainchannel", "99998888", OtherChannelSetId);

        await MigrateAsync(db, ThisMigration);

        var closers = await QueryAsync(
            db,
            """SELECT COALESCE("ClosedBy", '-') AS "Value" FROM "ChannelEmoteSetObservations";""");

        Assert.NotEmpty(closers);
        Assert.DoesNotContain("set-switch", closers);
    }

    [Fact]
    public async Task Down_RestoresTheTwoColumnIndex_WhenNoSetSwitchWasObserved()
    {
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "plain-channel", "plainchannel", "99998888", OtherChannelSetId);
        await InsertEmoteAsync(db, "plain-emote", "plain-channel", "Clap");
        await InsertUsageStatAsync(db, "plain-emote", Entry.BoundaryUtc, useCount: 1);

        await MigrateAsync(db, ThisMigration);
        await MigrateAsync(db, PreviousMigration);

        Assert.False(await ColumnExistsAsync(db, "UsageStats", "EmoteSetId"));
        Assert.False(await TableExistsAsync(db, "ChannelEmoteSetObservations"));

        var definitions = await QueryAsync(
            db,
            """SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'UsageStats' ORDER BY 1;""");
        Assert.Contains("IX_UsageStats_EmoteId_Date", definitions);
        Assert.DoesNotContain("IX_UsageStats_EmoteId_EmoteSetId_Date", definitions);
    }

    [Fact]
    public async Task Down_Fails_WhenOneEmoteDayCarriesTwoSetIds()
    {
        // Guard 2, the old unique index refusing to come back. The two rows are what a set switch
        // inside one day leaves behind.
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "plain-channel", "plainchannel", "99998888", OtherChannelSetId);
        await InsertEmoteAsync(db, "plain-emote", "plain-channel", "Clap");
        await InsertUsageStatAsync(db, "plain-emote", Entry.BoundaryUtc, useCount: 1);

        await MigrateAsync(db, ThisMigration);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
            VALUES ({0}, {1}, {2}, 7, 0, 0);
            """,
            "plain-emote",
            UnrelatedSetId,
            Entry.BoundaryUtc);

        var message = await MigrateAndCaptureFailureAsync(db, PreviousMigration);

        Assert.Contains("IX_UsageStats_EmoteId_Date", message);
        await AssertNothingWasRemovedAsync(db);
    }

    [Fact]
    public async Task Down_Fails_WhenTheLogRecordsASetSwitch_EvenWithoutAnIndexCollision()
    {
        // The case guard 2 alone would let through: the log knows about a switch, but no
        // (EmoteId, Date) carries two set ids — disjoint sets, or a switch that fell between two
        // days. Without guard 1, Down would recreate the old index happily and drop the column.
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "plain-channel", "plainchannel", "99998888", OtherChannelSetId);
        await InsertEmoteAsync(db, "plain-emote", "plain-channel", "Clap");
        await InsertUsageStatAsync(db, "plain-emote", Entry.BoundaryUtc, useCount: 1);

        await MigrateAsync(db, ThisMigration);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "ChannelEmoteSetObservations"
                ("ChannelId", "SevenTvEmoteSetId", "ObservedFromUtc", "ObservedToUtc", "ClosedBy")
            VALUES ({0}, {1}, {2}, {3}, 'set-switch');
            """,
            "plain-channel",
            UnrelatedSetId,
            Entry.BoundaryUtc.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Entry.BoundaryUtc.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        var message = await MigrateAndCaptureFailureAsync(db, PreviousMigration);

        Assert.Contains("observed set switch after migration", message);
        await AssertNothingWasRemovedAsync(db);
    }

    // A scratch database per case on the shared container, migrated up to the migration *before*
    // this one — the state the seeding below has to be written against.
    private async Task<string> CreateDatabaseAtPreviousMigrationAsync()
    {
        var databaseName = $"migration_check_{Guid.NewGuid():N}";
        await using (var admin = fixture.CreateDbContext())
        {
            // CREATE DATABASE cannot run inside a transaction and cannot be parameterized; the name
            // is a locally generated Guid, so EF1002 does not apply (same pattern as
            // PendingMigrationGuardTests).
#pragma warning disable EF1002
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {databaseName}");
#pragma warning restore EF1002
        }

        await using var db = fixture.CreateDbContext(databaseName);
        await MigrateAsync(db, PreviousMigration);

        return databaseName;
    }

    private static async Task MigrateAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private static async Task<string> MigrateAndCaptureFailureAsync(AppDbContext db, string targetMigration)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(db, targetMigration));

        var messages = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.AppendLine(current.Message);
        }

        return messages.ToString();
    }

    // Up aborted: the column, the table and the swapped index must all be absent, and the old index
    // must still be there. The abort checks sit in front of every DDL statement precisely so that
    // this holds without relying on the transaction alone.
    private static async Task AssertNothingWasAppliedAsync(AppDbContext db)
    {
        Assert.False(await ColumnExistsAsync(db, "UsageStats", "EmoteSetId"));
        Assert.False(await ColumnExistsAsync(db, "VoteSessions", "EmoteSetId"));
        Assert.False(await TableExistsAsync(db, "ChannelEmoteSetObservations"));

        var definitions = await QueryAsync(
            db,
            """SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'UsageStats' ORDER BY 1;""");
        Assert.Contains("IX_UsageStats_EmoteId_Date", definitions);
        Assert.DoesNotContain("IX_UsageStats_EmoteId_EmoteSetId_Date", definitions);
    }

    // Down aborted: neither column, nor table, nor index has been removed.
    private static async Task AssertNothingWasRemovedAsync(AppDbContext db)
    {
        Assert.True(await ColumnExistsAsync(db, "UsageStats", "EmoteSetId"));
        Assert.True(await ColumnExistsAsync(db, "VoteSessions", "EmoteSetId"));
        Assert.True(await TableExistsAsync(db, "ChannelEmoteSetObservations"));

        var definitions = await QueryAsync(
            db,
            """SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'UsageStats' ORDER BY 1;""");
        Assert.Contains("IX_UsageStats_EmoteId_EmoteSetId_Date", definitions);
        Assert.DoesNotContain("IX_UsageStats_EmoteId_Date", definitions);
    }

    private static async Task<bool> ColumnExistsAsync(AppDbContext db, string table, string column)
    {
        var found = await QueryAsync(
            db,
            """
            SELECT column_name AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = {0} AND column_name = {1};
            """,
            table,
            column);

        return found.Count > 0;
    }

    private static async Task<bool> TableExistsAsync(AppDbContext db, string table)
    {
        var found = await QueryAsync(
            db,
            """
            SELECT table_name AS "Value"
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = {0};
            """,
            table);

        return found.Count > 0;
    }

    private static async Task<List<string?>> QueryAsync(AppDbContext db, string sql, params object[] parameters) =>
        await db.Database.SqlQueryRaw<string?>(sql, parameters).ToListAsync();

    private static async Task InsertChannelAsync(
        AppDbContext db,
        string id,
        string channelName,
        string twitchChannelId,
        string activeEmoteSetId,
        bool isBotActive = true,
        DateTime? createdAt = null,
        DateTime? trackingResumedAt = null)
    {
        // Two statements rather than one with a nullable parameter: EF Core's raw-SQL parameter
        // builder derives the store type from the CLR type of the value, and a null has none.
        var created = createdAt ?? Entry.BoundaryUtc.AddDays(-30).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        if (trackingResumedAt is null)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Channels"
                    ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, NULL);
                """,
                id,
                twitchChannelId,
                channelName,
                activeEmoteSetId,
                isBotActive,
                created);

            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Channels"
                ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
            VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6});
            """,
            id,
            twitchChannelId,
            channelName,
            activeEmoteSetId,
            isBotActive,
            created,
            trackingResumedAt.Value);
    }

    private static async Task InsertEmoteAsync(AppDbContext db, string id, string channelId, string name) =>
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Emotes"
                ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "LastSyncedAt")
            VALUES ({0}, {1}, {2}, {3}, '', false, {4});
            """,
            id,
            $"7tv-{id}",
            channelId,
            name,
            DateTime.UtcNow);

    private static async Task InsertUsageStatAsync(AppDbContext db, string emoteId, DateOnly date, int useCount) =>
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "UsageStats" ("EmoteId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
            VALUES ({0}, {1}, {2}, 0, 0);
            """,
            emoteId,
            date,
            useCount);
}
