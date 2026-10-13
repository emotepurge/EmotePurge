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

// SwitchBackfillSnapshotToSlotKey (#375, M3 of epic #365, plan #375 §5 and 7.2): the snapshot key moves
// from (RunId, EmoteId) to (RunId, SlotId). Each case gets its own database on the shared container,
// migrated to M2 when the input is written the way the images before M3 wrote it (a NULL slot is only
// representable there), or to M3 itself for the Down cases. The migration ids are looked up from the
// chain by name, so another migration landing in between does not break the tests.
[Collection("Postgres")]
public class SwitchBackfillSnapshotToSlotKeyMigrationTests(PostgresFixture fixture)
{
    private const string M2Name = "SwitchUsageStatsToSlotKey";
    private const string M3Name = "SwitchBackfillSnapshotToSlotKey";
    private const string PrimaryKey = "PK_ChatLogBackfillRunEmotes";
    private const string SevenTvIndex = "IX_ChatLogBackfillRunEmotes_RunId_SevenTvEmoteId";

    private const string M3HistoryCountSql =
        """SELECT count(*)::text AS "Value" FROM "__EFMigrationsHistory" WHERE "MigrationId" LIKE '%\_SwitchBackfillSnapshotToSlotKey';""";

    private const string SlotIdNullableSql =
        """
        SELECT is_nullable AS "Value" FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'ChatLogBackfillRunEmotes' AND column_name = 'SlotId';
        """;

    private const string NullRowsSql = """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillRunEmotes" WHERE "SlotId" IS NULL;""";

    // Every snapshot row as "run login|emote|slot|CreatedSlot|CreatedRow", in a stable order.
    private const string SnapshotRowsSql =
        """
        SELECT r."RequestedByLogin" || '|' || re."EmoteId" || '|' || COALESCE(re."SlotId", 'null') || '|'
               || re."CreatedSlot" || '|' || re."CreatedRow" AS "Value"
        FROM "ChatLogBackfillRunEmotes" re JOIN "ChatLogBackfillRuns" r ON r."Id" = re."RunId"
        ORDER BY r."RequestedByLogin", re."EmoteId", re."SlotId";
        """;

    // ------------------------------------------------------------------ 1: schema

