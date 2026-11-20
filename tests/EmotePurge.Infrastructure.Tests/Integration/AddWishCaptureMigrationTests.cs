using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// AddWishCapture (wish emotes, child 6, T1) on rows that existed before it ran, the state the shared head-schema
// database never has. A second database on the same container is migrated to AddWishes, seeded the way the K1 to
// K3 image writes (raw SQL against that schema, the new columns unnamed), migrated forward, written again the way
// the old image keeps writing during the window between the manual migration and the redeploy, and migrated back.
[Collection("Postgres")]
public class AddWishCaptureMigrationTests(PostgresFixture fixture)
{
    private const string PreviousMigration = "20261117222139_AddWishes";

    // The K1 column set of a wish; an image older than K6 names exactly these.
    private const string InsertOldWishSql = """
        INSERT INTO "Wishes" ("ChannelId", "SevenTvEmoteId", "EmoteName", "Alias", "Source", "Status", "StatusChangedAtUtc",
            "DurationDays", "CreatedAtUtc", "StartsAtAuthoritative", "ExtensionCount", "Permanent", "BotOwned", "BotAttemptCount",
            "DecisionVersion", "EndedAtUtc")
        VALUES ({0}, {1}, 'Emote', 'Alias', 'manual', {2}, now(), 30, now(), false, 0, false, false, 0, 0, {3});
        """;

    private const string InsertOldSettingsSql = """
        INSERT INTO "ChannelWishSettings" ("ChannelId", "DefaultDurationDays", "ProbationLeadDays", "BotModeEnabled")
        VALUES ({0}, 21, 7, false);
        """;

    [Fact]
    public async Task Up_KeepsEveryRowOfTheOldImage_AndTheOldImageKeepsWritingAfterIt()
    {
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        var channelId = await InsertChannelAsync(db, "wishcapturebefore");
        foreach (var status in new[] { WishStatus.Approved, WishStatus.Active, WishStatus.Presented, WishStatus.Kept, WishStatus.Removed, WishStatus.Merged })
        {
            await InsertOldWishAsync(db, channelId, status);
        }

        await db.Database.ExecuteSqlRawAsync(InsertOldSettingsSql, channelId);

        // The CHECK validates the existing rows: every K1 row has all three emote fields.
        await MigrateAsync(db, null);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var wishes = await db.Wishes.AsNoTracking().Where(w => w.ChannelId == channelId).ToListAsync();
        Assert.Equal(6, wishes.Count);
        Assert.All(wishes, w => Assert.Equal((true, null, null, null, null, null), (w.HasEmote, w.RewardId, w.IrcMessageId, w.RequestText, w.RequestedName, w.MatchKind)));
        var settings = await db.ChannelWishSettings.AsNoTracking().SingleAsync(s => s.ChannelId == channelId);
        Assert.Equal((21, 14), (settings.DefaultDurationDays, settings.PendingExpiryDays));

        // The old image, still running after the manual migration, inserts without naming any K6 column.
        var later = await InsertChannelAsync(db, "wishcaptureafter");
        await InsertOldWishAsync(db, later, WishStatus.Approved);
        await db.Database.ExecuteSqlRawAsync(InsertOldSettingsSql, later);
        Assert.Equal(14, (await db.ChannelWishSettings.AsNoTracking().SingleAsync(s => s.ChannelId == later)).PendingExpiryDays);
        Assert.True(await db.Wishes.AnyAsync(w => w.ChannelId == later && w.Status == WishStatus.Approved && w.MatchKind == null));
    }

    // The way back drops the five tables and six columns and leaves the K1 rows readable; up again restores them.
    [Fact]
    public async Task Down_RemovesTheCaptureSchema_AndUpAppliesItAgain()
    {
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        await MigrateAsync(db, null);
        var channelId = await InsertChannelAsync(db, "wishcapturedown");
        db.ChannelWishRewards.Add(new ChannelWishReward { ChannelId = channelId, RewardId = "reward", Label = "Wish", CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await MigrateAsync(db, PreviousMigration);

        Assert.Empty(await QueryAsync(db, """
            SELECT tablename AS "Value" FROM pg_tables WHERE schemaname = 'public'
              AND tablename IN ('ChannelWishRewards', 'WishRewardLearnings', 'WishCaptureGaps', 'WishCaptureState', 'WishSlotExclusions', 'WishCaptureReceipts')
            """));
        Assert.Empty(await QueryAsync(db, """
            SELECT column_name AS "Value" FROM information_schema.columns
            WHERE (table_name = 'Wishes' AND column_name IN ('RewardId', 'IrcMessageId', 'RequestText', 'RequestedName', 'MatchKind'))
               OR (table_name = 'ChannelWishSettings' AND column_name = 'PendingExpiryDays')
            """));
        Assert.Empty(await QueryAsync(db, """SELECT conname AS "Value" FROM pg_constraint WHERE conname LIKE 'CK_Wishes_%' AND conname <> 'CK_Wishes_DurationDays'"""));
        await InsertOldWishAsync(db, channelId, WishStatus.Approved);

        await MigrateAsync(db, null);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private async Task InsertOldWishAsync(AppDbContext db, string channelId, string status) =>
        await db.Database.ExecuteSqlRawAsync(
            InsertOldWishSql,
            channelId,
            Guid.NewGuid().ToString("N")[..24],
            status,
            new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = WishStatus.IsEnded(status) ? DateTime.UtcNow : DBNull.Value });

    private async Task<string> CreateDatabaseAtPreviousMigrationAsync()
    {
        var databaseName = $"wish_capture_{Guid.NewGuid():N}";
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

    // The Channels table is the same before and after this migration, so the model can write it.
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
