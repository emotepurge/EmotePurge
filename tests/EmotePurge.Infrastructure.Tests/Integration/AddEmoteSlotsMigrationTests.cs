using System.Text;
using EmotePurge.Infrastructure.Migrations;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// AddEmoteSlots (#371, M1 of epic #365): the additive half of the emote-slot migration. Each case gets
// its own database on the shared container, migrated to the migration *before* this one, so the seed
// input is written against the old schema; that is why the fixtures below are raw SQL for every table
// that gains a column. The predecessor is looked up from the migration chain by name, so the tests
// keep working when another migration lands in between (plan #371, E1).
//
// The two collapse constants of the rollback procedure and the reusable seed constant have no input
// on a pre-M2/M3 schema, so their cases simulate the target state with raw SQL after Up (plan E14):
// the unique (EmoteId, EmoteSetId, Date) index is dropped for UsageStats, the (RunId, EmoteId) primary
// key is swapped for (RunId, SlotId) for the snapshot. Children 3 and 5 repeat them against the real
// M2/M3 Down.
[Collection("Postgres")]
public class AddEmoteSlotsMigrationTests(PostgresFixture fixture)
{
    private const string MigrationName = "AddEmoteSlots";
    private const string UsageUniqueIndex = "IX_UsageStats_EmoteId_EmoteSetId_Date";

    [Fact]
    public async Task Up_RunsThroughOnAnEmptyDatabase_AndCreatesTheSchema()
    {
        var (databaseName, previous, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);

        await MigrateAsync(db, current);

        Assert.True(await TableExistsAsync(db, "EmoteSlots"));
        Assert.True(await ColumnExistsAsync(db, "UsageStats", "SlotId"));
        Assert.True(await ColumnExistsAsync(db, "ChatLogBackfillRunEmotes", "SlotId"));
        Assert.True(await ColumnExistsAsync(db, "ChatLogBackfillRunEmotes", "CreatedSlot"));
        Assert.True(await ColumnExistsAsync(db, "VoteSessionEmotes", "NamesAtCreation"));

        var authoritative = await IndexDefinitionAsync(db, "IX_EmoteSlots_EmoteId_AddedAt_Name_Authoritative");
        Assert.Contains("UNIQUE", authoritative);
        Assert.Contains("(\"EmoteId\", \"AddedAt\", \"Name\")", authoritative);
        Assert.Contains("WHERE", authoritative);
        Assert.Contains("\"AddedAtFromRest\"", authoritative);

        var byName = await IndexDefinitionAsync(db, "IX_EmoteSlots_EmoteId_Name");
        Assert.DoesNotContain("UNIQUE", byName);
        Assert.Contains("(\"EmoteId\", \"Name\")", byName);

        var usage = await IndexDefinitionAsync(db, "IX_UsageStats_SlotId_EmoteSetId_Date");
        Assert.Contains("(\"SlotId\", \"EmoteSetId\", \"Date\")", usage);
        Assert.Contains("INCLUDE (\"UseCount\")", usage);

        Assert.Equal(
            ["c"],
            await QueryAsync(
                db,
                """SELECT confdeltype::text AS "Value" FROM pg_constraint WHERE conname = 'FK_EmoteSlots_Emotes_EmoteId';"""));
        Assert.Equal(
            ["c"],
            await QueryAsync(
                db,
                """SELECT confdeltype::text AS "Value" FROM pg_constraint WHERE conname = 'FK_UsageStats_EmoteSlots_SlotId';"""));
        Assert.Equal("0", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "EmoteSlots";"""));
        Assert.Equal("1", await OneAsync(db, HistoryCountSql));

        // The old unique key is untouched by M1: it falls in M2.
        Assert.NotEmpty(await IndexDefinitionAsync(db, UsageUniqueIndex));
    }

    [Fact]
    public async Task Up_SeedsOneUnsettledSeedPerEmoteSetPair_AndAttachesOnlyThatSetsRows()
    {
        var (databaseName, _, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await SeedStandardAsync(db);
        var pairSumsBefore = await OneAsync(db, PairSumsSql);

        await MigrateAsync(db, current);

        // X: sets A and B, Y: set A, Z: no usage and therefore no slot (D12).
        Assert.Equal("2", await OneAsync(db, SlotCountForEmoteSql("ex")));
        Assert.Equal("1", await OneAsync(db, SlotCountForEmoteSql("ey")));
        Assert.Equal("0", await OneAsync(db, SlotCountForEmoteSql("ez")));
        Assert.Equal("3", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "EmoteSlots";"""));

