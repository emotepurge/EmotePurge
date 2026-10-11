using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public sealed class SystemLoadRollupService(AppDbContext db) : ISystemLoadRollupService
{
    private const string CandidateHoursSql = """
        SELECT DISTINCT
            sample."Source" AS "Source",
            date_trunc('hour', sample."SampledAtUtc" AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' AS "HourUtc"
        FROM "SystemLoadSamples" AS sample
        WHERE sample."SampledAtUtc" < {0}
          AND (
            date_trunc('hour', sample."SampledAtUtc" AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' >= {1}
            OR NOT EXISTS (
                SELECT 1
                FROM "SystemLoadHourly" AS hourly
                WHERE hourly."Source" = sample."Source"
                  AND hourly."HourUtc" = date_trunc('hour', sample."SampledAtUtc" AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
            )
          )
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
        var sampleHourRows = await db.Database
            .SqlQueryRaw<CandidateHourRow>(CandidateHoursSql, closeEndUtc, recomputeFromUtc)
            .ToListAsync(cancellationToken);
        var candidateKeys = sampleHourRows
            .Select(static row => new HourKey(
                Enum.Parse<SystemLoadSource>(row.Source),
                DateTime.SpecifyKind(row.HourUtc, DateTimeKind.Utc)))
            .ToHashSet();

        var recentExistingKeys = await db.SystemLoadHourlies
            .AsNoTracking()
            .Where(hourly => hourly.HourUtc >= recomputeFromUtc && hourly.HourUtc <= lastClosableHourUtc)
            .Select(static hourly => new { hourly.Source, hourly.HourUtc })
            .ToListAsync(cancellationToken);
        var existingKeys = recentExistingKeys
            .Select(static hourly => new HourKey(hourly.Source, hourly.HourUtc))
            .ToHashSet();
        candidateKeys.UnionWith(existingKeys);

        var samples = await LoadCandidateSamplesAsync(candidateKeys, cancellationToken);
        var samplesBySource = samples
            .GroupBy(static sample => sample.Source)
            .ToDictionary(static group => group.Key, static group => group.ToArray());

        foreach (var key in candidateKeys.OrderBy(static key => key.HourUtc).ThenBy(static key => key.Source))
        {
            var hourSamples = GetHourAndPredecessor(samplesBySource, key.Source, key.HourUtc);
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
                db.Entry(existing).CurrentValues.SetValues(hourly);
            }
        }

        if (candidateKeys.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return candidateKeys.Count;
    }

    private async Task<List<SystemLoadSample>> LoadCandidateSamplesAsync(
        IReadOnlyCollection<HourKey> candidateKeys,
        CancellationToken cancellationToken)
    {
        if (candidateKeys.Count == 0)
        {
            return [];
        }

        var orderedKeys = candidateKeys
            .OrderBy(static key => key.HourUtc)
            .ThenBy(static key => key.Source)
            .ToArray();
        var sources = orderedKeys.Select(static key => key.Source.ToString()).ToArray();
        var hours = orderedKeys.Select(static key => key.HourUtc).ToArray();
        return await db.SystemLoadSamples
            .FromSqlInterpolated($"""
                WITH candidate_hours AS (
                    SELECT candidate."Source", candidate."HourUtc"
                    FROM unnest({sources}, {hours}) AS candidate("Source", "HourUtc")
                ), first_samples AS (
                    SELECT candidate."Source", candidate."HourUtc", sample."ProcessInstanceId",
                           MIN(sample."SampledAtUtc") AS "FirstSampledAtUtc"
                    FROM candidate_hours AS candidate
                    JOIN "SystemLoadSamples" AS sample
                      ON sample."Source" = candidate."Source"
                     AND sample."SampledAtUtc" >= candidate."HourUtc"
                     AND sample."SampledAtUtc" < candidate."HourUtc" + INTERVAL '1 hour'
                    GROUP BY candidate."Source", candidate."HourUtc", sample."ProcessInstanceId"
                ), selected_ids AS (
                    SELECT sample."Id"
                    FROM candidate_hours AS candidate
                    JOIN "SystemLoadSamples" AS sample
                      ON sample."Source" = candidate."Source"
                     AND sample."SampledAtUtc" >= candidate."HourUtc"
                     AND sample."SampledAtUtc" < candidate."HourUtc" + INTERVAL '1 hour'
                    UNION
                    SELECT predecessor."Id"
                    FROM first_samples AS first
                    JOIN LATERAL (
                        SELECT sample."Id"
                        FROM "SystemLoadSamples" AS sample
                        WHERE sample."Source" = first."Source"
                          AND sample."ProcessInstanceId" = first."ProcessInstanceId"
                          AND sample."SampledAtUtc" < first."FirstSampledAtUtc"
                        ORDER BY sample."SampledAtUtc" DESC, sample."Id" DESC
                        LIMIT 1
                    ) AS predecessor ON TRUE
                )
                SELECT sample.*
                FROM "SystemLoadSamples" AS sample
                JOIN selected_ids ON selected_ids."Id" = sample."Id"
                ORDER BY sample."Source", sample."SampledAtUtc"
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    private static IReadOnlyList<SystemLoadSample> GetHourAndPredecessor(
        IReadOnlyDictionary<SystemLoadSource, SystemLoadSample[]> samplesBySource,
        SystemLoadSource source,
        DateTime hourUtc)
    {
        if (!samplesBySource.TryGetValue(source, out var sourceSamples))
        {
            return [];
        }

        var hourEndUtc = hourUtc.AddHours(1);
        return sourceSamples
            .GroupBy(static sample => sample.ProcessInstanceId)
            .SelectMany(processSamples =>
            {
                var ordered = processSamples.OrderBy(static sample => sample.SampledAtUtc).ToArray();
                var firstHourSample = Array.FindIndex(ordered, sample => sample.SampledAtUtc >= hourUtc);
                if (firstHourSample < 0 || ordered[firstHourSample].SampledAtUtc >= hourEndUtc)
                {
                    return [];
                }

                var firstIncluded = Math.Max(0, firstHourSample - 1);
                var endExclusive = firstHourSample;
                while (endExclusive < ordered.Length && ordered[endExclusive].SampledAtUtc < hourEndUtc)
                {
                    endExclusive++;
                }

                return ordered[firstIncluded..endExclusive];
            })
            .OrderBy(static sample => sample.SampledAtUtc)
            .ToArray();
    }

    private static DateTime FloorToHour(DateTime timestampUtc) =>
        new(timestampUtc.Ticks - timestampUtc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

    private readonly record struct HourKey(SystemLoadSource Source, DateTime HourUtc);

    private sealed class CandidateHourRow
    {
        public CandidateHourRow()
        {
        }

        public string Source { get; set; } = string.Empty;

        public DateTime HourUtc { get; set; }
    }
}