    [Fact]
    public async Task Up_RunsThroughOnAnEmptyDatabase_AndSwitchesTheKey()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.M2);
        await using var db = fixture.CreateDbContext(databaseName);

        await MigrateAsync(db, ids.M3);

        Assert.Equal("NO", await OneAsync(db, SlotIdNullableSql));
        Assert.Contains("(\"RunId\", \"SlotId\")", await IndexDefinitionAsync(db, PrimaryKey));
        Assert.Contains("(\"RunId\", \"SevenTvEmoteId\")", await IndexDefinitionAsync(db, SevenTvIndex));
        Assert.Equal("1", await OneAsync(db, M3HistoryCountSql));
    }

    // ------------------------------------------------------------------ 2: the NULL rule (E11, R5)

    // A finished run's row without a slot goes to the oldest slot (D2 order) of its emote that carries
    // rows of the run's set — per run, so the same emote lands on different slots for runs of different
    // sets; an emote whose slots carry no rows of the run's set loses the row. Rows with a slot stay as
    // they are; the notice names both counts.
    [Fact]
    public async Task Up_AttachesNullRowsOfFinishedRuns_ToTheSlotCarryingTheRunSetsRows_OrDeletesThem_AndReportsBoth()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.M2);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(db, NullRuleSql);
        var notices = CaptureNotices(db);

        await MigrateAsync(db, ids.M3);

        Assert.Equal(
            [
                "runA|ex|x-a|false|false",
                "runB|ew|w-slot|true|false",
                "runB|ex|x-old|false|true"
            ],
            await QueryAsync(db, SnapshotRowsSql));
        Assert.Contains(
            notices,
            n => n.Contains("2 snapshot row(s) of finished runs without a slot attached", StringComparison.Ordinal)
                && n.Contains("2 deleted (no such slot)", StringComparison.Ordinal));
        // No slot was created or changed.
        Assert.Equal("7", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "EmoteSlots";"""));
        Assert.Equal("NO", await OneAsync(db, SlotIdNullableSql));
    }

    // ------------------------------------------------------------------ 3: open run

    [Fact]
    public async Task Up_Refuses_WhenAnOpenRunHasASnapshotRowWithoutASlot_AndChangesNothing()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.M2);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(db, NullRuleSql);
        await Exec(db, """UPDATE "ChatLogBackfillRuns" SET "Status" = 'paused' WHERE "RequestedByLogin" = 'runB';""");
        var before = await QueryAsync(db, SnapshotRowsSql);

        var failure = await MigrateAndCaptureFailureAsync(db, ids.M3);

        Assert.Contains("check failed: 1 queued, running or paused backfill run(s) have a snapshot row without a slot", failure, StringComparison.Ordinal);
        Assert.Contains("Cancel those runs", failure, StringComparison.Ordinal);
        Assert.Contains("Nothing was applied.", failure, StringComparison.Ordinal);
        Assert.Equal(before, await QueryAsync(db, SnapshotRowsSql));
        Assert.Equal("4", await OneAsync(db, NullRowsSql));
        Assert.Equal("YES", await OneAsync(db, SlotIdNullableSql));
        Assert.Contains("(\"RunId\", \"EmoteId\")", await IndexDefinitionAsync(db, PrimaryKey));
        Assert.Equal("0", await OneAsync(db, M3HistoryCountSql));
    }

    // ------------------------------------------------------------------ 4: lock

    [Fact]
    public async Task Up_Aborts_WhenAnotherSessionHoldsTheSnapshotTable_AndLeavesNothingBehind()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.M2);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(db, NullRuleSql);
        var before = await QueryAsync(db, SnapshotRowsSql);

        string failure;
        await using (var holder = new NpgsqlConnection(db.Database.GetConnectionString()))
        {
            await holder.OpenAsync();
            await using var transaction = await holder.BeginTransactionAsync();
            try
            {
                await using (var lockCommand = new NpgsqlCommand("""LOCK TABLE "ChatLogBackfillRunEmotes" IN ACCESS EXCLUSIVE MODE;""", holder, transaction))
                {
                    await lockCommand.ExecuteNonQueryAsync();
                }

                failure = await MigrateAndCaptureFailureAsync(db, ids.M3);
            }
            finally
            {
                await transaction.RollbackAsync();
            }
        }

        Assert.Contains("lock timeout", failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await QueryAsync(db, SnapshotRowsSql));
        Assert.Equal("YES", await OneAsync(db, SlotIdNullableSql));
        Assert.Equal("0", await OneAsync(db, M3HistoryCountSql));
    }

    // ------------------------------------------------------------------ 5, 6: Down

    // With one row per emote and run (every run enqueued with the flag off) Down is lossless and Up runs
    // again; the rows Up deleted stay deleted.
    [Fact]
    public async Task Down_WithOneRowPerEmote_RestoresTheOldKey_KeepsTheRows_AndUpRunsAgain()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.M2);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(db, NullRuleSql);
        await MigrateAsync(db, ids.M3);
        var rows = await QueryAsync(db, SnapshotRowsSql);

        await MigrateAsync(db, ids.M2);

        Assert.Equal("YES", await OneAsync(db, SlotIdNullableSql));
        Assert.Contains("(\"RunId\", \"EmoteId\")", await IndexDefinitionAsync(db, PrimaryKey));
        Assert.Contains("(\"RunId\", \"SevenTvEmoteId\")", await IndexDefinitionAsync(db, SevenTvIndex));
        Assert.Equal("0", await OneAsync(db, M3HistoryCountSql));
        Assert.Equal(rows, await QueryAsync(db, SnapshotRowsSql));

        await MigrateAsync(db, ids.M3);

        Assert.Equal(rows, await QueryAsync(db, SnapshotRowsSql));
        Assert.Contains("(\"RunId\", \"SlotId\")", await IndexDefinitionAsync(db, PrimaryKey));
    }

    // Hand-off #371 against the real M3 (replaces the simulated M3 state of AddEmoteSlotsMigrationTests):
    // two slot rows of one emote in a run make Down refuse with a pointer to the collapse; the collapse
    // keeps the oldest slot's row per (run, emote) — dated before undated, then the name bytewise, which
    // the ICU column collation would order the other way round — and carries CreatedRow onto it when any
    // row of the emote had it; after that Down passes.
    [Fact]
    public async Task Down_RefusesWhileARunHasTwoSlotRowsOfAnEmote_AndPassesAfterTheCollapse_WhichKeepsCreatedRow()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.M3);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(db, CollapseSql);
        await Exec(db, EmoteSlotMigrationFixtures.LinguisticNameCollationSql);
        Assert.Equal("10", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillRunEmotes";"""));

        var failure = await MigrateAndCaptureFailureAsync(db, ids.M2);

        Assert.Contains("Down refused: 4 (run, emote) pair(s) have more than one snapshot row", failure, StringComparison.Ordinal);
        Assert.Contains("CollapseBackfillSnapshotSql", failure, StringComparison.Ordinal);
        Assert.Equal("1", await OneAsync(db, M3HistoryCountSql));
        Assert.Equal("NO", await OneAsync(db, SlotIdNullableSql));

        Assert.Equal(4, await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.CollapseBackfillSnapshotSql));

        Assert.Equal(
            [
                "r1|ev|v-seed|false|false",
                "r1|ex|x-dated|false|true",
                "r1|ey|y-null|false|false",
                "r1|ez|z-upper|true|true",
                "r2|ex|x-dated|false|false",
                "r2|ey|y-seed|false|false"
            ],
            await QueryAsync(db, SnapshotRowsSql));
        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.CollapseBackfillSnapshotSql));

        await MigrateAsync(db, ids.M2);

        Assert.Equal("0", await OneAsync(db, M3HistoryCountSql));
        Assert.Contains("(\"RunId\", \"EmoteId\")", await IndexDefinitionAsync(db, PrimaryKey));
        Assert.Equal("6", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillRunEmotes";"""));
    }

    // ------------------------------------------------------------------ 7: images from before #375

    // An image from before #375 (child 3) writes one snapshot row per emote, always with a slot: that
    // insert runs against M3 unchanged. One that omitted the slot (before #373) fails on NOT NULL — such
    // an image cannot run against M2 either.
    [Fact]
    public async Task OldImageShape_OneRowPerEmoteWithASlot_InsertsAfterM3_ARowWithoutASlotDoesNot()
    {
        var (databaseName, _) = await CreateDatabaseAsync(m => m.M3);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(db, BaseSql);

        await Exec(
            db,
            """
            INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "AddedToSetDay", "CreatedRow", "SlotId", "CreatedSlot")
            SELECT r."Id", v.e, v.s, v.n, date '2026-03-01', false, v.slot, v.created
            FROM "ChatLogBackfillRuns" r,
                 (VALUES ('ex', '7x', 'EmoteX', 'x-old', false), ('ey', '7y', 'EmoteY', 'y-a', true)) v(e, s, n, slot, created)
            WHERE r."RequestedByLogin" = 'runB';
            """);

        Assert.Equal(
            ["runB|ex|x-old|false|false", "runB|ey|y-a|true|false"],
            await QueryAsync(db, SnapshotRowsSql));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(
            db,
            """
            INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow")
            SELECT r."Id", 'ez', '7z', 'EmoteZ', false FROM "ChatLogBackfillRuns" r WHERE r."RequestedByLogin" = 'runB';
            """));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, ex.SqlState);
    }

    // ------------------------------------------------------------------ fixtures

    // Channel c1 (active set A); emotes ex, ey, ez, ew. Slots: ex has an undated (x-undated) and two
    // dated (x-old, x-new) slots carrying rows of set B and an older one carrying only rows of set A
    // (x-a); ey's only slot carries rows of set A; ez has a slot without rows; ew one carrying rows of B.
    // Two runs, both finished, written as on M2: runB (set B, completed) and runA (set A, failed).
    private const string BaseSql =
        """
        INSERT INTO "Channels" ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
        VALUES ('c1', '1', 'chan', 'SETA', true, now(), NULL);
        INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "LastSyncedAt")
        VALUES ('ex', '7x', 'c1', 'EmoteX', '', false, now()),
               ('ey', '7y', 'c1', 'EmoteY', '', false, now()),
               ('ez', '7z', 'c1', 'EmoteZ', '', false, now()),
               ('ew', '7w', 'c1', 'EmoteW', '', false, now());
        INSERT INTO "EmoteSlots" ("Id", "EmoteId", "Name", "AddedAt", "AddedAtFromRest", "LastSyncedAt")
        VALUES ('x-undated', 'ex', 'Aaa',    NULL,         false, now()),
               ('x-old',     'ex', 'Zeta',   '2026-03-01', true,  now()),
               ('x-new',     'ex', 'Alpha',  '2026-05-01', true,  now()),
               ('x-a',       'ex', 'EmoteX', '2026-01-01', true,  now()),
               ('y-a',       'ey', 'EmoteY', '2026-01-01', true,  now()),
               ('z-norows',  'ez', 'EmoteZ', '2026-01-01', true,  now()),
               ('w-slot',    'ew', 'EmoteW', '2026-01-01', true,  now());
        INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source", "SlotId")
        VALUES ('ex', 'SETB', '2026-08-01', 1, 0, 0, 1, 'x-undated'),
               ('ex', 'SETB', '2026-08-02', 2, 0, 0, 1, 'x-old'),
               ('ex', 'SETB', '2026-08-03', 3, 0, 0, 1, 'x-new'),
               ('ex', 'SETA', '2026-08-01', 4, 0, 0, 0, 'x-a'),
               ('ey', 'SETA', '2026-08-01', 5, 0, 0, 0, 'y-a'),
               ('ew', 'SETB', '2026-08-01', 6, 0, 0, 1, 'w-slot');
        INSERT INTO "ChatLogBackfillRuns"
            ("ChannelId", "Status", "RequestedMonths", "WindowFrom", "WindowTo", "WeeksTotal", "EmoteSetId", "ArchiveBaseUrl",
             "RequestedByTwitchUserId", "RequestedByLogin", "RequestedAtUtc")
        VALUES ('c1', 'completed', 1, '2026-07-01', '2026-08-05', 5, 'SETB', 'https://logs.cyex.app/', '42', 'runB', now()),
               ('c1', 'failed',    1, '2026-07-01', '2026-08-05', 5, 'SETA', 'https://logs.cyex.app/', '42', 'runA', now());
        """;

    // BaseSql plus the M2-shaped snapshots: runB holds ex, ey (CreatedRow), ez without a slot and ew on
    // its slot (CreatedSlot); runA holds ex without a slot.
    private const string NullRuleSql =
        BaseSql
        + """

        INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow", "SlotId", "CreatedSlot")
        SELECT r."Id", v.e, v.s, v.n, v.created_row, v.slot, v.created_slot
        FROM "ChatLogBackfillRuns" r,
             (VALUES ('ex', '7x', 'EmoteX', true,  NULL,     false),
                     ('ey', '7y', 'EmoteY', false, NULL,     false),
                     ('ez', '7z', 'EmoteZ', true,  NULL,     false),
                     ('ew', '7w', 'EmoteW', false, 'w-slot', true)) v(e, s, n, created_row, slot, created_slot)
        WHERE r."RequestedByLogin" = 'runB';
        INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow")
        SELECT r."Id", 'ex', '7x', 'EmoteX', false FROM "ChatLogBackfillRuns" r WHERE r."RequestedByLogin" = 'runA';
        """;

    // The collapse fixture (hand-off #371, formerly simulated on M1): two completed runs r1 and r2 of set
    // A. r1 holds ex on its seed and on a dated slot (CreatedRow only on the seed row, which the collapse
    // drops), ey on its seed and an undated slot 'Alpha' (seed id sorts after it), ez on 'Bravo' and
    // 'alpha' (both undated, both CreatedRow and CreatedSlot) and ev on its seed alone; r2 holds ex on
    // the dated slot and its seed, ey on its seed alone.
    private const string CollapseSql =
        """
        INSERT INTO "Channels" ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
        VALUES ('c1', '1', 'chan', 'SETA', true, now(), NULL);
        INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "LastSyncedAt")
        VALUES ('ex', '7x', 'c1', 'EmoteX', '', false, now()),
               ('ey', '7y', 'c1', 'EmoteY', '', false, now()),
               ('ez', '7z', 'c1', 'EmoteZ', '', false, now()),
               ('ev', '7v', 'c1', 'EmoteV', '', false, now());
        INSERT INTO "EmoteSlots" ("Id", "EmoteId", "Name", "AddedAt", "AddedAtFromRest", "LastSyncedAt", "SeedUnsettled")
        VALUES ('x-seed',  'ex', 'EmoteX', NULL,         false, now(), true),
               ('x-dated', 'ex', 'Zeta',   '2026-03-01', true,  now(), false),
               ('y-seed',  'ey', 'EmoteY', NULL,         false, now(), true),
               ('y-null',  'ey', 'Alpha',  NULL,         false, now(), false),
               ('z-upper', 'ez', 'Bravo',  NULL,         false, now(), false),
               ('z-lower', 'ez', 'alpha',  NULL,         false, now(), false),
               ('v-seed',  'ev', 'EmoteV', NULL,         false, now(), true);
        INSERT INTO "ChatLogBackfillRuns"
            ("ChannelId", "Status", "RequestedMonths", "WindowFrom", "WindowTo", "WeeksTotal", "EmoteSetId", "ArchiveBaseUrl",
             "RequestedByTwitchUserId", "RequestedByLogin", "RequestedAtUtc")
        VALUES ('c1', 'completed', 1, '2026-08-01', '2026-09-01', 5, 'SETA', 'https://logs.cyex.app/', '42', 'r1', now()),
               ('c1', 'completed', 1, '2026-07-01', '2026-08-01', 5, 'SETA', 'https://logs.cyex.app/', '42', 'r2', now());
        INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow", "SlotId", "CreatedSlot")
        SELECT r."Id", v.e, v.s, v.n, v.created_row, v.slot, v.created_slot
        FROM "ChatLogBackfillRuns" r,
             (VALUES ('ex', '7x', 'EmoteX', true,  'x-seed',  false),
                     ('ex', '7x', 'Zeta',   false, 'x-dated', false),
                     ('ey', '7y', 'EmoteY', false, 'y-seed',  false),
                     ('ey', '7y', 'Alpha',  false, 'y-null',  false),
                     ('ez', '7z', 'alpha',  true,  'z-lower', true),
                     ('ez', '7z', 'Bravo',  true,  'z-upper', true),
                     ('ev', '7v', 'EmoteV', false, 'v-seed',  false)) v(e, s, n, created_row, slot, created_slot)
        WHERE r."RequestedByLogin" = 'r1';
        INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow", "SlotId", "CreatedSlot")
        SELECT r."Id", v.e, v.s, v.n, false, v.slot, false
        FROM "ChatLogBackfillRuns" r,
             (VALUES ('ex', '7x', 'Zeta',   'x-dated'),
                     ('ex', '7x', 'EmoteX', 'x-seed'),
                     ('ey', '7y', 'EmoteY', 'y-seed')) v(e, s, n, slot)
        WHERE r."RequestedByLogin" = 'r2';
        """;

    // Notices the server sends on this context's connection (RAISE NOTICE in the migration).
    private static List<string> CaptureNotices(AppDbContext db)
    {
        var notices = new List<string>();
        ((NpgsqlConnection)db.Database.GetDbConnection()).Notice += (_, e) => notices.Add(e.Notice.MessageText);
        return notices;
    }

    // A new database on the shared container, migrated to the migration the selector picks.
    private async Task<(string DatabaseName, MigrationIds Ids)> CreateDatabaseAsync(Func<MigrationIds, string> target)
    {
        var databaseName = $"migration_m3_{Guid.NewGuid():N}";
        await using (var admin = fixture.CreateDbContext())
        {
#pragma warning disable EF1002 // locally generated Guid, CREATE DATABASE cannot be parameterized
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {databaseName}");
#pragma warning restore EF1002
        }

        await using var db = fixture.CreateDbContext(databaseName);
        var chain = db.GetService<IMigrationsAssembly>().Migrations.Keys.Order(StringComparer.Ordinal).ToList();
        var m2 = chain.FindIndex(id => id.EndsWith("_" + M2Name, StringComparison.Ordinal));
        var m3 = chain.FindIndex(id => id.EndsWith("_" + M3Name, StringComparison.Ordinal));
        Assert.True(m2 > 0 && m3 > m2, "SwitchBackfillSnapshotToSlotKey must follow SwitchUsageStatsToSlotKey in the migration chain.");
        var ids = new MigrationIds(chain[m3 - 1], chain[m3]);
        await MigrateAsync(db, target(ids));
        return (databaseName, ids);
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

    private static async Task<string> IndexDefinitionAsync(AppDbContext db, string indexName) =>
        (await QueryAsync(db, """SELECT indexdef AS "Value" FROM pg_indexes WHERE indexname = {0};""", indexName)).SingleOrDefault() ?? "";

    private static async Task<List<string?>> QueryAsync(AppDbContext db, string sql, params object[] parameters) =>
        await db.Database.SqlQueryRaw<string?>(sql, parameters).ToListAsync();

    private static async Task<string> OneAsync(AppDbContext db, string sql, params object[] parameters) =>
        (await QueryAsync(db, sql, parameters)).Single() ?? "";

    private static async Task Exec(AppDbContext db, string sql) =>
        await db.Database.ExecuteSqlRawAsync(sql);

    // M2 is the migration directly before M3 in the chain (whatever its name, should one land in between).
    private sealed record MigrationIds(string M2, string M3);
}
