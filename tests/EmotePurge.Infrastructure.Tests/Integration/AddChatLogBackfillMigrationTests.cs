using System.Text;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// AddChatLogBackfill (#346, child 2): the additive migration with a refusing Down (D40). Each case
// gets its own database on the shared container, migrated to the migration *before* this one, so the
// "seeded with live rows" cases write against the old schema. The predecessor is looked up from the
// migration chain instead of being hard-coded, so the tests keep working when this migration is
// re-scaffolded behind another one.
[Collection("Postgres")]
public class AddChatLogBackfillMigrationTests(PostgresFixture fixture)
{
    private const string MigrationName = "AddChatLogBackfill";
    private const string SetId = "01BACKFILLTESTSET000000000";

    [Fact]
    public async Task Up_AndDown_RunOnAnEmptyDatabase_InBothDirections()
    {
        var (databaseName, previous, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);

        await MigrateAsync(db, current);
        Assert.True(await ColumnExistsAsync(db, "UsageStats", "Source"));
        Assert.True(await TableExistsAsync(db, "ChatLogBackfillRuns"));
        Assert.True(await TableExistsAsync(db, "ChatLogBackfillRunEmotes"));
        Assert.True(await TableExistsAsync(db, "ChatLogBackfillCoverage"));
        Assert.True(await TableExistsAsync(db, "ChatLogBackfillProviderState"));

        await MigrateAsync(db, previous);
        Assert.False(await ColumnExistsAsync(db, "UsageStats", "Source"));
        Assert.False(await TableExistsAsync(db, "ChatLogBackfillRuns"));
        Assert.False(await TableExistsAsync(db, "ChatLogBackfillRunEmotes"));
        Assert.False(await TableExistsAsync(db, "ChatLogBackfillCoverage"));
        Assert.False(await TableExistsAsync(db, "ChatLogBackfillProviderState"));

        await MigrateAsync(db, current);
        Assert.True(await TableExistsAsync(db, "ChatLogBackfillProviderState"));
    }

