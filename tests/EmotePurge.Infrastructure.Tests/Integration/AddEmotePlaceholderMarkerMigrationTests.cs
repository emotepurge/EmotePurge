using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Migrations;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The AddEmotePlaceholderMarker migration's backfill (chat-log backfill spec, child 1, AC 1) on rows
// that existed before it ran — the state PostgresFixture's shared database never has, because it is
// migrated to head before any row is inserted. Same pattern as RetentionTimestampBackfillMigrationTests:
// a second database on the same container, migrated to the migration before this one, seeded with raw
// SQL against that schema, then migrated forward (and back).
[Collection("Postgres")]
public class AddEmotePlaceholderMarkerMigrationTests(PostgresFixture fixture)
{
    private const string PreviousMigration = "20261008171918_AddBroadcasterChannelLocks";
    private const string SetId = "01J94NYQR0000D15QN0MIGRAT1";

    // Inserted the way an image older than the marker inserts: the column is not named, so only the
    // database default can fill it.
    private const string InsertPreMarkerRowSql = """
        INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "ArchivedAt", "FirstSeenAt", "LastSyncedAt", "LastEnteredSetAtUtc")
        VALUES (@id, @id, @channelId, @id, '', @archived, @archivedAt, NULL, now(), @enteredAt);
        """;

    [Fact]
    public async Task Up_MarksOnlyTheBallotShapedRow_AndDownDropsTheColumn()
    {
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        var channelId = await InsertChannelAsync(db, "migrationshapes");
        var now = DateTime.UtcNow;

        // (a) the vote-session insert, (b) a sync archive, (c) archived before ArchivedAt and
        // LastEnteredSetAtUtc existed, (d) active.
        await InsertPreMarkerRowAsync(db, channelId, "ballot", archived: true, archivedAt: null, enteredAt: now.AddHours(-1));
        await InsertPreMarkerRowAsync(db, channelId, "syncarchived", archived: true, archivedAt: now.AddHours(-2), enteredAt: now.AddDays(-3));
        await InsertPreMarkerRowAsync(db, channelId, "legacyarchived", archived: true, archivedAt: null, enteredAt: null);
        await InsertPreMarkerRowAsync(db, channelId, "active", archived: false, archivedAt: null, enteredAt: now.AddDays(-3));

        await MigrateAsync(db, null);

        Assert.Equal(new[] { "ballot" }, await MarkedIdsAsync(db, channelId));

        await MigrateAsync(db, PreviousMigration);

        Assert.False(await ColumnExistsAsync(db, "Emotes", "IsPlaceholder"));
        Assert.Equal(4, await db.Database.SqlQueryRaw<int>(
            """SELECT count(*)::int AS "Value" FROM "Emotes" WHERE "ChannelId" = {0}""", channelId).SingleAsync());
    }

