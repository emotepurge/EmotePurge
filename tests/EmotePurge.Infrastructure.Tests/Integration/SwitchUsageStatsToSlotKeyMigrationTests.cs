using System.Text;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.ChatLogArchive;
using EmotePurge.Infrastructure.Migrations;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Integration.SlotSyncKit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// SwitchUsageStatsToSlotKey (#373, M2 of epic #365, plan #373 §5 and §6.1): the non-additive key switch.
// Each case gets its own database on the shared container, migrated to the state it starts from — the
// migration before M1 when the input is written the way the old images wrote it (no slot columns), M1
// when the old writer runs between M1 and M2, or M2 itself for the Down cases. The migration ids are
// looked up from the chain by name, so another migration landing in between does not break the tests.
[Collection("Postgres")]
public class SwitchUsageStatsToSlotKeyMigrationTests(PostgresFixture fixture)
{
    private const string M1Name = "AddEmoteSlots";
    private const string M2Name = "SwitchUsageStatsToSlotKey";
    private const string SlotIndex = "IX_UsageStats_SlotId_EmoteSetId_Date";
    private const string EmoteIndex = "IX_UsageStats_EmoteId_EmoteSetId_Date";

    private const string M2HistoryCountSql =
        """SELECT count(*)::text AS "Value" FROM "__EFMigrationsHistory" WHERE "MigrationId" LIKE '%\_SwitchUsageStatsToSlotKey';""";

    private const string SlotIdNullableSql =
        """
        SELECT is_nullable AS "Value" FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'UsageStats' AND column_name = 'SlotId';
        """;

    // Per (EmoteId, EmoteSetId): row count and the three counter sums. Slot-agnostic.
    private const string PairSumsSql =
        """
        SELECT COALESCE(string_agg(t.line, ';' ORDER BY t.line), '') AS "Value" FROM (
            SELECT "EmoteId" || '|' || "EmoteSetId" || '|' || count(*) || '|' || sum("UseCount") || '|'
                   || sum("BotUseCount") || '|' || sum("SharedChatUseCount") AS line
            FROM "UsageStats" GROUP BY "EmoteId", "EmoteSetId"
        ) t;
        """;

    private const string PairSumsDigestSql =
        """
        SELECT md5(COALESCE(string_agg(t.line, ';' ORDER BY t.line), '')) AS "Value" FROM (
            SELECT "EmoteId" || '|' || "EmoteSetId" || '|' || count(*) || '|' || sum("UseCount") || '|'
                   || sum("BotUseCount") || '|' || sum("SharedChatUseCount") AS line
            FROM "UsageStats" GROUP BY "EmoteId", "EmoteSetId"
        ) t;
        """;

    // Without the row count: the collapse merges rows, it must not change the sums.
    private const string PairTotalsSql =
        """
        SELECT COALESCE(string_agg(t.line, ';' ORDER BY t.line), '') AS "Value" FROM (
            SELECT "EmoteId" || '|' || "EmoteSetId" || '|' || sum("UseCount") || '|'
                   || sum("BotUseCount") || '|' || sum("SharedChatUseCount") AS line
            FROM "UsageStats" GROUP BY "EmoteId", "EmoteSetId"
        ) t;
        """;

    // Every row with its slot, and every slot: what a no-op Up must leave byte-identical.
    private const string SlotAssignmentDigestSql =
        """
        SELECT md5(
            COALESCE((SELECT string_agg(u."Id" || ':' || u."SlotId", '|' ORDER BY u."Id") FROM "UsageStats" u), '') || '#' ||
            COALESCE((SELECT string_agg(s::text, '|' ORDER BY s."Id") FROM "EmoteSlots" s), '')) AS "Value";
        """;

    private const string NullRowsSql = """SELECT count(*)::text AS "Value" FROM "UsageStats" WHERE "SlotId" IS NULL;""";

    private const string SlotCountSql = """SELECT count(*)::text AS "Value" FROM "EmoteSlots";""";