    [Fact]
    public async Task Down_Succeeds_OverLiveRows_AndKeepsThem()
    {
        // AC 1, backward direction on a database with data: live rows (Source = 0) do not block Down,
        // survive it, and the column is gone afterwards.
        var (databaseName, previous, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "down-live-channel", "downlivechannel", "5550010");
        await InsertEmoteAsync(db, "down-live-emote", "down-live-channel");
        await MigrateAsync(db, current);
        foreach (var day in new[] { 1, 2, 3 })
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
                VALUES ('down-live-emote', {0}, {1}, 4, 0, 0);
                """,
                SetId,
                new DateOnly(2026, 9, day));
        }

        Assert.Equal(["3"], await QueryAsync(db, """SELECT count(*)::text AS "Value" FROM "UsageStats" WHERE "Source" = 0;"""));

        await MigrateAsync(db, previous);

        Assert.False(await ColumnExistsAsync(db, "UsageStats", "Source"));
        Assert.Equal(["3"], await QueryAsync(db, """SELECT count(*)::text AS "Value" FROM "UsageStats";"""));
    }

    [Fact]
    public async Task Up_GivesEveryExistingUsageRowTheLiveSource_AndSeedsExactlyOneProviderStateRow()
    {
        var (databaseName, _, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "live-channel", "livechannel", "5550001");
        await InsertEmoteAsync(db, "live-emote", "live-channel");
        foreach (var day in new[] { 1, 2, 3 })
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
                VALUES ({0}, {1}, {2}, 4, 0, 0);
                """,
                "live-emote",
                SetId,
                new DateOnly(2026, 9, day));
        }

        await MigrateAsync(db, current);

        Assert.Equal(["3"], await QueryAsync(db, """SELECT count(*)::text AS "Value" FROM "UsageStats" WHERE "Source" = 0;"""));
        Assert.Equal(["0"], await QueryAsync(db, """SELECT count(*)::text AS "Value" FROM "UsageStats" WHERE "Source" <> 0;"""));
        Assert.Equal(["1"], await QueryAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillProviderState";"""));
        Assert.Equal(["1"], await QueryAsync(db, """SELECT "Id"::text AS "Value" FROM "ChatLogBackfillProviderState";"""));
        Assert.Equal(["0"], await QueryAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillProviderState" WHERE "CooldownUntilUtc" IS NOT NULL;"""));
    }

    [Fact]
    public async Task Down_Raises_WhileAnImportedUsageRowExists_AndLeavesTheSchemaInPlace()
    {
        var (databaseName, previous, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "imp-channel", "impchannel", "5550002");
        await InsertEmoteAsync(db, "imp-emote", "imp-channel");
        await MigrateAsync(db, current);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source")
            VALUES ('imp-emote', {0}, {1}, 9, 0, 0, 1);
            """,
            SetId,
            new DateOnly(2026, 8, 1));

        var message = await MigrateAndCaptureFailureAsync(db, previous);

        Assert.Contains("Refusing to revert AddChatLogBackfill", message);
        Assert.Contains("1 imported usage row(s)", message);
        Assert.Contains("Rollback Plan", message);
        await AssertSchemaInPlaceAsync(db);
        Assert.Equal(["1"], await QueryAsync(db, """SELECT count(*)::text AS "Value" FROM "UsageStats" WHERE "Source" = 1;"""));
    }

    [Fact]
    public async Task Down_Raises_WhileACoverageRowExists_EvenWithoutImportedUsage()
    {
        var (databaseName, previous, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "cov-channel", "covchannel", "5550003");
        await MigrateAsync(db, current);
        await InsertCoverageDayAsync(db, "cov-channel", new DateOnly(2026, 8, 1));

        var message = await MigrateAndCaptureFailureAsync(db, previous);

        Assert.Contains("Refusing to revert AddChatLogBackfill", message);
        Assert.Contains("1 coverage day(s)", message);
        await AssertSchemaInPlaceAsync(db);
    }

    [Fact]
    public async Task Down_Succeeds_AfterTheDocumentedCleanup_AndDropsTheProviderState()
    {
        var (databaseName, previous, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "clean-channel", "cleanchannel", "5550004");
        await InsertEmoteAsync(db, "clean-emote", "clean-channel");
        await MigrateAsync(db, current);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source")
            VALUES ('clean-emote', {0}, {1}, 9, 0, 0, 1);
            """,
            SetId,
            new DateOnly(2026, 8, 1));
        await InsertCoverageDayAsync(db, "clean-channel", new DateOnly(2026, 8, 1));
        await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(db, previous));

        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "UsageStats" WHERE "Source" = 1;""");
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "ChatLogBackfillCoverage";""");
        await MigrateAsync(db, previous);

        Assert.False(await TableExistsAsync(db, "ChatLogBackfillProviderState"));
        Assert.False(await ColumnExistsAsync(db, "UsageStats", "Source"));
    }

    [Fact]
    public async Task ActiveRunIndex_RejectsASecondActiveRunOfOneChannel_ButNotATerminalOne()
    {
        var (databaseName, _, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "idx-channel", "idxchannel", "5550005");
        await InsertChannelAsync(db, "other-channel", "otherchannel", "5550006");
        await MigrateAsync(db, current);

        await InsertRunAsync(db, "idx-channel", "running");
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertRunAsync(db, "idx-channel", "queued"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("IX_ChatLogBackfillRuns_ChannelId_Active", duplicate.ConstraintName);

        // Another channel, and a terminal run next to the active one, are both fine.
        await InsertRunAsync(db, "other-channel", "queued");
        await InsertRunAsync(db, "idx-channel", "completed");
        await InsertRunAsync(db, "idx-channel", "failed");
        await InsertRunAsync(db, "idx-channel", "cancelled");
    }

    [Theory]
    [InlineData("Status", "'exploding'")]
    [InlineData("RequestedMonths", "2")]
    [InlineData("WindowTo", "'2026-01-01'")] // equal to WindowFrom: the window must be non-empty
    public async Task RunChecks_RejectValuesOutsideTheContract(string column, string value)
    {
        var (databaseName, _, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "chk-channel", "chkchannel", "5550007");
        await MigrateAsync(db, current);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertRunAsync(db, "chk-channel", "queued", overrideColumn: column, overrideValue: value));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task ProviderState_RejectsASecondRow()
    {
        var (databaseName, _, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await MigrateAsync(db, current);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            """INSERT INTO "ChatLogBackfillProviderState" ("Id", "UpdatedAtUtc") VALUES (2, now());"""));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Cascades_FollowTheSpec_RunEmotesWithTheRun_CoverageSurvivesTheRunButNotTheChannel()
    {
        var (databaseName, _, current) = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await InsertChannelAsync(db, "casc-channel", "cascchannel", "5550008");
        await MigrateAsync(db, current);
        var runId = await InsertRunAsync(db, "casc-channel", "completed");
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow")
            VALUES ({0}, 'no-such-emote-row', '7tv-x', 'Alias', false);
            """,
            runId);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "ChatLogBackfillCoverage" ("ChannelId", "Day", "EmoteSetId", "ArchiveHost", "RunId", "CompletedAtUtc")
            VALUES ('casc-channel', {0}, {1}, 'logs.cyex.app', {2}, now());
            """,
            new DateOnly(2026, 8, 1),
            SetId,
            runId);

        // No FK from the snapshot to Emotes (D25): the insert above already proved it. Deleting the
        // run removes its snapshot and nulls the coverage's RunId, the coverage day stays.
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "ChatLogBackfillRuns" WHERE "Id" = {0};""", runId);
        Assert.Equal(["0"], await QueryAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillRunEmotes";"""));
        Assert.Equal(["1"], await QueryAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillCoverage" WHERE "RunId" IS NULL;"""));

        // The channel takes its coverage (and runs) with it.
        await InsertRunAsync(db, "casc-channel", "queued");
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Channels" WHERE "Id" = 'casc-channel';""");
        Assert.Equal(["0"], await QueryAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillCoverage";"""));
        Assert.Equal(["0"], await QueryAsync(db, """SELECT count(*)::text AS "Value" FROM "ChatLogBackfillRuns";"""));
    }

    private static async Task<long> InsertRunAsync(
        AppDbContext db, string channelId, string status, string? overrideColumn = null, string? overrideValue = null)
    {
        var from = "'2026-01-01'";
        var to = overrideColumn == "WindowTo" ? overrideValue : "'2026-02-01'";
        var months = overrideColumn == "RequestedMonths" ? overrideValue : "1";
        var statusSql = overrideColumn == "Status" ? overrideValue : $"'{status}'";

#pragma warning disable EF1002 // all interpolated parts are test-controlled literals
        var ids = await db.Database.SqlQueryRaw<long>(
            $$"""
            INSERT INTO "ChatLogBackfillRuns"
                ("ChannelId", "Status", "RequestedMonths", "WindowFrom", "WindowTo", "WeeksTotal", "EmoteSetId", "ArchiveBaseUrl",
                 "RequestedByTwitchUserId", "RequestedByLogin", "RequestedAtUtc")
            VALUES ({0}, {{statusSql}}, {{months}}, {{from}}, {{to}}, 5, '{{SetId}}', 'https://logs.cyex.app/', '42', 'someone', now())
            RETURNING "Id" AS "Value";
            """,
            channelId).ToListAsync();
#pragma warning restore EF1002

        return ids.Single();
    }

    private static async Task InsertCoverageDayAsync(AppDbContext db, string channelId, DateOnly day) =>
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "ChatLogBackfillCoverage" ("ChannelId", "Day", "EmoteSetId", "ArchiveHost", "CompletedAtUtc")
            VALUES ({0}, {1}, {2}, 'logs.cyex.app', now());
            """,
            channelId,
            day,
            SetId);

    private static async Task AssertSchemaInPlaceAsync(AppDbContext db)
    {
        Assert.True(await ColumnExistsAsync(db, "UsageStats", "Source"));
        Assert.True(await TableExistsAsync(db, "ChatLogBackfillRuns"));
        Assert.True(await TableExistsAsync(db, "ChatLogBackfillRunEmotes"));
        Assert.True(await TableExistsAsync(db, "ChatLogBackfillCoverage"));
        Assert.True(await TableExistsAsync(db, "ChatLogBackfillProviderState"));
    }

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
        Assert.True(index > 0, "AddChatLogBackfill must have a predecessor in the migration chain.");
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

    private static async Task<List<string?>> QueryAsync(AppDbContext db, string sql, params object[] parameters) =>
        await db.Database.SqlQueryRaw<string?>(sql, parameters).ToListAsync();

    private static async Task InsertChannelAsync(AppDbContext db, string id, string channelName, string twitchChannelId) =>
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Channels" ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
            VALUES ({0}, {1}, {2}, {3}, true, {4}, NULL);
            """,
            id,
            twitchChannelId,
            channelName,
            SetId,
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

    private static async Task InsertEmoteAsync(AppDbContext db, string id, string channelId) =>
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "LastSyncedAt")
            VALUES ({0}, {1}, {2}, 'Emote', '', false, {3});
            """,
            id,
            $"7tv-{id}",
            channelId,
            DateTime.UtcNow);
}
