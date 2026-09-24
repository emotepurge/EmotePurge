using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Exercises the AddRetentionTimestamps migration's backfill UPDATEs directly, on rows that
// existed *before* the migration ran — the scenario ChannelServiceTests/ChannelIdentityServiceTests
// cannot reach, because PostgresFixture migrates its shared database to head before any row is
// ever inserted. Runs against a second, empty database on the same container (the
// PendingMigrationGuardTests pattern), migrated only up to the migration immediately before the
// one under test, so the pre-existing rows are inserted with raw SQL against the pre-migration
// schema — exactly what a real production database looked like before this migration shipped.
[Collection("Postgres")]
public class RetentionTimestampBackfillMigrationTests(PostgresFixture fixture)
{
    private const string PriorMigration = "20260907080507_AddUsageStatSharedChatUseCount";

    [Fact]
    public async Task AddRetentionTimestamps_BackfillsExistingUsersAndInactiveChannels_ButLeavesActiveChannelsNull()
    {
        var databaseName = $"retention_backfill_{Guid.NewGuid():N}";
        await using (var admin = fixture.CreateDbContext())
        {
            // CREATE DATABASE cannot run inside a transaction; ExecuteSqlRawAsync sends it as a
            // single non-transactional command. EF1002 (injection) does not apply — the name is a
            // locally generated Guid, and CREATE DATABASE cannot be parameterized anyway.
#pragma warning disable EF1002
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {databaseName}");
#pragma warning restore EF1002
        }

        DateTime migratedAt;
        await using (var db = fixture.CreateDbContext(databaseName))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PriorMigration);

            // Pre-existing rows the migration under test must backfill: a user row and both an
            // inactive and an active channel row. Raw SQL because the schema at this point does not
            // yet have LastSeenAtUtc/DeactivatedAtUtc — these are exactly the columns being added.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Users" ("Id", "TwitchUsername", "DisplayName", "LastLogin")
                VALUES ('retention-backfill-user', 'retentionbackfilluser', 'RetentionBackfillUser', now() - interval '90 days');
                """);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Channels" ("Id", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt")
                VALUES ('retention-backfill-inactive', 'retentionbackfillinactive', '', false, now() - interval '90 days');
                """);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Channels" ("Id", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt")
                VALUES ('retention-backfill-active', 'retentionbackfillactive', '', true, now() - interval '90 days');
                """);

            migratedAt = DateTime.UtcNow;
            await migrator.MigrateAsync();
        }

        await using var verify = fixture.CreateDbContext(databaseName);
        var tolerance = TimeSpan.FromSeconds(30);

        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == "retention-backfill-user");
        Assert.NotNull(user.LastSeenAtUtc);
        Assert.InRange(user.LastSeenAtUtc!.Value, migratedAt - tolerance, DateTime.UtcNow + tolerance);

        var inactiveChannel = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == "retention-backfill-inactive");
        Assert.NotNull(inactiveChannel.DeactivatedAtUtc);
        Assert.InRange(inactiveChannel.DeactivatedAtUtc!.Value, migratedAt - tolerance, DateTime.UtcNow + tolerance);

        // The active channel's row is untouched by the backfill — DeactivatedAtUtc stays an honest
        // null, exactly as it would for a channel LeaveAsync has never deactivated.
        var activeChannel = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == "retention-backfill-active");
        Assert.Null(activeChannel.DeactivatedAtUtc);
    }
}