    // ------------------------------------------------------------------ 1: schema

    [Fact]
    public async Task Up_RunsThroughOnAnEmptyDatabase_AndSwitchesTheKey()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(M1Name);
        await using var db = fixture.CreateDbContext(databaseName);

        await MigrateAsync(db, ids.M2);

        Assert.Equal("NO", await OneAsync(db, SlotIdNullableSql));
        var slotIndex = await IndexDefinitionAsync(db, SlotIndex);
        Assert.Contains("UNIQUE", slotIndex);
        Assert.Contains("(\"SlotId\", \"EmoteSetId\", \"Date\")", slotIndex);
        Assert.Contains("INCLUDE (\"UseCount\")", slotIndex);
        var emoteIndex = await IndexDefinitionAsync(db, EmoteIndex);
        Assert.DoesNotContain("UNIQUE", emoteIndex);
        Assert.Contains("(\"EmoteId\", \"EmoteSetId\", \"Date\")", emoteIndex);
        Assert.Contains("INCLUDE (\"UseCount\")", emoteIndex);
        Assert.Equal("1", await OneAsync(db, M2HistoryCountSql));
    }

    // ------------------------------------------------------------------ 2: attach

    // Rows the old writer added with a NULL slot after M1: an existing pair's go onto its seed, a new
    // pair (a new set of a known emote, an emote that never had a slot) gets a new seed; sums per pair
    // stay the same.
    [Fact]
    public async Task Up_AttachesTheOldWritersNullRows_ToThePairsSeed_OrToANewSeed_AndKeepsPairSums()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.BeforeM1);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(db, StandardSql);
        await MigrateAsync(db, ids.M1);
        var seedOfXA = await OneAsync(db, """SELECT DISTINCT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "EmoteSetId" = 'SETA';""");
        await Exec(db, OldWriterRowsSql);
        var pairSums = await OneAsync(db, PairSumsSql);

        await MigrateAsync(db, ids.M2);

        Assert.Equal("0", await OneAsync(db, NullRowsSql));
        Assert.Equal(pairSums, await OneAsync(db, PairSumsSql));
        Assert.Equal(seedOfXA, await OneAsync(db, """SELECT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "EmoteSetId" = 'SETA' AND "Date" = '2026-09-03';"""));
        // M1 made 3 seeds (ex/A, ex/B, ey/A); the attach adds ey/B and ez/A.
        Assert.Equal("5", await OneAsync(db, SlotCountSql));
        Assert.Equal(
            "5",
            await OneAsync(db, """SELECT count(DISTINCT ("EmoteId", "EmoteSetId", "SlotId"))::text AS "Value" FROM "UsageStats";"""));
        Assert.Equal(
            "0",
            await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "EmoteSlots" WHERE NOT "SeedUnsettled" OR "AddedAt" IS NOT NULL;"""));
    }

    // ------------------------------------------------------------------ 3: lock

    [Fact]
    public async Task Up_Aborts_WhenAnotherSessionHoldsUsageStats_AndLeavesNothingBehind()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.BeforeM1);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(db, StandardSql);
        await MigrateAsync(db, ids.M1);
        await Exec(db, OldWriterRowsSql);
        var slotsBefore = await OneAsync(db, SlotCountSql);

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

                failure = await MigrateAndCaptureFailureAsync(db, ids.M2);
            }
            finally
            {
                await transaction.RollbackAsync();
            }
        }

        Assert.Contains("lock timeout", failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("YES", await OneAsync(db, SlotIdNullableSql));
        Assert.Equal("4", await OneAsync(db, NullRowsSql));
        Assert.Equal(slotsBefore, await OneAsync(db, SlotCountSql));
        Assert.DoesNotContain("UNIQUE", await IndexDefinitionAsync(db, SlotIndex));
        Assert.Equal("0", await OneAsync(db, M2HistoryCountSql));
    }

    // ------------------------------------------------------------------ 4: checks (a)–(e), plan E12

    // (a) cannot happen on a consistent database (every NULL row's emote exists, so the attach places
    // it); a trigger that keeps the slot NULL crafts it.
    [Fact]
    public async Task Up_CheckA_FailsLoudly_WhenARowStillHasNoSlotAfterTheAttach()
    {
        var failure = await RunM2OnCraftedM1StateAsync(
            """
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
            VALUES ('ex', 'SETA', '2026-09-03', 1, 0, 0);
            CREATE FUNCTION keep_slot_null() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW."SlotId" := NULL; RETURN NEW; END $$;
            CREATE TRIGGER keep_slot_null BEFORE UPDATE ON "UsageStats" FOR EACH ROW WHEN (OLD."SlotId" IS NULL) EXECUTE FUNCTION keep_slot_null();
            """);

        Assert.Contains("check (a) failed: 1 usage row(s) still have no slot", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Up_CheckB_FailsLoudly_WhenARowSitsOnASlotOfAnotherEmote()
    {
        var failure = await RunM2OnCraftedM1StateAsync(
            """
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "SlotId")
            SELECT 'ex', 'SETA', date '2026-09-05', 1, 0, 0, "SlotId" FROM "UsageStats" WHERE "EmoteId" = 'ey' LIMIT 1;
            """);

        Assert.Contains("check (b) failed: 1 usage row(s) sit on a slot of another emote", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Up_CheckC_FailsLoudly_WhenAPairCarriesRowsOnTwoUnsettledSeeds()
    {
        var failure = await RunM2OnCraftedM1StateAsync(
            """
            INSERT INTO "EmoteSlots" ("Id", "EmoteId", "Name", "AddedAt", "AddedAtFromRest", "LastSyncedAt", "SeedUnsettled")
            VALUES ('second-seed', 'ex', 'EmoteX', NULL, false, now(), true);
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "SlotId")
            VALUES ('ex', 'SETA', '2026-09-05', 1, 0, 0, 'second-seed');
            """);

        Assert.Contains("check (c) failed: 1 (emote, emote set) pair(s) carry rows on more than one unsettled seed", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Up_CheckD_FailsLoudly_WhenAnUnsettledSeedCarriesRowsOfTwoSets()
    {
        // ey's seed (set A) gets a row of set B as well; no other slot carries (ey, B), so (c) passes.
        var failure = await RunM2OnCraftedM1StateAsync(
            """
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "SlotId")
            SELECT 'ey', 'SETB', date '2026-09-05', 1, 0, 0, "SlotId" FROM "UsageStats" WHERE "EmoteId" = 'ey' LIMIT 1;
            """);

        Assert.Contains("check (d) failed: 1 unsettled seed(s) carry rows of more than one emote set", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Up_CheckE_FailsLoudly_WhenAnOpenBackfillRunHasASnapshotRowWithoutASlot_AndNamesTheWayOut()
    {
        // The standard run (set B, completed) snapshots ex and ey; ey has no seed in set B, so its
        // snapshot row is NULL since M1 (M1 E9). Reopening the run makes it a blocker.
        var failure = await RunM2OnCraftedM1StateAsync("""UPDATE "ChatLogBackfillRuns" SET "Status" = 'paused';""");

        Assert.Contains("check (e) failed: 1 queued, running or paused backfill run(s) have a snapshot row without a slot", failure, StringComparison.Ordinal);
        Assert.Contains("Cancel those runs", failure, StringComparison.Ordinal);
    }

    // The terminal run with the same NULL snapshot row passes (terminal runs are child 5's / M3's).
    [Fact]
    public async Task Up_CheckE_LetsATerminalRunWithANullSnapshotRowPass()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.BeforeM1);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(db, StandardSql);
        await MigrateAsync(db, ids.M1);
        Assert.Equal("1", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillRunEmotes" WHERE "SlotId" IS NULL;"""));

        await MigrateAsync(db, ids.M2);

        Assert.Equal("1", await OneAsync(db, M2HistoryCountSql));
    }

    // ------------------------------------------------------------------ 5: hand-off fixture (#371/#372)

    // M1 → the old writer (raw inserts without a slot) → the child-2 settle through SyncChannelAsync with
    // a faked 7TV client and the flag on → M2: the sums per (emote, set) survive, the existing pair's
    // late rows land on the settled slot, a pair the settle could not see gets its own seed next to the
    // REST slot, and the next sync settles that seed onto the REST slot ("target exists"). Replaces plan
    // 372 slot test 21.
    [Fact]
    public async Task HandOff_M1_OldWriter_Child2Settle_M2_KeepsSums_AndTheNextSyncSettlesTheNewSeedOntoTheRestSlot()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.BeforeM1);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(
            db,
            $"""
            INSERT INTO "Channels" ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
            VALUES ('c1', 'tw_m2_handoff', 'm2_handoff', '{SetA}', true, now(), NULL);
            INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "LastSyncedAt")
            VALUES ('ex', 'x', 'c1', 'A', '{Url("x")}', false, now()),
                   ('ey', 'y', 'c1', 'B', '{Url("y")}', false, now());
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source")
            VALUES ('ex', '{SetA}', '2026-08-01', 5, 1, 0, 0),
                   ('ex', '{SetB}', '2026-08-01', 3, 0, 1, 0);
            """);
        await MigrateAsync(db, ids.M1);
        var seedXA = await OneAsync(db, $"""SELECT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "EmoteSetId" = '{SetA}';""");
        var seedXB = await OneAsync(db, $"""SELECT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "EmoteSetId" = '{SetB}';""");

        // The old writer (an image from before #373 on the M1 schema): no slot.
        await Exec(
            db,
            $"""
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
            VALUES ('ex', '{SetA}', '2026-08-02', 7, 0, 0),
                   ('ey', '{SetA}', '2026-08-02', 4, 0, 2);
            """);

        // Child 2 with the flag on: ex's seed of the active set settles in place, ey gets a REST slot.
        var channel = await db.Channels.AsNoTracking().SingleAsync(c => c.Id == "c1");
        Assert.NotNull(await SyncOnAsync(db, channel, Live("x", "A", T1), Live("y", "B", T1)));
        var settledX = await SlotAsync(db, seedXA);
        Assert.Equal(("A", T1, true, false), (settledX.Name, settledX.AddedAt, settledX.AddedAtFromRest, settledX.SeedUnsettled));
        var restY = Assert.Single(await SlotsAsync(db, "ey"));
        var pairSums = await OneAsync(db, PairSumsSql);

        await MigrateAsync(db, ids.M2);

        Assert.Equal("0", await OneAsync(db, NullRowsSql));
        Assert.Equal(pairSums, await OneAsync(db, PairSumsSql));
        Assert.Equal([seedXA], await QueryAsync(db, $"""SELECT DISTINCT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "EmoteSetId" = '{SetA}';"""));
        Assert.Equal([seedXB], await QueryAsync(db, $"""SELECT DISTINCT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "EmoteSetId" = '{SetB}';"""));
        var newSeedY = Assert.Single(await QueryAsync(db, """SELECT DISTINCT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ey';"""));
        Assert.NotEqual(restY.Id, newSeedY);
        Assert.True((await SlotAsync(db, newSeedY!)).SeedUnsettled);

        Assert.NotNull(await SyncOnAsync(db, channel, Live("x", "A", T1), Live("y", "B", T1)));

        Assert.Equal(pairSums, await OneAsync(db, PairSumsSql));
        Assert.Equal([restY.Id], await QueryAsync(db, """SELECT DISTINCT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ey';"""));
        var emptiedSeed = await SlotAsync(db, newSeedY!);
        Assert.Equal((false, true, (DateTime?)null), (emptiedSeed.SeedUnsettled, emptiedSeed.IsArchived, emptiedSeed.ArchivedAt));
        // Only ex's seed of the other set stays unsettled: its set has not been reconciled.
        Assert.Equal(
            [seedXB],
            await QueryAsync(db, """SELECT s."Id" AS "Value" FROM "EmoteSlots" s WHERE s."SeedUnsettled";"""));
    }

    // ------------------------------------------------------------------ 6, 7: Down

    [Fact]
    public async Task Down_WithoutDuplicateCells_RestoresTheOldKey_KeepsSums_AndUpRunsAgain()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.BeforeM1);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(db, StandardSql);
        await MigrateAsync(db, ids.M1);
        await Exec(db, OldWriterRowsSql);
        await MigrateAsync(db, ids.M2);
        var pairSums = await OneAsync(db, PairSumsSql);
        var assignment = await OneAsync(db, SlotAssignmentDigestSql);

        await MigrateAsync(db, ids.M1);

        Assert.Equal("YES", await OneAsync(db, SlotIdNullableSql));
        Assert.Contains("UNIQUE", await IndexDefinitionAsync(db, EmoteIndex));
        Assert.DoesNotContain("UNIQUE", await IndexDefinitionAsync(db, SlotIndex));
        Assert.Equal("0", await OneAsync(db, M2HistoryCountSql));
        Assert.Equal(pairSums, await OneAsync(db, PairSumsSql));
        // Slot ids and seeds stay.
        Assert.Equal(assignment, await OneAsync(db, SlotAssignmentDigestSql));

        await MigrateAsync(db, ids.M2);

        Assert.Equal(assignment, await OneAsync(db, SlotAssignmentDigestSql));
        Assert.Contains("UNIQUE", await IndexDefinitionAsync(db, SlotIndex));
    }

    // The collapse fixture of the M1 tests against the real Down: two slots of one emote on the same day
    // make Down refuse with a pointer to the collapse; after the collapse Down passes and the sums are
    // unchanged. The Name column is put on an ICU collation so the collapse's COLLATE "C" is what decides
    // 'Bravo' before 'alpha' (the bytewise Alpine default would hide a missing clause).
    [Fact]
    public async Task Down_RefusesWhileACellHasRowsOfTwoSlots_AndPassesAfterTheCollapse_WithUnchangedSums()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.BeforeM1);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(db, EmoteSlotMigrationFixtures.CollapseBaseSql);
        await MigrateAsync(db, ids.M2);
        await Exec(db, EmoteSlotMigrationFixtures.LinguisticNameCollationSql);
        var seed = await OneAsync(db, """SELECT "SlotId" AS "Value" FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "Date" = '2026-09-01';""");
        await Exec(db, EmoteSlotMigrationFixtures.CollapseExtraSlotsSql);
        var sumsBefore = await OneAsync(db, PairTotalsSql);

        var failure = await MigrateAndCaptureFailureAsync(db, ids.M1);

        Assert.Contains("Down refused: 3 (emote, emote set, date) cell(s) have more than one usage row", failure, StringComparison.Ordinal);
        Assert.Contains("CollapseUsageStatsSql", failure, StringComparison.Ordinal);
        Assert.Equal("1", await OneAsync(db, M2HistoryCountSql));
        Assert.Equal("NO", await OneAsync(db, SlotIdNullableSql));

        Assert.Equal(3, await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.CollapseUsageStatsSql));
        Assert.Equal("12|3|3|0|x-dated", await OneAsync(db, CellSql("2026-09-01")));
        Assert.Equal("6|1|1|0|x-null", await OneAsync(db, CellSql("2026-09-02")));
        Assert.Equal("30|3|6|1|x-upper", await OneAsync(db, CellSql("2026-09-03")));
        Assert.Equal($"8|0|0|0|{seed}", await OneAsync(db, CellSql("2026-09-04")));

        await MigrateAsync(db, ids.M1);

        Assert.Equal("0", await OneAsync(db, M2HistoryCountSql));
        Assert.Contains("UNIQUE", await IndexDefinitionAsync(db, EmoteIndex));
        Assert.Equal(sumsBefore, await OneAsync(db, PairTotalsSql));
        Assert.Equal("5", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "UsageStats";"""));
    }

    // ------------------------------------------------------------------ 8: the flag-off world

    // With the flag off at most one slot per pair carries rows (E6a), so M2's Down is lossless — including
    // the interleaving from the review (Sol P2-3): a pair whose only row is an import row of a run that
    // retention deleted; a block of a run of another set deletes it through its channel-wide delete; the
    // next map refresh hands out a pending key while a buffered count still carries the old slot; one
    // flush with both keys. Phase 1 writes the old key first, phase 2 finds it: one slot.
    [Fact]
    public async Task FlagOffWorld_FlushesWithPendingAndSlotKeys_AndTheReviewInterleaving_LeaveOneSlotPerPair_SoDownIsLossless()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.M2);
        await using var db = fixture.CreateDbContext(databaseName);
        var channel = await SeedChannelAsync(db, "m2_flag_off");
        var x = await SeedEmoteAsync(db, channel, "x", "X");
        var y = await SeedEmoteAsync(db, channel, "y", "Y");
        // y's only row: an import of a run that is gone (retention deleted run and snapshot).
        var importSeed = await SeedSeedAsync(db, y);
        db.UsageStats.Add(new UsageStat
        {
            EmoteId = y.Id,
            SlotId = importSeed.Id,
            EmoteSetId = SetA,
            Date = new DateOnly(2026, 8, 2),
            UseCount = 9,
            Source = UsageStatSource.ChatLogArchive
        });
        await db.SaveChangesAsync();
        var cache = new EmoteMatchCache();

        // (i) The flag-off pipeline: map → flush (pending key, creating x's seed) → map → flush (slot key).
        var map = await RefreshAsync(db, cache, channel, x, y);
        Assert.Equal(PendingSlotKey.For(x.Id), map["X"]);
        var oldYKey = map["Y"];
        Assert.Equal(importSeed.Id, oldYKey);
        await FlushAsync(db, (map["X"], 2));
        map = await RefreshAsync(db, cache, channel, x, y);
        var seedX = map["X"];
        Assert.False(PendingSlotKey.IsPending(seedX));
        await FlushAsync(db, (seedX, 3));

        // (ii) A running run of another set replaces the block holding y's import day.
        var run = await BackfillRunSeed.AddRunAsync(db, channel.Id, ChatLogBackfillRunStatus.Running);
        Assert.NotEqual(SetA, run.EmoteSetId);
        Assert.IsType<ChatLogBackfillBlockResult.Committed>(await CreateBackfillService(db).ReplaceBlockAsync(
            run.Id, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 8), [], 0, 0, isLastBlock: false));
        Assert.False(await db.UsageStats.AnyAsync(u => u.EmoteId == y.Id));
        map = await RefreshAsync(db, cache, channel, x, y);
        Assert.Equal(PendingSlotKey.For(y.Id), map["Y"]);

        // One batch with the buffered old key and the new pending key of the same pair.
        await FlushAsync(db, (oldYKey, 4), (map["Y"], 1));

        Assert.Equal([importSeed.Id], await db.UsageStats.Where(u => u.EmoteId == y.Id).Select(u => u.SlotId).Distinct().ToListAsync());
        Assert.Equal(5, await db.UsageStats.Where(u => u.EmoteId == y.Id).SumAsync(u => u.UseCount));
        Assert.Equal(1, await db.EmoteSlots.CountAsync(s => s.EmoteId == y.Id));
        Assert.Equal([seedX], await db.UsageStats.Where(u => u.EmoteId == x.Id).Select(u => u.SlotId).Distinct().ToListAsync());
        Assert.Equal(5, await db.UsageStats.Where(u => u.EmoteId == x.Id).SumAsync(u => u.UseCount));
        Assert.Equal(
            "0",
            await OneAsync(
                db,
                """
                SELECT count(*)::text AS "Value" FROM (
                    SELECT 1 FROM "UsageStats" GROUP BY "EmoteId", "EmoteSetId" HAVING count(DISTINCT "SlotId") > 1) p;
                """));

        var pairSums = await OneAsync(db, PairSumsSql);
        var assignment = await OneAsync(db, SlotAssignmentDigestSql);

        await MigrateAsync(db, ids.M1);

        Assert.Equal(pairSums, await OneAsync(db, PairSumsSql));
        Assert.Contains("UNIQUE", await IndexDefinitionAsync(db, EmoteIndex));

        await MigrateAsync(db, ids.M2);

        Assert.Equal(assignment, await OneAsync(db, SlotAssignmentDigestSql));
    }

    // ------------------------------------------------------------------ 9: volume

    // Up-Down-Up with thousands of pairs and with statistics that predate the old writer's NULL rows (the
    // state M2 meets on production) stays within EF's 30-second command timeout.
    [Fact]
    public async Task UpDownUp_WithThousandsOfPairsAndLateNullRows_StaysWithinTheCommandTimeout_AndKeepsPairSums()
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.BeforeM1);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(db, EmoteSlotMigrationFixtures.ThousandsOfPairsSql);
        await MigrateAsync(db, ids.M1);
        await Exec(db, """ANALYZE "UsageStats";""");
        await Exec(db, EmoteSlotMigrationFixtures.LateNullRowsForThousandsOfPairsSql);
        var pairSums = await OneAsync(db, PairSumsDigestSql);

        await MigrateAsync(db, ids.M2);
        await MigrateAsync(db, ids.M1);
        await MigrateAsync(db, ids.M2);

        Assert.Equal("0", await OneAsync(db, NullRowsSql));
        Assert.Equal("11000", await OneAsync(db, SlotCountSql));
        Assert.Equal(
            "11000",
            await OneAsync(db, """SELECT count(DISTINCT ("EmoteId", "EmoteSetId", "SlotId"))::text AS "Value" FROM "UsageStats";"""));
        Assert.Equal(pairSums, await OneAsync(db, PairSumsDigestSql));
        Assert.Equal("76000", await OneAsync(db, """SELECT count(*)::text AS "Value" FROM "UsageStats";"""));
    }

    // ------------------------------------------------------------------ fixtures

    // Channel c1 (active set A), emotes X (active), Y (archived), Z (no usage); X is used in sets A and
    // B, Y in A. One completed run on set B snapshots X and Y (Y's snapshot row stays NULL at M1: no
    // seed in B). Written before M1, without slots.
    private const string StandardSql =
        """
        INSERT INTO "Channels" ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
        VALUES ('c1', '1', 'chan', 'SETA', true, now(), NULL);
        INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "ArchivedAt", "LastSyncedAt")
        VALUES ('ex', '7x', 'c1', 'EmoteX', '', false, NULL,         now()),
               ('ey', '7y', 'c1', 'EmoteY', '', true,  '2026-09-01', now()),
               ('ez', '7z', 'c1', 'EmoteZ', '', false, NULL,         now());
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
        """;

    // On top of StandardSql after M1, as an image from before #373 writes: an existing pair's new day,
    // a new set of an existing emote, an emote that never had a slot (two days).
    private const string OldWriterRowsSql =
        """
        INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
        VALUES ('ex', 'SETA', '2026-09-03', 1, 0, 0),
               ('ey', 'SETB', '2026-09-03', 2, 0, 0),
               ('ez', 'SETA', '2026-09-03', 4, 0, 0),
               ('ez', 'SETA', '2026-09-04', 4, 0, 0);
        """;

    // StandardSql → M1 → the crafted violation → M2, which must fail and leave nothing applied.
    private async Task<string> RunM2OnCraftedM1StateAsync(string violationSql)
    {
        var (databaseName, ids) = await CreateDatabaseAsync(m => m.BeforeM1);
        await using var db = fixture.CreateDbContext(databaseName);
        await Exec(db, StandardSql);
        await MigrateAsync(db, ids.M1);
        await Exec(db, violationSql);
        var slotsBefore = await OneAsync(db, SlotCountSql);

        var failure = await MigrateAndCaptureFailureAsync(db, ids.M2);

        Assert.Equal("YES", await OneAsync(db, SlotIdNullableSql));
        Assert.Equal(slotsBefore, await OneAsync(db, SlotCountSql));
        Assert.Equal("0", await OneAsync(db, M2HistoryCountSql));
        Assert.Contains("Nothing was applied.", failure, StringComparison.Ordinal);
        return failure;
    }

    // One flag-off refresh of the match map through the real sync (the 7TV client answers x and y), and
    // its result.
    private static async Task<IReadOnlyDictionary<string, string>> RefreshAsync(
        AppDbContext db, EmoteMatchCache cache, Channel channel, Emote x, Emote y)
    {
        var service = CreateService(db, ApiReturning(channel, SetA, Live(x.SevenTvEmoteId, x.Name, null), Live(y.SevenTvEmoteId, y.Name, null)), slotsEnabled: false, cache);
        Assert.NotNull(await service.SyncChannelAsync(channel.ChannelName));
        return cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId;
    }

    private static async Task FlushAsync(AppDbContext db, params (string SlotKey, int Uses)[] counts)
    {
        var result = await new UsageStatFlushService(db, NullLogger<UsageStatFlushService>.Instance).FlushAsync(
            counts.ToDictionary(c => new UsageCounterKey(c.SlotKey, SetA), c => new EmoteUsageCounts(c.Uses, 0, 0)));
        Assert.Empty(result.Requeued);
    }

    private static ChatLogBackfillService CreateBackfillService(AppDbContext db) =>
        new(
            db,
            Substitute.For<IRedisPublisher>(),
            Substitute.For<IExcludedChannelFilter>(),
            Substitute.For<ITrackedEmoteSetMembershipService>(),
            Substitute.For<IForeignEmoteSetService>(),
            new ChatLogBackfillOptions(),
            new ChatLogArchiveOptions { BaseUrl = "https://logs.cyex.app/" },
            TimeProvider.System,
            NullLogger<ChatLogBackfillService>.Instance);

    private static string CellSql(string date) =>
        $"""
        SELECT "UseCount" || '|' || "BotUseCount" || '|' || "SharedChatUseCount" || '|' || "Source" || '|' || "SlotId" AS "Value"
        FROM "UsageStats" WHERE "EmoteId" = 'ex' AND "EmoteSetId" = 'SETA' AND "Date" = '{date}';
        """;

    private Task<(string DatabaseName, MigrationIds Ids)> CreateDatabaseAsync(string migrationName) =>
        CreateDatabaseAsync(m => migrationName == M1Name ? m.M1 : m.M2);

    // A new database on the shared container, migrated to the migration the selector picks.
    private async Task<(string DatabaseName, MigrationIds Ids)> CreateDatabaseAsync(Func<MigrationIds, string> target)
    {
        var databaseName = $"migration_m2_{Guid.NewGuid():N}";
        await using (var admin = fixture.CreateDbContext())
        {
#pragma warning disable EF1002 // locally generated Guid, CREATE DATABASE cannot be parameterized
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {databaseName}");
#pragma warning restore EF1002
        }

        await using var db = fixture.CreateDbContext(databaseName);
        var chain = db.GetService<IMigrationsAssembly>().Migrations.Keys.Order(StringComparer.Ordinal).ToList();
        var m1 = chain.FindIndex(id => id.EndsWith("_" + M1Name, StringComparison.Ordinal));
        var m2 = chain.FindIndex(id => id.EndsWith("_" + M2Name, StringComparison.Ordinal));
        Assert.True(m1 > 0 && m2 > m1, "SwitchUsageStatsToSlotKey must follow AddEmoteSlots in the migration chain.");
        var ids = new MigrationIds(chain[m1 - 1], chain[m1], chain[m2]);
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

    private sealed record MigrationIds(string BeforeM1, string M1, string M2);
}
