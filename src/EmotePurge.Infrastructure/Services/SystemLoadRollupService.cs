using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public sealed class SystemLoadRollupService(AppDbContext db, ILogger<SystemLoadRollupService> logger) : ISystemLoadRollupService
{
    // Backfill keys per transaction. Bounds memory: the Worker limit is 512 MB, and the measured peak was
    // 230 MB at 24 keys on 1 s samples over 7 days without hourly rows (roughly 3,600 samples per key).
    private const int BackfillBatchKeys = 24;

    // Backfill only: (source, hour) pairs with samples before the close boundary and no hourly row, however old.
    // Loose index scan instead of a scan over every raw row: enumerate the sources with min() jumps, then per
    // source jump from hour to hour with one index probe each, then anti-join against the hourly rows per pair.
    // The cost follows the number of hours, not the number of samples. No lower bound, so nothing is stranded.
    private const string CandidateHoursSql = """
        WITH RECURSIVE sources AS (
            SELECT min(sample."Source") AS "Source"
            FROM "SystemLoadSamples" AS sample
            UNION ALL
            SELECT (
                SELECT min(sample."Source")
                FROM "SystemLoadSamples" AS sample
                WHERE sample."Source" > sources."Source")
            FROM sources
            WHERE sources."Source" IS NOT NULL
        ), sample_hours AS (
            SELECT sources."Source",
                   (SELECT min(sample."SampledAtUtc")
                    FROM "SystemLoadSamples" AS sample
                    WHERE sample."Source" = sources."Source"
                      AND sample."SampledAtUtc" < {0}) AS "FirstSampledAtUtc"
            FROM sources
            WHERE sources."Source" IS NOT NULL
            UNION ALL
            SELECT sample_hours."Source",
                   (SELECT min(sample."SampledAtUtc")
                    FROM "SystemLoadSamples" AS sample
                    WHERE sample."Source" = sample_hours."Source"
                      AND sample."SampledAtUtc" >= (date_trunc('hour', sample_hours."FirstSampledAtUtc" AT TIME ZONE 'UTC') + INTERVAL '1 hour') AT TIME ZONE 'UTC'
                      AND sample."SampledAtUtc" < {0})
            FROM sample_hours
            WHERE sample_hours."FirstSampledAtUtc" IS NOT NULL
        )
        SELECT sample_hours."Source" AS "Source",
               date_trunc('hour', sample_hours."FirstSampledAtUtc" AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' AS "HourUtc"
        FROM sample_hours
        WHERE sample_hours."FirstSampledAtUtc" IS NOT NULL
          AND NOT EXISTS (
            SELECT 1 FROM "SystemLoadHourly" AS hourly
            WHERE hourly."Source" = sample_hours."Source"
              AND hourly."HourUtc" = date_trunc('hour', sample_hours."FirstSampledAtUtc" AT TIME ZONE 'UTC') AT TIME ZONE 'UTC')
        """;

    private const string WindowCountsSql = """
        SELECT
            sample."Source" AS "Source",
            date_trunc('hour', sample."SampledAtUtc" AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' AS "HourUtc",
            COUNT(*) AS "Count"
        FROM "SystemLoadSamples" AS sample
        WHERE sample."Source" = ANY({2})
          AND sample."SampledAtUtc" >= {0}
          AND sample."SampledAtUtc" < {1}
        GROUP BY 1, 2
        """;

    public async Task<int> RecomputeClosableHoursAsync(
        DateTime nowUtc,
        TimeSpan gracePeriod,
        int samplingIntervalSeconds,
        CancellationToken cancellationToken = default)
    {
        var graceCutoffUtc = nowUtc - gracePeriod;
        var lastClosableHourUtc = FloorToHour(graceCutoffUtc).AddHours(-1);
        var closeEndUtc = lastClosableHourUtc.AddHours(1);
        var recomputeFromUtc = FloorToHour(nowUtc.AddHours(-3));
        var backfillRows = await db.Database
            .SqlQueryRaw<CandidateHourRow>(CandidateHoursSql, closeEndUtc)
            .ToListAsync(cancellationToken);
        var candidateKeys = backfillRows
            .Select(static row => new SystemLoadHourKey(
                Enum.Parse<SystemLoadSource>(row.Source),
                DateTime.SpecifyKind(row.HourUtc, DateTimeKind.Utc)))
            .ToHashSet();

        var windowHours = new List<DateTime>();
        for (var hour = recomputeFromUtc; hour <= lastClosableHourUtc; hour = hour.AddHours(1))
        {
            windowHours.Add(hour);
        }

        var rawCountRows = await db.Database
            .SqlQueryRaw<WindowCountRow>(
                WindowCountsSql,
                recomputeFromUtc,
                closeEndUtc,
                Enum.GetValues<SystemLoadSource>().Select(static source => source.ToString()).ToArray())
            .ToListAsync(cancellationToken);
        var rawCounts = rawCountRows.ToDictionary(
            static row => new SystemLoadHourKey(
                Enum.Parse<SystemLoadSource>(row.Source),
                DateTime.SpecifyKind(row.HourUtc, DateTimeKind.Utc)),
            static row => row.Count);
        var recentExisting = await db.SystemLoadHourlies
            .AsNoTracking()
            .Where(hourly => hourly.HourUtc >= recomputeFromUtc && hourly.HourUtc <= lastClosableHourUtc)
            .Select(static hourly => new { hourly.Source, hourly.HourUtc, hourly.SampleCount })
            .ToListAsync(cancellationToken);
        var storedCounts = recentExisting.ToDictionary(
            static hourly => new SystemLoadHourKey(hourly.Source, hourly.HourUtc),
            static hourly => hourly.SampleCount);
        var existingKeys = storedCounts.Keys.ToHashSet();
        candidateKeys.UnionWith(SystemLoadRollupSelection.SelectDirtyHours(windowHours, rawCounts, storedCounts));

        var keptRows = 0;
        var keptGroups = 0;
        foreach (var batch in SystemLoadRollupBatching.Split(candidateKeys, recomputeFromUtc, BackfillBatchKeys))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = await LoadCandidateSamplesAsync(batch, cancellationToken);
            var indexBySource = samples
                .GroupBy(static sample => sample.Source)
                .ToDictionary(static group => group.Key, static group => new SystemLoadSampleIndex(group));

            foreach (var key in batch)
            {
                var hourSamples = indexBySource.TryGetValue(key.Source, out var index) ? index.Slice(key.HourUtc) : [];
                var hourly = SystemLoadRollupMath.Calculate(hourSamples, key.Source, key.HourUtc, samplingIntervalSeconds);
                var existing = existingKeys.Contains(key)
                    ? await db.SystemLoadHourlies.SingleAsync(
                        row => row.Source == key.Source && row.HourUtc == key.HourUtc,
                        cancellationToken)
                    : null;
                if (existing is null)
                {
                    db.SystemLoadHourlies.Add(hourly);
                }
                else
                {
                    var kept = SystemLoadRollupSelection.KeepStoredCpuGroups(existing, hourly);
                    if (kept > 0)
                    {
                        keptRows++;
                        keptGroups += kept;
                    }

                    db.Entry(existing).CurrentValues.SetValues(hourly);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        if (keptRows > 0)
        {
            logger.LogWarning(
                "System-load rollup kept stored CPU figures for {Rows} hourly rows ({Groups} figure groups) because the recompute found no valid pairs.",
                keptRows,
                keptGroups);
        }

        return candidateKeys.Count;
    }

    private async Task<List<SystemLoadSample>> LoadCandidateSamplesAsync(
        IReadOnlyCollection<SystemLoadHourKey> candidateKeys,
        CancellationToken cancellationToken)
    {
        if (candidateKeys.Count == 0)
        {
            return [];
        }

        var sources = candidateKeys.Select(static key => key.Source.ToString()).ToArray();
        var hours = candidateKeys.Select(static key => key.HourUtc).ToArray();

        // The ORDER BY inside the LATERAL subquery is load-bearing: without it Postgres flattens the subquery
        // into a plain join on Source alone and the plan degrades to keys x rows.
        return await db.SystemLoadSamples
            .FromSqlInterpolated($"""
                WITH candidate_hours AS (
                    SELECT candidate."Source", candidate."HourUtc"
                    FROM unnest({sources}, {hours}) AS candidate("Source", "HourUtc")
                ), hour_samples AS (
                    SELECT sample.*
                    FROM candidate_hours AS candidate
                    CROSS JOIN LATERAL (
                        SELECT inner_sample.*
                        FROM "SystemLoadSamples" AS inner_sample
                        WHERE inner_sample."Source" = candidate."Source"
                          AND inner_sample."SampledAtUtc" >= candidate."HourUtc"
                          AND inner_sample."SampledAtUtc" < candidate."HourUtc" + INTERVAL '1 hour'
                        ORDER BY inner_sample."SampledAtUtc"
                    ) AS sample
                ), first_samples AS (
                    SELECT hour_sample."Source", hour_sample."ProcessInstanceId",
                           MIN(hour_sample."SampledAtUtc") AS "FirstSampledAtUtc"
                    FROM hour_samples AS hour_sample
                    GROUP BY hour_sample."Source",
                             date_trunc('hour', hour_sample."SampledAtUtc" AT TIME ZONE 'UTC'),
                             hour_sample."ProcessInstanceId"
                )
                SELECT hour_sample.*
                FROM hour_samples AS hour_sample
                UNION ALL
                SELECT predecessor.*
                FROM first_samples AS first
                CROSS JOIN LATERAL (
                    SELECT sample.*
                    FROM "SystemLoadSamples" AS sample
                    WHERE sample."Source" = first."Source"
                      AND sample."ProcessInstanceId" = first."ProcessInstanceId"
                      AND sample."SampledAtUtc" < first."FirstSampledAtUtc"
                    ORDER BY sample."SampledAtUtc" DESC, sample."Id" DESC
                    LIMIT 1
                ) AS predecessor
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM candidate_hours AS candidate
                    WHERE candidate."Source" = predecessor."Source"
                      AND candidate."HourUtc" = date_trunc('hour', predecessor."SampledAtUtc" AT TIME ZONE 'UTC') AT TIME ZONE 'UTC')
                ORDER BY "Source", "SampledAtUtc", "Id"
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    private static DateTime FloorToHour(DateTime timestampUtc) =>
        new(timestampUtc.Ticks - timestampUtc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

    private sealed class WindowCountRow
    {
        public string Source { get; set; } = string.Empty;

        public DateTime HourUtc { get; set; }

        public long Count { get; set; }
    }

    private sealed class CandidateHourRow
    {
        public CandidateHourRow()
        {
        }

        public string Source { get; set; } = string.Empty;

        public DateTime HourUtc { get; set; }
    }
}
