using System.Text;
using EmotePurge.Core.Entities;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// <see cref="ILogCaptureStore"/> on the writer's own <see cref="LogCaptureDataSource"/>. A batch is one
/// multi-row <c>INSERT … ON CONFLICT ("CaptureId") DO NOTHING</c> (not binary COPY, which cannot skip
/// conflicts), so a repeated batch is idempotent. The status upsert raises <c>LastIncompleteAtUtc</c> only
/// (<c>GREATEST</c>). Every command has a 5 s timeout.
/// </summary>
internal sealed class PostgresLogCaptureStore(LogCaptureDataSource dataSource, LogCaptureIdentity identity) : ILogCaptureStore
{
    internal const int DefaultCommandTimeoutSeconds = 5;

    // Source, instance and process start are the same for every row of a process and are sent once.
    private const string InsertHead =
        "INSERT INTO \"LogEntries\" (\"CaptureId\", \"TimestampUtc\", \"Source\", \"InstanceId\", \"ProcessStartedUtc\", \"Level\", "
        + "\"Category\", \"EventId\", \"EventName\", \"Message\", \"ExceptionType\", \"Exception\", \"Properties\", \"Truncated\") VALUES ";

    private const string InsertTail = " ON CONFLICT (\"CaptureId\") DO NOTHING";
    private const int SharedParameters = 3;
    private const int ParametersPerRow = 11;

    private const string UpsertStatusSql =
        "INSERT INTO \"LogCaptureStatus\" (\"Source\", \"InstanceId\", \"ProcessStartedUtc\", \"LastFlushUtc\", \"CaptureEnabled\", "
        + "\"LastIncompleteAtUtc\", \"CleanShutdownAtUtc\") VALUES ($1, $2, $3, $4, $5, $6, $7) "
        + "ON CONFLICT (\"Source\", \"InstanceId\") DO UPDATE SET "
        + "\"ProcessStartedUtc\" = EXCLUDED.\"ProcessStartedUtc\", "
        + "\"LastFlushUtc\" = EXCLUDED.\"LastFlushUtc\", "
        + "\"CaptureEnabled\" = EXCLUDED.\"CaptureEnabled\", "
        + "\"LastIncompleteAtUtc\" = GREATEST(\"LogCaptureStatus\".\"LastIncompleteAtUtc\", EXCLUDED.\"LastIncompleteAtUtc\"), "
        + "\"CleanShutdownAtUtc\" = EXCLUDED.\"CleanShutdownAtUtc\"";

    private const string ReadLatestOtherSql =
        "SELECT \"Source\", \"InstanceId\", \"ProcessStartedUtc\", \"LastFlushUtc\", \"CaptureEnabled\", \"LastIncompleteAtUtc\", \"CleanShutdownAtUtc\" "
        + "FROM \"LogCaptureStatus\" WHERE \"Source\" = $1 AND \"InstanceId\" <> $2 "
        + "ORDER BY \"ProcessStartedUtc\" DESC, \"LastFlushUtc\" DESC LIMIT 1";

    /// <summary>Command timeout in seconds; tests shorten it to provoke timeouts quickly.</summary>
    internal int CommandTimeoutSeconds { get; init; } = DefaultCommandTimeoutSeconds;

