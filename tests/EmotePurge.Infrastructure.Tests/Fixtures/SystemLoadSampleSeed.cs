using EmotePurge.Core.Entities;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Tests.Fixtures;

// Bulk-seeds SystemLoadSamples for the load-dashboard performance gate: 24 h of samples per source
// at a fixed cadence ending at nowUtc, with realistic monotone counters, two process-instance
// changes per source (counters reset, no predecessor) and one 10-minute gap shared by both sources.
// Binary COPY, not EF inserts: 172,800 rows must seed in seconds. Deterministic (fixed Random seed).
internal static class SystemLoadSampleSeed
{
    public static readonly TimeSpan GapLength = TimeSpan.FromMinutes(10);

    private const string CopyCommand =
        "COPY \"SystemLoadSamples\" (\"SampledAtUtc\", \"Source\", \"ProcessInstanceId\", \"PreviousSampledAtUtc\", " +
        "\"ProcessCpuTimeTicks\", \"CgroupCpuUsageMicroseconds\", \"CgroupCpuQuotaMicroseconds\", \"CgroupCpuPeriodMicroseconds\", " +
        "\"CgroupMemoryCurrentBytes\", \"CgroupMemoryMaxBytes\", \"HostCpuBusyJiffies\", \"HostCpuTotalJiffies\", " +
        "\"HostCpuIowaitJiffies\", \"HostCpuStealJiffies\", \"HostLoadAverage1\", \"HostLoadAverage5\", \"HostLoadAverage15\", " +
        "\"HostMemoryTotalBytes\", \"HostMemoryAvailableBytes\", \"HostSwapTotalBytes\", \"HostSwapFreeBytes\", " +
        "\"ActiveChannels\", \"LiveChannels\", \"ChatLines\", \"EmoteMatches\") FROM STDIN (FORMAT BINARY)";

    // Seeds 24 h ending at nowUtc and returns the number of rows written.
    public static async Task<long> SeedAsync(string connectionString, DateTime nowUtc, int intervalSeconds)
    {
        var slots = 24 * 3600 / intervalSeconds;
        var gapStart = nowUtc.AddHours(-12) - GapLength / 2;
        var gapEnd = gapStart + GapLength;
        long written = 0;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var writer = await connection.BeginBinaryImportAsync(CopyCommand);

        foreach (var source in new[] { SystemLoadSource.Api, SystemLoadSource.Worker })
        {
            var random = new Random(source == SystemLoadSource.Api ? 11 : 42);
            var instance = Guid.NewGuid();
            var counters = new Counters();
            DateTime? previous = null;

            for (var slot = 0; slot < slots; slot++)
            {
                var at = nowUtc.AddSeconds(-(double)(slots - 1 - slot) * intervalSeconds);
                if (slot == slots / 3 || slot == 2 * slots / 3)
                {
                    // Process restart: new instance id, counters start over, nothing to pair with.
                    instance = Guid.NewGuid();
                    counters = new Counters();
                    previous = null;
                }

                if (at >= gapStart && at < gapEnd)
                {
                    continue;
                }

                counters.Advance(random, intervalSeconds, source);
                await writer.StartRowAsync();
                await writer.WriteAsync(at, NpgsqlDbType.TimestampTz);
                await writer.WriteAsync(source.ToString(), NpgsqlDbType.Varchar);
                await writer.WriteAsync(instance, NpgsqlDbType.Uuid);
                if (previous is { } p)
                {
                    await writer.WriteAsync(p, NpgsqlDbType.TimestampTz);
                }
                else
                {
                    await writer.WriteNullAsync();
                }

                await writer.WriteAsync(counters.ProcessCpuTicks, NpgsqlDbType.Bigint);
                await writer.WriteAsync(counters.CgroupCpuMicros, NpgsqlDbType.Bigint);
                await writer.WriteAsync(source == SystemLoadSource.Worker ? 100_000L : 200_000L, NpgsqlDbType.Bigint);
                await writer.WriteAsync(100_000L, NpgsqlDbType.Bigint);
                await writer.WriteAsync(counters.CgroupMemory, NpgsqlDbType.Bigint);
                await writer.WriteAsync(536_870_912L, NpgsqlDbType.Bigint);
                await writer.WriteAsync(counters.HostBusy, NpgsqlDbType.Bigint);
                await writer.WriteAsync(counters.HostTotal, NpgsqlDbType.Bigint);
                await writer.WriteAsync(counters.HostIowait, NpgsqlDbType.Bigint);
                await writer.WriteAsync(counters.HostSteal, NpgsqlDbType.Bigint);
                await writer.WriteAsync(counters.Load1, NpgsqlDbType.Double);
                await writer.WriteAsync(counters.Load1 * 0.9, NpgsqlDbType.Double);
                await writer.WriteAsync(counters.Load1 * 0.8, NpgsqlDbType.Double);
                await writer.WriteAsync(25_769_803_776L, NpgsqlDbType.Bigint);
                await writer.WriteAsync(counters.HostAvailable, NpgsqlDbType.Bigint);
                await writer.WriteAsync(8_589_934_592L, NpgsqlDbType.Bigint);
                await writer.WriteAsync(8_000_000_000L, NpgsqlDbType.Bigint);
                if (source == SystemLoadSource.Worker)
                {
                    await writer.WriteAsync(counters.ActiveChannels, NpgsqlDbType.Integer);
                    await writer.WriteAsync(counters.LiveChannels, NpgsqlDbType.Integer);
                    await writer.WriteAsync(counters.ChatLines, NpgsqlDbType.Bigint);
                    await writer.WriteAsync(counters.EmoteMatches, NpgsqlDbType.Bigint);
                }
                else
                {
                    await writer.WriteNullAsync();
                    await writer.WriteNullAsync();
                    await writer.WriteNullAsync();
                    await writer.WriteNullAsync();
                }

                previous = at;
                written++;
            }
        }

        await writer.CompleteAsync();
        return written;
    }

