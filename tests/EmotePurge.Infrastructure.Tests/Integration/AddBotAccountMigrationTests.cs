using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// AddBotAccount (#487, T1) on rows that existed before it ran. A second database on the same container is migrated
// to AddWishCapture, seeded the way the K6 image writes (raw SQL, the new columns unnamed), migrated forward and
// written again the way that image keeps writing between the manual migration and the redeploy. The migration is
// additive: every column that existed before keeps its type, nullability and default.
[Collection("Postgres")]
public class AddBotAccountMigrationTests(PostgresFixture fixture)
{
    private const string PreviousMigration = "20261119153549_AddWishCapture";

    private const string InsertOldWishSql = """
        INSERT INTO "Wishes" ("ChannelId", "SevenTvEmoteId", "EmoteName", "Alias", "Source", "Status", "StatusChangedAtUtc",
            "DurationDays", "CreatedAtUtc", "StartsAtAuthoritative", "ExtensionCount", "Permanent", "BotOwned", "BotAttemptCount",
            "DecisionVersion")
        VALUES ({0}, {1}, 'Emote', 'Alias', 'manual', 'approved', now(), 30, now(), false, 0, false, false, 0, 0);
        """;

    private const string InsertOldSettingsSql = """
        INSERT INTO "ChannelWishSettings" ("ChannelId", "DefaultDurationDays", "ProbationLeadDays", "BotModeEnabled")
        VALUES ({0}, 21, 7, false);
        """;

    private const string ColumnShapeSql = """
        SELECT table_name || '.' || column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default, '') AS "Value"
        FROM information_schema.columns WHERE table_schema = 'public' AND table_name <> 'BotAccountCredentials'
        ORDER BY 1
        """;

    [Fact]
    public async Task Up_ChangesNoExistingColumn_AndOnlyAddsTheNewOnes()
    {
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        var before = await QueryAsync(db, ColumnShapeSql);

        await MigrateAsync(db, null);

        var after = await QueryAsync(db, ColumnShapeSql);
        Assert.Empty(before.Except(after));
        string[] added =
        [
            "ChannelWishSettings.BotEditorCheckedIdentityVersion:integer:YES:",
            "Wishes.BotAddAckAtUtc:timestamp with time zone:YES:",
            "Wishes.BotAttemptVersion:integer:YES:",
            "Wishes.BotConfirmDeadlineAtUtc:timestamp with time zone:YES:",
            "Wishes.BotRemoveAtUtc:timestamp with time zone:YES:",
            "Wishes.BotRemoveEvidence:jsonb:YES:",
            "Wishes.BotRepairAtUtc:timestamp with time zone:YES:",
            "Wishes.BotSetId:character varying:YES:",
        ];
        Assert.Equal(added.Order(), after.Except(before).Order());
    }

    [Fact]
    public async Task Up_KeepsEveryRowOfTheOldImage_AndTheOldImageKeepsWritingAfterIt()
    {
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        var channelId = await InsertChannelAsync(db, "botaccountbefore");
        await db.Database.ExecuteSqlRawAsync(InsertOldWishSql, channelId, "emotebefore");
        await db.Database.ExecuteSqlRawAsync(InsertOldSettingsSql, channelId);

        await MigrateAsync(db, null);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var wish = await db.Wishes.AsNoTracking().SingleAsync(w => w.ChannelId == channelId);
        Assert.Equal(
            (null, null, null, null, null, null, null),
            (wish.BotSetId, wish.BotAttemptVersion, wish.BotRemoveAtUtc, wish.BotRemoveEvidence, wish.BotAddAckAtUtc, wish.BotConfirmDeadlineAtUtc, wish.BotRepairAtUtc));
        Assert.Null((await db.ChannelWishSettings.AsNoTracking().SingleAsync(s => s.ChannelId == channelId)).BotEditorCheckedIdentityVersion);

        // The old image, still running after the manual migration, inserts without naming any new column.
        var later = await InsertChannelAsync(db, "botaccountafter");
        await db.Database.ExecuteSqlRawAsync(InsertOldWishSql, later, "emoteafter");
        await db.Database.ExecuteSqlRawAsync(InsertOldSettingsSql, later);
        Assert.True(await db.Wishes.AnyAsync(w => w.ChannelId == later && w.BotSetId == null));
        Assert.True(await db.ChannelWishSettings.AnyAsync(s => s.ChannelId == later && s.BotEditorCheckedIdentityVersion == null));
    }

    [Fact]
    public async Task Up_CreatesTheCredentialTableEmpty_WithTheDefaultsOfAnUpsertThatNamesOnlyTheId()
    {
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await MigrateAsync(db, null);

        Assert.Empty(await db.BotAccountCredentials.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("""INSERT INTO "BotAccountCredentials" ("Id") VALUES (1)""");

        var row = await db.BotAccountCredentials.AsNoTracking().SingleAsync();
        Assert.Equal((BotAccountStatus.None, false, 0, 0L, 0, 0L, 0), (row.Status, row.KillSwitch, row.CredentialVersion, row.RunnerEpoch, row.IdentityVersion, row.KillSwitchVersion, row.RenewalFailureCount));
    }

    [Fact]
    public async Task Down_RemovesTheBotAccountSchema_AndUpAppliesItAgain()
    {
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await MigrateAsync(db, null);

        await MigrateAsync(db, PreviousMigration);

        Assert.Empty(await QueryAsync(db, """SELECT tablename AS "Value" FROM pg_tables WHERE schemaname = 'public' AND tablename = 'BotAccountCredentials'"""));
        Assert.Empty(await QueryAsync(db, """
            SELECT column_name AS "Value" FROM information_schema.columns
            WHERE (table_name = 'Wishes' AND column_name IN ('BotSetId', 'BotAttemptVersion', 'BotRemoveAtUtc', 'BotRemoveEvidence', 'BotAddAckAtUtc', 'BotConfirmDeadlineAtUtc', 'BotRepairAtUtc'))
               OR (table_name = 'ChannelWishSettings' AND column_name = 'BotEditorCheckedIdentityVersion')
            """));

        await MigrateAsync(db, null);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private async Task<string> CreateDatabaseAtPreviousMigrationAsync()
    {
        var databaseName = $"bot_account_{Guid.NewGuid():N}";
        await using (var admin = fixture.CreateDbContext())
        {
            // CREATE DATABASE cannot run inside a transaction or take a parameter; the name is a locally
            // generated Guid, so EF1002 (injection) does not apply.
#pragma warning disable EF1002
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {databaseName}");
#pragma warning restore EF1002
        }

        await using var db = fixture.CreateDbContext(databaseName);
        await MigrateAsync(db, PreviousMigration);
        return databaseName;
    }

    private static async Task MigrateAsync(AppDbContext db, string? targetMigration) =>
        await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(targetMigration);

    private static async Task<string> InsertChannelAsync(AppDbContext db, string name)
    {
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}", IsBotActive = false };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return channel.Id;
    }

    private static async Task<List<string>> QueryAsync(AppDbContext db, string sql) =>
        await db.Database.SqlQueryRaw<string>(sql).ToListAsync();
}
