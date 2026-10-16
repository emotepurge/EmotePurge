using System.Collections.Concurrent;
using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Tests.Fixtures;

/// <summary>
/// Seeding helpers for the log viewer tests: a database of their own per test class (created and migrated
/// once, emptied before every test — instance and status tests need an empty <c>LogCaptureStatus</c>), entry
/// and status builders with harmless defaults, and a raw Npgsql insert for entries whose transaction a test
/// holds open (the writer's path is raw Npgsql too, so no EF interceptor can reach it).
/// </summary>
internal static class LogEntrySeed
{
    public const string DefaultInstance = "aaaa0001";

    private static readonly ConcurrentDictionary<string, Lazy<Task>> Databases = new(StringComparer.Ordinal);

    /// <summary>Creates and migrates <paramref name="databaseName"/> once per test run, then empties the log tables.</summary>
    public static async Task PrepareDatabaseAsync(PostgresFixture fixture, string databaseName)
    {
        await Databases.GetOrAdd(databaseName, name => new Lazy<Task>(() => CreateAsync(fixture, name))).Value;
        await using var db = fixture.CreateDbContext(databaseName);
        await db.Database.ExecuteSqlRawAsync("TRUNCATE \"LogEntries\", \"LogCaptureStatus\" RESTART IDENTITY;");
    }

    public static LogEntry Entry(
        DateTime timestampUtc,
        string message = "message",
        LogEntryLevel level = LogEntryLevel.Information,
        LogSource source = LogSource.Worker,
        string category = "EmotePurge.Worker.Worker",
        string instanceId = DefaultInstance,
        DateTime? processStartedUtc = null,
        int? eventId = null,
        string? eventName = null,
        string? exception = null,
        string? properties = null) =>
        new()
        {
            CaptureId = Guid.NewGuid(),
            TimestampUtc = DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc),
            Source = source,
            InstanceId = instanceId,
            ProcessStartedUtc = DateTime.SpecifyKind(processStartedUtc ?? timestampUtc.AddHours(-1), DateTimeKind.Utc),
            Level = level,
            Category = category,
            EventId = eventId,
            EventName = eventName,
            Message = message,
            ExceptionType = exception is null ? null : "System.InvalidOperationException",
            Exception = exception,
            Properties = properties,
        };

    public static LogCaptureStatus Status(LogSource source, string instanceId, DateTime processStartedUtc, DateTime? lastFlushUtc = null) =>
        new()
        {
            Source = source,
            InstanceId = instanceId,
            ProcessStartedUtc = DateTime.SpecifyKind(processStartedUtc, DateTimeKind.Utc),
            LastFlushUtc = DateTime.SpecifyKind(lastFlushUtc ?? processStartedUtc, DateTimeKind.Utc),
            CaptureEnabled = true,
            LastIncompleteAtUtc = DateTime.SpecifyKind(processStartedUtc, DateTimeKind.Utc),
        };

    /// <summary>Inserts the entries and returns them with their database ids.</summary>
    public static async Task<IReadOnlyList<LogEntry>> InsertAsync(PostgresFixture fixture, string databaseName, params LogEntry[] entries)
    {
        await using var db = fixture.CreateDbContext(databaseName);
        db.LogEntries.AddRange(entries);
        await db.SaveChangesAsync();
        return entries;
    }

    public static async Task InsertStatusAsync(PostgresFixture fixture, string databaseName, params LogCaptureStatus[] rows)
    {
        await using var db = fixture.CreateDbContext(databaseName);
        db.LogCaptureStatuses.AddRange(rows);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Inserts one entry on <paramref name="connection"/> inside whatever transaction it holds and returns its
    /// id; the row stays invisible to other sessions until that transaction commits.
    /// </summary>
    public static async Task<long> InsertRawAsync(NpgsqlConnection connection, LogEntry entry)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO \"LogEntries\" (\"CaptureId\", \"TimestampUtc\", \"Source\", \"InstanceId\", \"ProcessStartedUtc\", \"Level\", \"Category\", \"Message\", \"Truncated\") " +
            "VALUES (@capture, @ts, @source, @instance, @started, @level, @category, @message, false) RETURNING \"Id\"",
            connection);
        command.Parameters.Add(new NpgsqlParameter("capture", NpgsqlDbType.Uuid) { Value = entry.CaptureId });
        command.Parameters.Add(new NpgsqlParameter("ts", NpgsqlDbType.TimestampTz) { Value = entry.TimestampUtc });
        command.Parameters.Add(new NpgsqlParameter("source", NpgsqlDbType.Smallint) { Value = (short)entry.Source });
        command.Parameters.Add(new NpgsqlParameter("instance", NpgsqlDbType.Varchar) { Value = entry.InstanceId });
        command.Parameters.Add(new NpgsqlParameter("started", NpgsqlDbType.TimestampTz) { Value = entry.ProcessStartedUtc });
        command.Parameters.Add(new NpgsqlParameter("level", NpgsqlDbType.Smallint) { Value = (short)entry.Level });
        command.Parameters.Add(new NpgsqlParameter("category", NpgsqlDbType.Varchar) { Value = entry.Category });
        command.Parameters.Add(new NpgsqlParameter("message", NpgsqlDbType.Text) { Value = entry.Message });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task CreateAsync(PostgresFixture fixture, string databaseName)
    {
        await using (var admin = fixture.CreateDbContext())
        {
#pragma warning disable EF1002 // constant test database name, CREATE DATABASE cannot be parameterized
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {databaseName}");
#pragma warning restore EF1002
        }

        await using AppDbContext db = fixture.CreateDbContext(databaseName);
        await db.Database.MigrateAsync();
    }
}