        Assert.Equal("0", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "UsageStats" WHERE "SlotId" IS NULL;"""));
        // Every pair sits on exactly one slot, and every slot carries exactly one pair.
        Assert.Equal(
            "3",
            await OneAsync(db, """SELECT count(DISTINCT ("EmoteId", "EmoteSetId", "SlotId"))::text AS "Value" FROM "UsageStats";"""));
        Assert.Equal(
            "3",
            await OneAsync(db, """SELECT count(DISTINCT ("EmoteId", "EmoteSetId"))::text AS "Value" FROM "UsageStats";"""));
        Assert.Equal(
            "3",
            await OneAsync(db, """SELECT count(DISTINCT "SlotId")::text AS "Value" FROM "UsageStats";"""));
        // The slot of a row belongs to the row's own emote.
        Assert.Equal(
            "0",
            await OneAsync(
                db,
                """
                SELECT count(*)::text AS "Value" FROM "UsageStats" u
                JOIN "EmoteSlots" s ON s."Id" = u."SlotId" WHERE s."EmoteId" <> u."EmoteId";
                """));

        // Seed shape: unsettled, no AddedAt although the emote has a FirstSeenAt.
        Assert.Equal(
            "0",
            await OneAsync(
                db,
                """
                SELECT count(*)::text AS "Value" FROM "EmoteSlots"
                WHERE "AddedAt" IS NOT NULL OR "AddedAtFromRest" OR NOT "SeedUnsettled";
                """));
        Assert.Equal(
            "1",
            await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "Emotes" WHERE "Id" = 'ex' AND "FirstSeenAt" IS NOT NULL;"""));

        // The Source = 1 row stays on its own set's seed with its Source.
        Assert.Equal(
            "1",
            await OneAsync(
                db,
                """
                SELECT count(*)::text AS "Value" FROM "UsageStats" u
                JOIN "EmoteSlots" s ON s."Id" = u."SlotId"
                WHERE u."EmoteId" = 'ex' AND u."EmoteSetId" = 'SETB' AND u."Source" = 1
                  AND s."Id" <> (SELECT DISTINCT "SlotId" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "EmoteSetId" = 'SETA');
                """));

        Assert.Equal(pairSumsBefore, await OneAsync(db, PairSumsSql));
        Assert.Equal("4", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "UsageStats";"""));
    }

    [Fact]
    public async Task Up_CopiesNameAndArchiveStateFromTheEmote_ForEverySeed()
    {
        var (databaseName, _, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(
            db,
            """
            INSERT INTO "Channels" ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
            VALUES ('c1', '1', 'chan', 'SETA', true, now(), NULL),
                   ('c2', '2', 'emptyset', '', true, now(), NULL);
            INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "ArchivedAt", "IsPlaceholder", "LastSyncedAt")
            VALUES ('e-active',   '7a', 'c1', 'ActiveOne',  '', false, NULL,         false, now()),
                   ('e-archived', '7b', 'c1', 'ArchivedOne', '', true,  '2026-09-01', false, now()),
                   ('e-holder',   '7c', 'c1', 'Placeholder', '', true,  NULL,         true,  now()),
                   ('e-in-b',     '7d', 'c1', 'OnlyInB',     '', false, NULL,         false, now()),
                   ('e-c2-act',   '7e', 'c2', 'EmptyActive', '', false, NULL,         false, now()),
                   ('e-c2-arch',  '7f', 'c2', 'EmptyArch',   '', true,  '2026-08-15', false, now());
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source")
            VALUES ('e-active',   'SETA', '2026-09-01', 1, 0, 0, 0),
                   ('e-active',   'SETB', '2026-09-01', 1, 0, 0, 0),
                   ('e-archived', 'SETA', '2026-09-01', 2, 0, 0, 0),
                   ('e-holder',   'SETA', '2026-09-01', 3, 0, 0, 1),
                   ('e-in-b',     'SETB', '2026-09-01', 4, 0, 0, 0),
                   ('e-c2-act',   'SETX', '2026-09-01', 5, 0, 0, 0),
                   ('e-c2-arch',  'SETX', '2026-09-01', 6, 0, 0, 0);
            """);
        var started = DateTime.UtcNow.AddSeconds(-5);

        await MigrateAsync(db, current);

        Assert.Equal("7", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "EmoteSlots";"""));
        // Name, IsArchived and ArchivedAt come 1:1 from the emote, for every seed, whatever its set.
        Assert.Equal(
            "0",
            await OneAsync(
                db,
                """
                SELECT count(*)::text AS "Value" FROM "EmoteSlots" s JOIN "Emotes" e ON e."Id" = s."EmoteId"
                WHERE s."Name" <> e."Name" OR s."IsArchived" <> e."IsArchived" OR s."ArchivedAt" IS DISTINCT FROM e."ArchivedAt";
                """));

        // The shapes the plan names, asserted explicitly so a symmetric bug in the join cannot hide.
        Assert.Equal(["false|", "false|"], await SlotStatesAsync(db, "e-active"));
        Assert.Equal(["true|2026-09-01"], await SlotStatesAsync(db, "e-archived"));
        Assert.Equal(["true|"], await SlotStatesAsync(db, "e-holder"));
        // An active emote whose rows sit only in a non-active set: the seed stays active (spec wording).
        Assert.Equal(["false|"], await SlotStatesAsync(db, "e-in-b"));
        // A channel without an active set: the seeds follow the emote.
        Assert.Equal(["false|"], await SlotStatesAsync(db, "e-c2-act"));
        Assert.Equal(["true|2026-08-15"], await SlotStatesAsync(db, "e-c2-arch"));

        // One shared stamp, taken at the migration.
        Assert.Equal("1", await OneAsync(db, """SELECT count(DISTINCT "LastSyncedAt")::text AS "Value" FROM "EmoteSlots";"""));
        var stamps = await db.Database
            .SqlQueryRaw<DateTime>("""SELECT min("LastSyncedAt") AS "Value" FROM "EmoteSlots";""")
            .ToListAsync();
        var stamp = Assert.Single(stamps);
        Assert.True(stamp >= started, $"LastSyncedAt {stamp:O} predates the test start {started:O}.");
    }

    [Fact]
    public async Task Up_FillsSnapshotSlotIdFromTheRunsSet_LeavesRowsWithoutSeedNull_AndAddsNamesAtCreation()
    {
        var (databaseName, _, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await SeedStandardAsync(db);

        await MigrateAsync(db, current);

        // The run is on set B: X has a seed there, Y has usage only in A and so stays NULL (E9).
        Assert.Equal(
            "ok",
            await OneAsync(
                db,
                """
                SELECT CASE WHEN re."SlotId" = (SELECT DISTINCT "SlotId" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "EmoteSetId" = 'SETB')
                            THEN 'ok' ELSE 'bad' END AS "Value"
                FROM "ChatLogBackfillRunEmotes" re WHERE re."EmoteId" = 'ex';
                """));
        Assert.Equal(
            "null",
            await OneAsync(db, """SELECT COALESCE("SlotId", 'null') AS "Value" FROM "ChatLogBackfillRunEmotes" WHERE "EmoteId" = 'ey';"""));
        Assert.Equal(
            "0",
            await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillRunEmotes" WHERE "CreatedSlot";"""));
        Assert.Equal("2", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillRunEmotes";"""));

        Assert.Equal("1", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "VoteSessionEmotes";"""));
        Assert.Equal(
            "1",
            await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "VoteSessionEmotes" WHERE "NamesAtCreation" IS NULL;"""));
    }

    [Fact]
    public async Task Up_DoesNotChangeAnyEmoteRow()
    {
        var (databaseName, _, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await SeedStandardAsync(db);
        var before = await OneAsync(db, EmotesDigestSql);
        Assert.NotEqual("", before);

        await MigrateAsync(db, current);

        Assert.Equal(before, await OneAsync(db, EmotesDigestSql));
        // The digest covers FirstSeenAt, Name and IsArchived; spell the one the seed must never touch.
        Assert.Equal(
            "1",
            await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "Emotes" WHERE "Id" = 'ex' AND "FirstSeenAt" = '2026-01-01';"""));
    }

    [Fact]
    public async Task Up_Aborts_WhenAnotherSessionHoldsUsageStats_AndLeavesNothingBehind()
    {
        var (databaseName, _, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await SeedStandardAsync(db);

        string failure;
        await using (var holder = new NpgsqlConnection(db.Database.GetConnectionString()))
        {
            await holder.OpenAsync();
            await using var transaction = await holder.BeginTransactionAsync();
            try
            {
                await using (var lockCommand = new NpgsqlCommand("""LOCK TABLE "UsageStats" IN ACCESS EXCLUSIVE MODE;""", holder, transaction))
                {
                    await lockCommand.ExecuteNonQueryAsync();
                }

                failure = await MigrateAndCaptureFailureAsync(db, current);
            }
            finally
            {
                // Release the lock whatever happened above; the connection closes with the await using.
                await transaction.RollbackAsync();
            }
        }

        Assert.Contains("lock timeout", failure, StringComparison.OrdinalIgnoreCase);
        Assert.False(await TableExistsAsync(db, "EmoteSlots"));
        Assert.False(await ColumnExistsAsync(db, "UsageStats", "SlotId"));
        Assert.False(await ColumnExistsAsync(db, "ChatLogBackfillRunEmotes", "SlotId"));
        Assert.False(await ColumnExistsAsync(db, "ChatLogBackfillRunEmotes", "CreatedSlot"));
        Assert.False(await ColumnExistsAsync(db, "VoteSessionEmotes", "NamesAtCreation"));
        Assert.Equal("0", await OneAsync(db, HistoryCountSql));
    }

    [Fact]
    public async Task Down_RemovesEverything_AndKeepsRowsAndSums()
    {
        var (databaseName, previous, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await SeedStandardAsync(db);
        await MigrateAsync(db, current);
        var pairSums = await OneAsync(db, PairSumsSql);

        await MigrateAsync(db, previous);

        Assert.False(await TableExistsAsync(db, "EmoteSlots"));
        Assert.False(await ColumnExistsAsync(db, "UsageStats", "SlotId"));
        Assert.False(await ColumnExistsAsync(db, "ChatLogBackfillRunEmotes", "SlotId"));
        Assert.False(await ColumnExistsAsync(db, "ChatLogBackfillRunEmotes", "CreatedSlot"));
        Assert.False(await ColumnExistsAsync(db, "VoteSessionEmotes", "NamesAtCreation"));
        Assert.Empty(await IndexDefinitionAsync(db, "IX_UsageStats_SlotId_EmoteSetId_Date"));
        Assert.Equal("0", await OneAsync(db, HistoryCountSql));

        Assert.Equal(pairSums, await OneAsync(db, PairSumsSql));
        Assert.Equal("4", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "UsageStats";"""));
        Assert.Equal("2", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillRunEmotes";"""));
        Assert.Equal("1", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "VoteSessionEmotes";"""));
    }

    [Fact]
    public async Task UpDownUp_SeedsTheSameStructureAgain_WithNewIds()
    {
        var (databaseName, previous, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await SeedStandardAsync(db);

        await MigrateAsync(db, current);
        var structureFirst = await OneAsync(db, PairToSlotStructureSql);
        var idsFirst = await QueryAsync(db, """SELECT "Id" AS "Value" FROM "EmoteSlots" ORDER BY 1;""");

        await MigrateAsync(db, previous);
        await MigrateAsync(db, current);
        var idsSecond = await QueryAsync(db, """SELECT "Id" AS "Value" FROM "EmoteSlots" ORDER BY 1;""");

        Assert.Equal(structureFirst, await OneAsync(db, PairToSlotStructureSql));
        Assert.Equal(idsFirst.Count, idsSecond.Count);
        Assert.Empty(idsFirst.Intersect(idsSecond));
    }

    [Fact]
    public async Task CollapseUsageStatsSql_SumsOntoTheOldestSlot_RestoresUniqueness_AndIsIdempotent()
    {
        var (databaseName, previous, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(
            db,
            """
            INSERT INTO "Channels" ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
            VALUES ('c1', '1', 'chan', 'SETA', true, now(), NULL);
            INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "LastSyncedAt")
            VALUES ('ex', '7x', 'c1', 'EmoteX', '', false, now()),
                   ('ey', '7y', 'c1', 'EmoteY', '', false, now());
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source")
            VALUES ('ex', 'SETA', '2026-09-01', 5, 1, 0, 0),
                   ('ex', 'SETA', '2026-09-02', 2, 0, 0, 0),
                   ('ex', 'SETA', '2026-09-04', 8, 0, 0, 0),
                   ('ey', 'SETA', '2026-09-01', 9, 0, 0, 0);
            """);
        await MigrateAsync(db, current);

        // Simulated M2 state: the unique key is gone and a pair can carry several slots.
        await Exec(db, $"""DROP INDEX "{UsageUniqueIndex}";""");
        var seed = await OneAsync(db, """SELECT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "Date" = '2026-09-01';""");
        await Exec(
            db,
            """
            INSERT INTO "EmoteSlots" ("Id", "EmoteId", "Name", "AddedAt", "AddedAtFromRest", "LastSyncedAt")
            VALUES ('x-dated', 'ex', 'Zeta',  '2026-03-01', true,  now()),
                   ('x-null',  'ex', 'Alpha', NULL,         false, now()),
                   ('x-upper', 'ex', 'Bravo', NULL,         false, now()),
                   ('x-lower', 'ex', 'alpha', NULL,         false, now());
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source", "SlotId")
            VALUES
              -- dated beats the NULL-dated seed; Source 0 + 1 -> 0
              ('ex', 'SETA', '2026-09-01', 7, 2, 3, 1, 'x-dated'),
              -- two NULL-dated slots ('EmoteX' seed vs 'Alpha'): the ordinal smaller name wins
              ('ex', 'SETA', '2026-09-02', 4, 1, 1, 0, 'x-null'),
              -- ordinal order: 'Bravo' before 'alpha' (an uppercase letter sorts first under "C"); Source 1 + 1 -> 1
              ('ex', 'SETA', '2026-09-03', 10, 1, 2, 1, 'x-lower'),
              ('ex', 'SETA', '2026-09-03', 20, 2, 4, 1, 'x-upper');
            """);
        var sumsBefore = await OneAsync(db, PairTotalsSql);

        var removed = await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.CollapseUsageStatsSql);

        Assert.Equal(3, removed);
        Assert.Equal(sumsBefore, await OneAsync(db, PairTotalsSql));
        Assert.Equal("12|3|3|0|x-dated", await OneAsync(db, CellSql("ex", "SETA", "2026-09-01")));
        Assert.Equal("6|1|1|0|x-null", await OneAsync(db, CellSql("ex", "SETA", "2026-09-02")));
        Assert.Equal("30|3|6|1|x-upper", await OneAsync(db, CellSql("ex", "SETA", "2026-09-03")));
        // Singletons are untouched, down to their slot.
        Assert.Equal($"8|0|0|0|{seed}", await OneAsync(db, CellSql("ex", "SETA", "2026-09-04")));
        Assert.Equal("9|0|0|0|" + await OneAsync(db, """SELECT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ey';"""), await OneAsync(db, CellSql("ey", "SETA", "2026-09-01")));
        Assert.Equal("5", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "UsageStats";"""));

        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.CollapseUsageStatsSql));

        // Uniqueness holds again, in the exact form of M2's Down guard.
        await Exec(db, $"""CREATE UNIQUE INDEX "{UsageUniqueIndex}" ON "UsageStats" ("EmoteId", "EmoteSetId", "Date") INCLUDE ("UseCount");""");

        await MigrateAsync(db, previous);
        Assert.False(await TableExistsAsync(db, "EmoteSlots"));
        Assert.Equal("5", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "UsageStats";"""));
    }

    [Fact]
    public async Task SeedSql_AttachesLateRowsOfExistingAndNewPairs_AndIsIdempotent()
    {
        var (databaseName, _, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await SeedStandardAsync(db);
        await MigrateAsync(db, current);
        var seedOfXA = await OneAsync(db, """SELECT DISTINCT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "EmoteSetId" = 'SETA';""");
        var maxIdBefore = long.Parse(await OneAsync(db, """SELECT max("Id")::text AS "Value" FROM "UsageStats";"""));
        var slottedBefore = await OneAsync(db, SlottedRowsDigestSql, maxIdBefore);
        Assert.Equal("3", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "EmoteSlots";"""));

        // Rows as the unchanged flush writes them after M1: no SlotId. (i) existing pair, new date;
        // (ii) a new set of an existing emote; (iii) an emote that never had a slot.
        await Exec(
            db,
            """
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
            VALUES ('ex', 'SETA', '2026-09-03', 1, 0, 0),
                   ('ey', 'SETB', '2026-09-03', 2, 0, 0),
                   ('ez', 'SETA', '2026-09-03', 4, 0, 0),
                   ('ez', 'SETA', '2026-09-04', 4, 0, 0);
            """);

        var first = await db.Database.ExecuteSqlRawAsync(EmoteSlotSeedSql.AttachUnslottedUsageRowsSql);

        Assert.Equal(4, first);
        Assert.Equal("0", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "UsageStats" WHERE "SlotId" IS NULL;"""));
        Assert.Equal("5", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "EmoteSlots";"""));
        Assert.Equal(seedOfXA, await OneAsync(db, """SELECT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "Date" = '2026-09-03';"""));
        Assert.Equal(slottedBefore, await OneAsync(db, SlottedRowsDigestSql, maxIdBefore));

        // The two new seeds: values from Emotes, unsettled, one shared pair each.
        Assert.Equal(
            "2",
            await OneAsync(
                db,
                """
                SELECT count(*)::text AS "Value" FROM "EmoteSlots" s JOIN "Emotes" e ON e."Id" = s."EmoteId"
                WHERE s."Id" IN (SELECT "SlotId" FROM "UsageStats" WHERE "Date" = '2026-09-03' AND "EmoteId" IN ('ey', 'ez'))
                  AND s."SeedUnsettled" AND s."AddedAt" IS NULL AND NOT s."AddedAtFromRest"
                  AND s."Name" = e."Name" AND s."IsArchived" = e."IsArchived" AND s."ArchivedAt" IS NOT DISTINCT FROM e."ArchivedAt";
                """));
        Assert.Equal(
            "1",
            await OneAsync(db, """SELECT count(DISTINCT "SlotId")::text AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ez';"""));
        Assert.Equal(
            "ex",
            await OneAsync(db, """SELECT s."EmoteId" AS "Value" FROM "UsageStats" u JOIN "EmoteSlots" s ON s."Id" = u."SlotId" WHERE u."EmoteId" = 'ex' AND u."Date" = '2026-09-03';"""));

        // Idempotent: a second run, even twice in one transaction, changes and creates nothing.
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(EmoteSlotSeedSql.AttachUnslottedUsageRowsSql));
            Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(EmoteSlotSeedSql.AttachUnslottedUsageRowsSql));
            await transaction.CommitAsync();
        }

        Assert.Equal("5", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "EmoteSlots";"""));
    }

    [Fact]
    public async Task SeedSql_AttachesToTheSlotCarryingThePairsRows_AfterASimulatedSettle_AndPicksTheOldestWhenSeveralDo()
    {
        var (databaseName, _, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await SeedStandardAsync(db);
        await MigrateAsync(db, current);

        // (a) The seed of (X, A) is settled the way child 2 does it: AddedAt, authoritative, renamed.
        var seedOfXA = await OneAsync(db, """SELECT DISTINCT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "EmoteSetId" = 'SETA';""");
        await Exec(
            db,
            """
            UPDATE "EmoteSlots" SET "AddedAt" = '2026-05-01', "AddedAtFromRest" = true, "SeedUnsettled" = false, "Name" = 'b-settled'
            WHERE "Id" = (SELECT DISTINCT "SlotId" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "EmoteSetId" = 'SETA');
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
            VALUES ('ex', 'SETA', '2026-09-12', 9, 0, 0);
            """);

        Assert.Equal(1, await db.Database.ExecuteSqlRawAsync(EmoteSlotSeedSql.AttachUnslottedUsageRowsSql));
        Assert.Equal(seedOfXA, await OneAsync(db, """SELECT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "Date" = '2026-09-12';"""));
        Assert.Equal("3", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "EmoteSlots";"""));

        // (b) Several slots carry rows of (X, A) on other dates: the D2-oldest takes the NULL row.
        await Exec(
            db,
            """
            INSERT INTO "EmoteSlots" ("Id", "EmoteId", "Name", "AddedAt", "AddedAtFromRest", "LastSyncedAt")
            VALUES ('older',    'ex', 'z-older',    '2026-04-01', true,  now()),
                   ('nulldate', 'ex', 'A-nulldate', NULL,         false, now());
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "SlotId")
            VALUES ('ex', 'SETA', '2026-09-10', 1, 0, 0, 'older'),
                   ('ex', 'SETA', '2026-09-11', 1, 0, 0, 'nulldate');
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
            VALUES ('ex', 'SETA', '2026-09-13', 6, 0, 0);
            """);
        var sumBefore = await OneAsync(db, PairSumsSql);

        Assert.Equal(1, await db.Database.ExecuteSqlRawAsync(EmoteSlotSeedSql.AttachUnslottedUsageRowsSql));

        Assert.Equal("older", await OneAsync(db, """SELECT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "Date" = '2026-09-13';"""));
        Assert.Equal("5", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "EmoteSlots";"""));
        Assert.Equal(sumBefore, await OneAsync(db, PairSumsSql));
        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(EmoteSlotSeedSql.AttachUnslottedUsageRowsSql));
    }

    [Fact]
    public async Task CollapseBackfillSnapshotSql_KeepsTheOldestSlotsRow_PerRunAndEmote_AndRestoresThePrimaryKey()
    {
        var (databaseName, previous, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(
            db,
            """
            INSERT INTO "Channels" ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
            VALUES ('c1', '1', 'chan', 'SETA', true, now(), NULL);
            INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "LastSyncedAt")
            VALUES ('ex', '7x', 'c1', 'EmoteX', '', false, now()),
                   ('ey', '7y', 'c1', 'EmoteY', '', false, now()),
                   ('ez', '7z', 'c1', 'EmoteZ', '', false, now()),
                   ('ev', '7v', 'c1', 'EmoteV', '', false, now());
            -- ez has no usage at all: it gets no seed, its slots below are the only ones.
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source")
            VALUES ('ex', 'SETA', '2026-09-01', 1, 0, 0, 0),
                   ('ey', 'SETA', '2026-09-01', 1, 0, 0, 0),
                   ('ev', 'SETA', '2026-09-01', 1, 0, 0, 0);
            INSERT INTO "ChatLogBackfillRuns"
                ("ChannelId", "Status", "RequestedMonths", "WindowFrom", "WindowTo", "WeeksTotal", "EmoteSetId", "ArchiveBaseUrl",
                 "RequestedByTwitchUserId", "RequestedByLogin", "RequestedAtUtc")
            VALUES ('c1', 'completed', 1, '2026-08-01', '2026-09-01', 5, 'SETA', 'https://logs.cyex.app/', '42', 'r1', now()),
                   ('c1', 'completed', 1, '2026-07-01', '2026-08-01', 5, 'SETA', 'https://logs.cyex.app/', '42', 'r2', now());
            INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow")
            SELECT r."Id", v.e, v.s, v.n, false
            FROM "ChatLogBackfillRuns" r,
                 (VALUES ('ex', '7x', 'EmoteX'), ('ey', '7y', 'EmoteY'), ('ev', '7v', 'EmoteV')) v(e, s, n)
            WHERE r."RequestedByLogin" = 'r1';
            INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow")
            SELECT r."Id", v.e, v.s, v.n, false
            FROM "ChatLogBackfillRuns" r,
                 (VALUES ('ex', '7x', 'EmoteX'), ('ey', '7y', 'EmoteY')) v(e, s, n)
            WHERE r."RequestedByLogin" = 'r2';
            """);
        await MigrateAsync(db, current);
        // Up bound every snapshot row to its pair's seed; the simulation below needs that (PK columns).
        Assert.Equal("0", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillRunEmotes" WHERE "SlotId" IS NULL;"""));
        var seedOfEy = await OneAsync(db, """SELECT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ey';""");
        var seedOfEv = await OneAsync(db, """SELECT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ev';""");

        // Simulated M3 state: one snapshot row per (RunId, SlotId) instead of per (RunId, EmoteId).
        await Exec(
            db,
            """
            ALTER TABLE "ChatLogBackfillRunEmotes" DROP CONSTRAINT "PK_ChatLogBackfillRunEmotes";
            ALTER TABLE "ChatLogBackfillRunEmotes" ADD CONSTRAINT "PK_ChatLogBackfillRunEmotes" PRIMARY KEY ("RunId", "SlotId");
            INSERT INTO "EmoteSlots" ("Id", "EmoteId", "Name", "AddedAt", "AddedAtFromRest", "LastSyncedAt")
            VALUES ('x-dated',  'ex', 'Zeta',  '2026-03-01', true,  now()),
                   ('y-null',   'ey', 'Alpha', NULL,         false, now()),
                   ('z-upper',  'ez', 'Bravo', NULL,         false, now()),
                   ('z-lower',  'ez', 'alpha', NULL,         false, now());
            INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow", "SlotId")
            SELECT r."Id", v.e, v.s, v.n, false, v.slot
            FROM "ChatLogBackfillRuns" r,
                 (VALUES ('ex', '7x', 'Zeta',  'x-dated'),
                         ('ey', '7y', 'Alpha', 'y-null'),
                         ('ez', '7z', 'alpha', 'z-lower'),
                         ('ez', '7z', 'Bravo', 'z-upper')) v(e, s, n, slot)
            WHERE r."RequestedByLogin" = 'r1';
            -- the second run: the dated slot again, decided on its own
            INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow", "SlotId")
            SELECT r."Id", 'ex', '7x', 'Zeta', false, 'x-dated' FROM "ChatLogBackfillRuns" r WHERE r."RequestedByLogin" = 'r2';
            """);
        Assert.Equal("10", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillRunEmotes";"""));

        var removed = await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.CollapseBackfillSnapshotSql);

        Assert.Equal(4, removed);
        Assert.Equal(
            [
                "r1|ev|" + seedOfEv,
                "r1|ex|x-dated",
                "r1|ey|y-null",
                "r1|ez|z-upper",
                "r2|ex|x-dated",
                "r2|ey|" + seedOfEy
            ],
            await QueryAsync(
                db,
                """
                SELECT r."RequestedByLogin" || '|' || re."EmoteId" || '|' || re."SlotId" AS "Value"
                FROM "ChatLogBackfillRunEmotes" re JOIN "ChatLogBackfillRuns" r ON r."Id" = re."RunId"
                ORDER BY r."RequestedByLogin", re."EmoteId";
                """));

        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.CollapseBackfillSnapshotSql));

        // The old primary key is valid again, in the form of M3's Down guard.
        await Exec(
            db,
            """
            ALTER TABLE "ChatLogBackfillRunEmotes" DROP CONSTRAINT "PK_ChatLogBackfillRunEmotes";
            ALTER TABLE "ChatLogBackfillRunEmotes" ADD CONSTRAINT "PK_ChatLogBackfillRunEmotes" PRIMARY KEY ("RunId", "EmoteId");
            """);

        await MigrateAsync(db, previous);
        Assert.False(await ColumnExistsAsync(db, "ChatLogBackfillRunEmotes", "SlotId"));
        Assert.Equal("6", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillRunEmotes";"""));
    }

    private const string HistoryCountSql =
        """SELECT count(*)::text AS "Value" FROM "__EFMigrationsHistory" WHERE "MigrationId" LIKE '%\_AddEmoteSlots';""";

    // Per (EmoteId, EmoteSetId): row count and the three counter sums. Slot-agnostic, so it reads the
    // same before and after M1 and after the collapse.
    private const string PairSumsSql =
        """
        SELECT COALESCE(string_agg(t.line, ';' ORDER BY t.line), '') AS "Value" FROM (
            SELECT "EmoteId" || '|' || "EmoteSetId" || '|' || count(*) || '|' || sum("UseCount") || '|'
                   || sum("BotUseCount") || '|' || sum("SharedChatUseCount") AS line
            FROM "UsageStats" GROUP BY "EmoteId", "EmoteSetId"
        ) t;
        """;

    // The same without the row count: the collapse merges rows, it must not change the sums.
    private const string PairTotalsSql =
        """
        SELECT COALESCE(string_agg(t.line, ';' ORDER BY t.line), '') AS "Value" FROM (
            SELECT "EmoteId" || '|' || "EmoteSetId" || '|' || sum("UseCount") || '|'
                   || sum("BotUseCount") || '|' || sum("SharedChatUseCount") AS line
            FROM "UsageStats" GROUP BY "EmoteId", "EmoteSetId"
        ) t;
        """;

    // Every Emotes row as text, so any changed column (FirstSeenAt, Name, IsArchived, ...) flips the hash.
    private const string EmotesDigestSql =
        """SELECT md5(COALESCE(string_agg(e::text, '|' ORDER BY e."Id"), '')) AS "Value" FROM "Emotes" e;""";

    private const string PairToSlotStructureSql =
        """
        SELECT COALESCE(string_agg(t.line, ';' ORDER BY t.line), '') AS "Value" FROM (
            SELECT DISTINCT u."EmoteId" || '|' || u."EmoteSetId" || '|' || s."Name" || '|' || s."SeedUnsettled" AS line
            FROM "UsageStats" u JOIN "EmoteSlots" s ON s."Id" = u."SlotId"
        ) t;
        """;

    private const string SlottedRowsDigestSql =
        """
        SELECT md5(COALESCE(string_agg(u."Id" || ':' || u."SlotId", '|' ORDER BY u."Id"), '')) AS "Value"
        FROM "UsageStats" u WHERE u."SlotId" IS NOT NULL AND u."Id" <= {0};
        """;

    private static string SlotCountForEmoteSql(string emoteId) =>
        $"""SELECT count(*)::text AS "Value" FROM "EmoteSlots" WHERE "EmoteId" = '{emoteId}';""";

    private static string CellSql(string emoteId, string setId, string date) =>
        $"""
        SELECT "UseCount" || '|' || "BotUseCount" || '|' || "SharedChatUseCount" || '|' || "Source" || '|' || "SlotId" AS "Value"
        FROM "UsageStats" WHERE "EmoteId" = '{emoteId}' AND "EmoteSetId" = '{setId}' AND "Date" = '{date}';
        """;

    // Channel c1 (active set A), emotes X (active, FirstSeenAt), Y (archived), Z (no usage); X is used in
    // sets A and B (one Source = 1 row), Y in A. One completed run on set B snapshots X and Y, one ended
    // ballot holds X. Written as the old images wrote it: no slot anywhere.
    private static async Task SeedStandardAsync(AppDbContext db) =>
        await Exec(
            db,
            """
            INSERT INTO "Channels" ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
            VALUES ('c1', '1', 'chan', 'SETA', true, now(), NULL);
            INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "ArchivedAt", "FirstSeenAt", "LastSyncedAt")
            VALUES ('ex', '7x', 'c1', 'EmoteX', '', false, NULL,         '2026-01-01', now()),
                   ('ey', '7y', 'c1', 'EmoteY', '', true,  '2026-09-01', NULL,         now()),
                   ('ez', '7z', 'c1', 'EmoteZ', '', false, NULL,         NULL,         now());
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source")
            VALUES ('ex', 'SETA', '2026-09-01', 5, 1, 0, 0),
                   ('ex', 'SETA', '2026-09-02', 7, 0, 2, 0),
                   ('ex', 'SETB', '2026-09-01', 3, 0, 0, 1),
                   ('ey', 'SETA', '2026-09-01', 11, 0, 0, 0);
            INSERT INTO "ChatLogBackfillRuns"
                ("ChannelId", "Status", "RequestedMonths", "WindowFrom", "WindowTo", "WeeksTotal", "EmoteSetId", "ArchiveBaseUrl",
                 "RequestedByTwitchUserId", "RequestedByLogin", "RequestedAtUtc")
            VALUES ('c1', 'completed', 1, '2026-08-01', '2026-09-01', 5, 'SETB', 'https://logs.cyex.app/', '42', 'someone', now());
            INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow")
            SELECT r."Id", v.e, v.s, v.n, false
            FROM "ChatLogBackfillRuns" r, (VALUES ('ex', '7x', 'EmoteX'), ('ey', '7y', 'EmoteY')) v(e, s, n);
            INSERT INTO "VoteSessions" ("ChannelId", "Title", "AllowedVoterRoles", "IsActive", "HideResultsUntilEnd", "StartedAt", "EndedAt")
            VALUES ('c1', 'Ballot', 0, false, false, now(), now());
            INSERT INTO "VoteSessionEmotes" ("VoteSessionId", "EmoteId")
            SELECT "Id", 'ex' FROM "VoteSessions";
            """);

    private async Task<(string DatabaseName, string Previous, string Current)> CreateDatabaseAtPreviousMigrationAsync()
    {
        var databaseName = $"migration_check_{Guid.NewGuid():N}";
        await using (var admin = fixture.CreateDbContext())
        {
#pragma warning disable EF1002 // locally generated Guid, CREATE DATABASE cannot be parameterized
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {databaseName}");
#pragma warning restore EF1002
        }

        await using var db = fixture.CreateDbContext(databaseName);
        var chain = db.GetService<IMigrationsAssembly>().Migrations.Keys.Order(StringComparer.Ordinal).ToList();
        var index = chain.FindIndex(id => id.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        Assert.True(index > 0, "AddEmoteSlots must have a predecessor in the migration chain.");
        await MigrateAsync(db, chain[index - 1]);

        return (databaseName, chain[index - 1], chain[index]);
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

    private static async Task<bool> ColumnExistsAsync(AppDbContext db, string table, string column) =>
        (await QueryAsync(
            db,
            """
            SELECT column_name AS "Value" FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = {0} AND column_name = {1};
            """,
            table,
            column)).Count > 0;

    private static async Task<bool> TableExistsAsync(AppDbContext db, string table) =>
        (await QueryAsync(
            db,
            """
            SELECT table_name AS "Value" FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = {0};
            """,
            table)).Count > 0;

    // "IsArchived|ArchivedAt (date)" per slot of the emote, sorted.
    private static async Task<List<string?>> SlotStatesAsync(AppDbContext db, string emoteId) =>
        await QueryAsync(
            db,
            """
            SELECT s."IsArchived"::text || '|' || COALESCE(to_char(s."ArchivedAt", 'YYYY-MM-DD'), '') AS "Value"
            FROM "EmoteSlots" s WHERE s."EmoteId" = {0} ORDER BY 1;
            """,
            emoteId);

    private static async Task<string> IndexDefinitionAsync(AppDbContext db, string indexName) =>
        (await QueryAsync(db, """SELECT indexdef AS "Value" FROM pg_indexes WHERE indexname = {0};""", indexName)).SingleOrDefault() ?? "";

    private static async Task<List<string?>> QueryAsync(AppDbContext db, string sql, params object[] parameters) =>
        await db.Database.SqlQueryRaw<string?>(sql, parameters).ToListAsync();

    private static async Task<string> OneAsync(AppDbContext db, string sql, params object[] parameters) =>
        (await QueryAsync(db, sql, parameters)).Single() ?? "";

    private static async Task Exec(AppDbContext db, string sql) =>
        await db.Database.ExecuteSqlRawAsync(sql);
}