    // The operator's re-run after the production redeploy (docs/Operations.md): a ballot row the old
    // image created after the migration is marked, a row the new sync has un-archived and archived
    // again is not re-marked, and a second run changes nothing.
    [Fact]
    public async Task BackfillSql_ReRunAfterTheMigration_MarksLateBallotRowsOnly_AndIsIdempotent()
    {
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using var db = fixture.CreateDbContext(databaseName);
        var channelId = await InsertChannelAsync(db, "migrationrerun");
        await InsertPreMarkerRowAsync(db, channelId, "ballot", archived: true, archivedAt: null, enteredAt: DateTime.UtcNow.AddHours(-1));
        await MigrateAsync(db, null);

        // The new sync un-archived the marked row (clearing the marker) and archived it again later.
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE "Emotes" SET "IsPlaceholder" = false, "IsArchived" = true, "ArchivedAt" = now() WHERE "Id" = 'ballot';""");
        // The old image, still running, created a ballot row after the migration.
        await InsertPreMarkerRowAsync(db, channelId, "lateballot", archived: true, archivedAt: null, enteredAt: DateTime.UtcNow);

        Assert.Equal(1, await db.Database.ExecuteSqlRawAsync(AddEmotePlaceholderMarker.BackfillSql));
        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(AddEmotePlaceholderMarker.BackfillSql));
        Assert.Equal(new[] { "lateballot" }, await MarkedIdsAsync(db, channelId));
    }

    // EPIC AC 27, last clause: the vote-session precedent's rows pass the leave-detection test after
    // the migration backfill. A ballot row created by the pre-marker insert, 31 minutes old, gets no
    // leave observation from a REST resync of the active set; the sync-archived row next to it does.
    [Fact]
    public async Task AfterTheBackfill_AResyncRecordsNoLeaveForTheBallotRow_ButStillForTheSyncArchivedRow()
    {
        var databaseName = await CreateDatabaseAtPreviousMigrationAsync();
        await using (var seed = fixture.CreateDbContext(databaseName))
        {
            var channelId = await InsertChannelAsync(seed, "migrationresync");
            await InsertPreMarkerRowAsync(seed, channelId, "keep", archived: false, archivedAt: null, enteredAt: null);
            await InsertPreMarkerRowAsync(seed, channelId, "ballot", archived: true, archivedAt: null, enteredAt: DateTime.UtcNow.AddMinutes(-31));
            await InsertPreMarkerRowAsync(seed, channelId, "syncarchived", archived: true, archivedAt: DateTime.UtcNow.AddDays(-1), enteredAt: null);
            await MigrateAsync(seed, null);
        }

        await using (var db = fixture.CreateDbContext(databaseName))
        {
            var apiClient = Substitute.For<ISevenTvApiClient>();
            apiClient.GetChannelStateForTwitchUserAsync("tw_migrationresync", Arg.Any<CancellationToken>())
                .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState(
                    "7tv-user", new SevenTvEmoteSet(SetId, [new SevenTvEmote("keep", "keep", "")]))));
            var service = new SevenTvSyncService(db, apiClient, new EmoteMatchCache(), new DuplicateEmoteNameTracker(),
                new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(),
                new BroadcasterChannelLockService(db), new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System),
                new EmptySetConfirmationTracker(new EmptySetConfirmationOptions(), TimeProvider.System),
                NullLogger<SevenTvSyncService>.Instance);
            Assert.NotNull(await service.SyncChannelAsync("migrationresync"));
        }

        await using var verify = fixture.CreateDbContext(databaseName);
        var observed = await verify.EmoteSetLeaveObservations.AsNoTracking()
            .Select(o => o.SevenTvEmoteId).ToListAsync();
        Assert.Equal(new[] { "syncarchived" }, observed);
    }

    private async Task<string> CreateDatabaseAtPreviousMigrationAsync()
    {
        var databaseName = $"placeholder_marker_{Guid.NewGuid():N}";
        await using (var admin = fixture.CreateDbContext())
        {
            // CREATE DATABASE cannot run inside a transaction or take a parameter; the name is a
            // locally generated Guid, so EF1002 (injection) does not apply.
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
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}", ActiveEmoteSetId = SetId, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return channel.Id;
    }

    private static Task InsertPreMarkerRowAsync(
        AppDbContext db, string channelId, string id, bool archived, DateTime? archivedAt, DateTime? enteredAt) =>
        db.Database.ExecuteSqlRawAsync(
            InsertPreMarkerRowSql,
            [
                new NpgsqlParameter("id", NpgsqlDbType.Text) { Value = id },
                new NpgsqlParameter("channelId", NpgsqlDbType.Text) { Value = channelId },
                new NpgsqlParameter("archived", NpgsqlDbType.Boolean) { Value = archived },
                new NpgsqlParameter("archivedAt", NpgsqlDbType.TimestampTz) { Value = (object?)archivedAt ?? DBNull.Value },
                new NpgsqlParameter("enteredAt", NpgsqlDbType.TimestampTz) { Value = (object?)enteredAt ?? DBNull.Value },
            ]);

    private static Task<List<string>> MarkedIdsAsync(AppDbContext db, string channelId) =>
        db.Emotes.AsNoTracking()
            .Where(e => e.ChannelId == channelId && e.IsPlaceholder)
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .ToListAsync();

    private static async Task<bool> ColumnExistsAsync(AppDbContext db, string table, string column) =>
        await db.Database.SqlQueryRaw<string>(
            """
            SELECT column_name AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = {0} AND column_name = {1}
            """,
            table,
            column).AnyAsync();
}