    public async Task InsertAsync(IReadOnlyList<CapturedLogEntry> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await using var command = dataSource.DataSource.CreateCommand(BuildInsertSql(batch.Count));
        command.CommandTimeout = CommandTimeoutSeconds;
        var parameters = command.Parameters;
        parameters.Add(new NpgsqlParameter<short> { TypedValue = (short)identity.Source, NpgsqlDbType = NpgsqlDbType.Smallint });
        parameters.Add(new NpgsqlParameter<string> { TypedValue = identity.InstanceId, NpgsqlDbType = NpgsqlDbType.Varchar });
        parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = identity.ProcessStartedUtc, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        foreach (var entry in batch)
        {
            parameters.Add(new NpgsqlParameter<Guid> { TypedValue = entry.CaptureId, NpgsqlDbType = NpgsqlDbType.Uuid });
            parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = entry.TimestampUtc, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            parameters.Add(new NpgsqlParameter<short> { TypedValue = (short)entry.Level, NpgsqlDbType = NpgsqlDbType.Smallint });
            parameters.Add(new NpgsqlParameter<string> { TypedValue = entry.Category, NpgsqlDbType = NpgsqlDbType.Varchar });
            parameters.Add(Nullable(entry.EventId, NpgsqlDbType.Integer));
            parameters.Add(Nullable(entry.EventName, NpgsqlDbType.Varchar));
            parameters.Add(new NpgsqlParameter<string> { TypedValue = entry.Message, NpgsqlDbType = NpgsqlDbType.Text });
            parameters.Add(Nullable(entry.ExceptionType, NpgsqlDbType.Varchar));
            parameters.Add(Nullable(entry.Exception, NpgsqlDbType.Text));
            parameters.Add(Nullable(entry.PropertiesJson, NpgsqlDbType.Jsonb));
            parameters.Add(new NpgsqlParameter<bool> { TypedValue = entry.Truncated, NpgsqlDbType = NpgsqlDbType.Boolean });
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertStatusAsync(LogCaptureStatusWrite status, CancellationToken ct)
    {
        await using var command = dataSource.DataSource.CreateCommand(UpsertStatusSql);
        command.CommandTimeout = CommandTimeoutSeconds;
        command.Parameters.Add(new NpgsqlParameter<short> { TypedValue = (short)status.Source, NpgsqlDbType = NpgsqlDbType.Smallint });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status.InstanceId, NpgsqlDbType = NpgsqlDbType.Varchar });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = status.ProcessStartedUtc, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = status.LastFlushUtc, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = status.CaptureEnabled, NpgsqlDbType = NpgsqlDbType.Boolean });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = status.LastIncompleteAtUtc, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(Nullable(status.CleanShutdownAtUtc, NpgsqlDbType.TimestampTz));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<LogCaptureStatusWrite?> ReadLatestOtherInstanceAsync(LogSource source, string ownInstanceId, CancellationToken ct)
    {
        await using var command = dataSource.DataSource.CreateCommand(ReadLatestOtherSql);
        command.CommandTimeout = CommandTimeoutSeconds;
        command.Parameters.Add(new NpgsqlParameter<short> { TypedValue = (short)source, NpgsqlDbType = NpgsqlDbType.Smallint });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ownInstanceId, NpgsqlDbType = NpgsqlDbType.Varchar });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new LogCaptureStatusWrite(
            (LogSource)reader.GetInt16(0),
            reader.GetString(1),
            reader.GetFieldValue<DateTime>(2),
            reader.GetFieldValue<DateTime>(3),
            reader.GetBoolean(4),
            reader.GetFieldValue<DateTime>(5),
            await reader.IsDBNullAsync(6, ct) ? null : reader.GetFieldValue<DateTime>(6));
    }

    private static string BuildInsertSql(int rows)
    {
        var sql = new StringBuilder(InsertHead.Length + (rows * 96) + InsertTail.Length);
        sql.Append(InsertHead);
        for (var row = 0; row < rows; row++)
        {
            var p = SharedParameters + (row * ParametersPerRow);
            if (row > 0)
            {
                sql.Append(", ");
            }

            sql.Append('(')
                .Append('$').Append(p + 1).Append(", $").Append(p + 2).Append(", $1, $2, $3, $").Append(p + 3)
                .Append(", $").Append(p + 4).Append(", $").Append(p + 5).Append(", $").Append(p + 6)
                .Append(", $").Append(p + 7).Append(", $").Append(p + 8).Append(", $").Append(p + 9)
                .Append(", $").Append(p + 10).Append(", $").Append(p + 11).Append(')');
        }

        return sql.Append(InsertTail).ToString();
    }

    private static NpgsqlParameter Nullable(object? value, NpgsqlDbType type) =>
        new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