    private sealed class Counters
    {
        public long ProcessCpuTicks;
        public long CgroupCpuMicros;
        public long CgroupMemory = 150_000_000;
        public long HostBusy;
        public long HostTotal;
        public long HostIowait;
        public long HostSteal;
        public double Load1 = 0.5;
        public long HostAvailable = 18_000_000_000;
        public int ActiveChannels = 40;
        public int LiveChannels = 12;
        public long ChatLines;
        public long EmoteMatches;

        public void Advance(Random random, int seconds, SystemLoadSource source)
        {
            var cores = source == SystemLoadSource.Worker ? 0.15 : 0.05;
            var used = cores * (0.5 + random.NextDouble());
            ProcessCpuTicks += (long)(used * seconds * 10_000_000);
            CgroupCpuMicros += (long)(used * seconds * 1_000_000);
            CgroupMemory = Math.Clamp(CgroupMemory + random.Next(-2_000_000, 2_000_001), 100_000_000, 450_000_000);

            // Six host cores at 100 Hz; utilisation wanders between 5 % and 45 %.
            var total = 6L * 100 * seconds;
            var busy = (long)(total * (0.05 + 0.4 * random.NextDouble()));
            HostTotal += total;
            HostBusy += busy;
            HostIowait += (long)(total * 0.01 * random.NextDouble());
            HostSteal += (long)(total * 0.005 * random.NextDouble());
            Load1 = Math.Clamp(Load1 + (random.NextDouble() - 0.5) * 0.2, 0.05, 6.0);
            HostAvailable = Math.Clamp(HostAvailable + random.Next(-20_000_000, 20_000_001), 10_000_000_000, 22_000_000_000);

            if (source == SystemLoadSource.Worker)
            {
                ActiveChannels = Math.Clamp(ActiveChannels + random.Next(-1, 2), 30, 60);
                LiveChannels = Math.Clamp(LiveChannels + random.Next(-1, 2), 0, ActiveChannels);
                ChatLines += random.Next(0, 400);
                EmoteMatches += random.Next(0, 150);
            }
        }
    }
}
