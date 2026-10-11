using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public sealed class SystemLoadRollupService(AppDbContext db) : ISystemLoadRollupService
{
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
        var samples = await db.SystemLoadSamples
            .AsNoTracking()
            .Where(sample => sample.SampledAtUtc < closeEndUtc)
            .OrderBy(sample => sample.Source)
            .ThenBy(sample => sample.SampledAtUtc)
            .ToListAsync(cancellationToken);

        var existingHours = await db.SystemLoadHourlies
            .AsNoTracking()
            .Where(hourly => hourly.HourUtc <= lastClosableHourUtc)
            .Select(hourly => new { hourly.Source, hourly.HourUtc })
            .ToListAsync(cancellationToken);
        var existingKeys = existingHours.Select(static hourly => (hourly.Source, hourly.HourUtc)).ToHashSet();
        var recentExistingKeys = existingHours
            .Where(hourly => hourly.HourUtc >= recomputeFromUtc)
            .Select(static hourly => (hourly.Source, hourly.HourUtc));
        var candidateKeys = samples
            .Select(sample => (sample.Source, HourUtc: FloorToHour(sample.SampledAtUtc)))
            .Where(key => key.HourUtc <= lastClosableHourUtc)
            .Where(key => key.HourUtc >= recomputeFromUtc || !existingKeys.Contains(key))
            .ToHashSet();
        candidateKeys.UnionWith(recentExistingKeys);

        var samplesBySource = samples
            .GroupBy(static sample => sample.Source)
            .ToDictionary(static group => group.Key, static group => group.ToArray());

        foreach (var (source, hourUtc) in candidateKeys.OrderBy(static key => key.HourUtc).ThenBy(static key => key.Source))
        {
            var hourSamples = GetHourAndPredecessor(samplesBySource, source, hourUtc);
            var hourly = SystemLoadRollupMath.Calculate(hourSamples, source, hourUtc, samplingIntervalSeconds);
            var existing = existingKeys.Contains((source, hourUtc))
                ? await db.SystemLoadHourlies.SingleAsync(
                    row => row.Source == source && row.HourUtc == hourUtc,
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
}
