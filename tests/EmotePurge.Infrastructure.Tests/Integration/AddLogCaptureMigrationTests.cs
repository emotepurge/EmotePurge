using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// AddLogCapture: three tables, their indexes and the one-time filter seed. Every case that depends on
// seed state (one deletes a seed row) gets its own database on the shared container.
[Collection("Postgres")]
public class AddLogCaptureMigrationTests(PostgresFixture fixture)
{
    private static readonly string[] SeedNames = ["Start nach Deploy", "Twitch-Reconnects", "7TV-Sync/EventAPI", "Alle Fehler"];

    [Fact]
    public async Task Migration_CreatesThreeTablesWithPlannedIndexes()
    {
        await using var db = await CreateMigratedDatabaseAsync();

        Assert.Equal(
            ["LogCaptureStatus", "LogEntries", "LogFilters"],
            await QueryAsync(db, """SELECT tablename AS "Value" FROM pg_tables WHERE schemaname = 'public' AND tablename LIKE 'Log%' ORDER BY 1;"""));

        var indexes = await QueryAsync(db, """SELECT indexdef AS "Value" FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('LogEntries', 'LogFilters') AND indexname NOT LIKE 'PK_%' ORDER BY indexname;""");
        Assert.Equal(5, indexes.Count);
        Assert.Contains(indexes, d => d!.Contains("UNIQUE") && d.Contains("(\"CaptureId\")"));
        Assert.Contains(indexes, d => d!.Contains("(\"TimestampUtc\" DESC, \"Id\" DESC)") && !d.Contains("WHERE"));
        Assert.Contains(indexes, d => d!.Contains("(\"TimestampUtc\")") && d.Contains("WHERE (\"Level\" >= 4)"));
        Assert.Contains(indexes, d => d!.Contains("(\"Source\", \"ProcessStartedUtc\" DESC)"));
        Assert.Contains(indexes, d => d!.Contains("UNIQUE") && d.Contains("(\"NameKey\")"));

        Assert.Equal(["jsonb"], await QueryAsync(db, """SELECT data_type AS "Value" FROM information_schema.columns WHERE table_name = 'LogEntries' AND column_name = 'Properties';"""));
        Assert.Equal(["NO"], await QueryAsync(db, """SELECT is_nullable AS "Value" FROM information_schema.columns WHERE table_name = 'LogCaptureStatus' AND column_name = 'LastIncompleteAtUtc';"""));
    }

    [Fact]
    public async Task Migration_SeedsFourFiltersExactly()
    {
        await using var db = await CreateMigratedDatabaseAsync();

        var rows = await db.LogFilters.AsNoTracking().OrderBy(f => f.SortOrder).ToListAsync();

        Assert.Equal([1, 2, 3, 4], rows.Select(r => r.SortOrder));
        Assert.Equal(SeedNames, rows.Select(r => r.Name));
        Assert.Equal(
            [
                "instance:latest uptime<15m",
                "source:worker cat:TwitchConnectionWatchdog,TwitchChatManager,TwitchLib.* -\"angestoßen (Quelle\" -\" gejoint.\"",
                "source:worker cat:SevenTv*",
                "level>=error",
            ],
            rows.Select(r => r.Filter));
        Assert.All(rows, r =>
        {
            Assert.Equal(new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc), r.CreatedAtUtc);
            Assert.Equal(r.CreatedAtUtc, r.UpdatedAtUtc);
        });
    }

    [Fact]
    public async Task SeedNameKeys_EqualNormalizeKeyOfSeedNames()
    {
        await using var db = await CreateMigratedDatabaseAsync();

        var rows = await db.LogFilters.AsNoTracking().OrderBy(f => f.SortOrder).ToListAsync();

        Assert.Equal(4, rows.Count);
        Assert.Equal(rows.Select(r => LogFilterNames.NormalizeKey(r.Name)), rows.Select(r => r.NameKey));
    }

    [Fact]
    public async Task SeedDeleted_StaysDeleted_AfterReapplyingContext()
    {
        var databaseName = await CreateDatabaseAsync();
        await using (var first = fixture.CreateDbContext(databaseName))
        {
            await first.Database.MigrateAsync();
            await first.Database.ExecuteSqlRawAsync("""DELETE FROM "LogFilters" WHERE "SortOrder" = 4;""");
        }

        await using var second = fixture.CreateDbContext(databaseName);
        await second.Database.MigrateAsync();

        Assert.Equal(3, await second.LogFilters.CountAsync());
    }

    [Fact]
    public async Task CaptureId_IsUnique()
    {
        await using var db = await CreateMigratedDatabaseAsync();
        var captureId = Guid.NewGuid();
        db.LogEntries.Add(NewEntry(captureId));
        await db.SaveChangesAsync();

        await using var other = fixture.CreateDbContext(db.Database.GetDbConnection().Database);
        other.LogEntries.Add(NewEntry(captureId));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());

        Assert.Equal("23505", Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [Fact]
    public async Task FilterNameKey_IsUnique()
    {
        await using var db = await CreateMigratedDatabaseAsync();
        var now = DateTime.UtcNow;
        db.LogFilters.Add(new LogFilter { Name = "alle FEHLER", NameKey = "alle fehler", Filter = "x", SortOrder = 5, CreatedAtUtc = now, UpdatedAtUtc = now });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Equal("23505", Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [Fact]
    public void NormalizeKey_FoldsUnicodeCaseAndWhitespace()
    {
        Assert.Equal("ärger im chat", LogFilterNames.NormalizeKey("  Ärger  im   Chat "));
        // Decomposed A + combining diaeresis folds to the composed form.
        Assert.Equal("ärger", LogFilterNames.NormalizeKey("Ärger"));
    }

    private static LogEntry NewEntry(Guid captureId) => new()
    {
        CaptureId = captureId,
        TimestampUtc = DateTime.UtcNow,
        Source = LogSource.Api,
        InstanceId = "abcd1234",
        ProcessStartedUtc = DateTime.UtcNow,
        Level = LogEntryLevel.Error,
        Category = "Test",
        Message = "m",
        Properties = """{"a":1}""",
    };

    private async Task<string> CreateDatabaseAsync()
    {
        var databaseName = $"log_capture_{Guid.NewGuid():N}";
        await using var admin = fixture.CreateDbContext();
#pragma warning disable EF1002 // locally generated Guid, CREATE DATABASE cannot be parameterized
        await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {databaseName}");
#pragma warning restore EF1002
        return databaseName;
    }

    private async Task<AppDbContext> CreateMigratedDatabaseAsync()
    {
        var db = fixture.CreateDbContext(await CreateDatabaseAsync());
        await db.Database.MigrateAsync();
        return db;
    }

    private static async Task<List<string>> QueryAsync(AppDbContext db, string sql) =>
        await db.Database.SqlQueryRaw<string>(sql).ToListAsync();
}
