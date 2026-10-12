using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The read of the raw load path (#381, spec 5.1/5.2): one index range scan, chart columns only, in time
/// order, streamed through a plain <see cref="System.Data.Common.DbDataReader"/> loop into the
/// <see cref="AdminDashboardRawLoadAccumulator"/> with one reused row object.
/// </summary>
/// <remarks>
/// Measured at 24 h x 1 s (171,600 samples, the response gate of spec section 11 B):
/// <list type="bullet">
/// <item>EF materialization of every row cost ~430 ms and ~93 MiB per read.</item>
/// <item>Bucketing in SQL (LAG pairing, percentile_disc or per-bucket value arrays) was slower still, at
/// 750-1,400 ms: Postgres evaluates the per-row rate expressions and aggregate transitions far slower
/// than this loop does.</item>
/// <item>This typed reader loop reads in ~260-290 ms with under 1 MiB allocated.</item>
/// </list>
/// The read starts one maximum pair gap (<see cref="SystemLoadRollupMath.MaximumPairGap"/>) before the
/// window. That replaces a separate predecessor lookup: only a sample in that stretch can form a valid
/// pair with an instance's first sample in the window, and the last such sample of the instance is
/// exactly its immediate predecessor. An older predecessor would fail the gap rule anyway.
/// The command goes through EF's relational command pipeline, so command interceptors see it like any
/// other query.
/// </remarks>
internal static class AdminDashboardRawLoadQuery
{
    // {0} = fromUtc minus the maximum pair gap, {1} = toUtc, both inclusive. The column order is the
    // ordinal contract of ReadRow.
    private const string Sql = """
        SELECT
            sample."Id",
            sample."SampledAtUtc",
            sample."Source" = 'Worker',
            sample."ProcessInstanceId",
            sample."PreviousSampledAtUtc",
            sample."ProcessCpuTimeTicks",
            sample."CgroupCpuUsageMicroseconds",
            sample."CgroupCpuQuotaMicroseconds",
            sample."CgroupCpuPeriodMicroseconds",
            sample."CgroupMemoryCurrentBytes",
            sample."CgroupMemoryMaxBytes",
            sample."HostCpuBusyJiffies",
            sample."HostCpuTotalJiffies",
            sample."HostLoadAverage1",
            sample."HostMemoryTotalBytes",
            sample."HostMemoryAvailableBytes",
            sample."HostSwapTotalBytes",
            sample."HostSwapFreeBytes",
            sample."ActiveChannels",
            sample."LiveChannels",
            sample."ChatLines",
            sample."EmoteMatches"
        FROM "SystemLoadSamples" AS sample
        WHERE sample."Source" IN ('Api', 'Worker')
          AND sample."SampledAtUtc" >= {0}
          AND sample."SampledAtUtc" <= {1}
        ORDER BY sample."SampledAtUtc", sample."Id"
        """;

    public static async Task ReadIntoAsync(
        AppDbContext db,
        AdminDashboardLoadWindow window,
        int samplingIntervalSeconds,
        AdminDashboardRawLoadAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var readFromUtc = window.FromUtc - SystemLoadRollupMath.MaximumPairGap(samplingIntervalSeconds);
        var command = db.GetService<IRawSqlCommandBuilder>().Build(Sql, [readFromUtc, window.ToUtc]);
        await using var reader = await command.RelationalCommand.ExecuteReaderAsync(
            new RelationalCommandParameterObject(
                db.GetService<IRelationalConnection>(),
                command.ParameterValues,
                readerColumns: null,
                db,
                db.GetService<IRelationalCommandDiagnosticsLogger>(),
                CommandSource.FromSqlQuery),
            cancellationToken);

        var row = new AdminDashboardLoadSampleRow();
        while (await reader.ReadAsync(cancellationToken))
        {
            ReadRow(reader.DbDataReader, row);
            accumulator.Add(row);
        }
    }

    private static void ReadRow(System.Data.Common.DbDataReader reader, AdminDashboardLoadSampleRow row)
    {
        row.Id = reader.GetInt64(0);
        row.SampledAtUtc = reader.GetFieldValue<DateTime>(1);
        row.IsWorker = reader.GetBoolean(2);
        row.ProcessInstanceId = reader.GetGuid(3);
        row.PreviousSampledAtUtc = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTime>(4);
        row.ProcessCpuTimeTicks = Int64(reader, 5);
        row.CgroupCpuUsageMicroseconds = Int64(reader, 6);
        row.CgroupCpuQuotaMicroseconds = Int64(reader, 7);
        row.CgroupCpuPeriodMicroseconds = Int64(reader, 8);
        row.CgroupMemoryCurrentBytes = Int64(reader, 9);
        row.CgroupMemoryMaxBytes = Int64(reader, 10);
        row.HostCpuBusyJiffies = Int64(reader, 11);
        row.HostCpuTotalJiffies = Int64(reader, 12);
        row.HostLoadAverage1 = reader.IsDBNull(13) ? null : reader.GetDouble(13);
        row.HostMemoryTotalBytes = Int64(reader, 14);
        row.HostMemoryAvailableBytes = Int64(reader, 15);
        row.HostSwapTotalBytes = Int64(reader, 16);
        row.HostSwapFreeBytes = Int64(reader, 17);
        row.ActiveChannels = reader.IsDBNull(18) ? null : reader.GetInt32(18);
        row.LiveChannels = reader.IsDBNull(19) ? null : reader.GetInt32(19);
        row.ChatLines = Int64(reader, 20);
        row.EmoteMatches = Int64(reader, 21);
    }

    private static long? Int64(System.Data.Common.DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
