using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class SystemLoadRollupBatchingTests
{
    private static readonly DateTime From = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    // protects: backfill keys are chunked ascending by (hour, source) in chunks of the given size.
    // fails_when: a chunk exceeds the size, the order is not ascending, or a key is lost or repeated.
    // no_claim: SQL or transactions; pure key lists.
    [Fact]
    public void BackfillKeysAreChunkedAscending()
    {
        var keys = Enumerable.Range(0, 5)
            .SelectMany(hour => new[] { Key(SystemLoadSource.Worker, -10 + hour), Key(SystemLoadSource.Api, -10 + hour) })
            .Reverse()
            .ToArray();

        var batches = SystemLoadRollupBatching.Split(keys, From, 4);

        Assert.Equal([4, 4, 2], batches.Select(static batch => batch.Count));
        var flattened = batches.SelectMany(static batch => batch).ToArray();
        Assert.Equal(
            keys.OrderBy(static key => key.HourUtc).ThenBy(static key => key.Source),
            flattened);
    }

    // protects: all window keys travel in one final batch, even when they exceed the chunk size, so a dirty
    //   hour and its paired next hour are never split across transactions.
    // fails_when: window keys are chunked, mixed into a backfill chunk, or not last.
    // no_claim: that the caller persists a batch atomically.
    [Fact]
    public void WindowKeysAlwaysFormOneFinalBatch()
    {
        var keys = new[]
        {
            Key(SystemLoadSource.Api, -2),
            Key(SystemLoadSource.Api, 0),
            Key(SystemLoadSource.Worker, 0),
            Key(SystemLoadSource.Api, 1),
            Key(SystemLoadSource.Worker, 1),
        };

        var batches = SystemLoadRollupBatching.Split(keys, From, 2);

        Assert.Equal(2, batches.Count);
        Assert.Equal([Key(SystemLoadSource.Api, -2)], batches[0]);
        Assert.Equal(4, batches[1].Count);
        Assert.All(batches[1], key => Assert.True(key.HourUtc >= From));
    }

    // protects: no candidates means no batch (so no transaction and no save).
    // fails_when: an empty batch is returned.
    // no_claim: none beyond protects.
    [Fact]
    public void EmptyInputYieldsNoBatch() =>
        Assert.Empty(SystemLoadRollupBatching.Split([], From, 24));

    // protects: the indexed slice equals the former per-key regroup logic: same samples, same order, for several
    //   instances, an instance that starts inside the hour, an instance that ends before it, and empty hours.
    // fails_when: a predecessor is missing or doubled, an instance without hour samples leaks in, or order differs.
    // no_claim: SQL; the samples are synthetic and in memory.
    [Fact]
    public void SliceMatchesTheReferenceLogicForEveryHour()
    {
        var hour = From;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var samples = new List<SystemLoadSample>();
        for (var minute = -20; minute <= 100; minute += 5)
        {
            samples.Add(Sample(hour.AddMinutes(minute), a));
        }

        for (var minute = 17; minute <= 50; minute += 11)
        {
            samples.Add(Sample(hour.AddMinutes(minute).AddSeconds(1), b));
        }

        samples.Add(Sample(hour.AddMinutes(-30), c));
        samples.Add(Sample(hour.AddMinutes(-3), c));
        samples.Reverse();
        var index = new SystemLoadSampleIndex(samples);

        foreach (var offset in new[] { -3, -1, 0, 1, 2, 5 })
        {
            var target = hour.AddHours(offset);
            Assert.Equal(Reference(samples, target), index.Slice(target));
        }

        var slice = index.Slice(hour);
        Assert.DoesNotContain(slice, sample => sample.ProcessInstanceId == c);
        Assert.Contains(slice, sample => sample.ProcessInstanceId == b && sample.SampledAtUtc == hour.AddMinutes(17).AddSeconds(1));
        Assert.Equal(slice.OrderBy(static sample => sample.SampledAtUtc), slice);
    }

    private static SystemLoadHourKey Key(SystemLoadSource source, int hourOffset) => new(source, From.AddHours(hourOffset));

    private static SystemLoadSample Sample(DateTime atUtc, Guid instance) => new()
    {
        SampledAtUtc = atUtc,
        Source = SystemLoadSource.Api,
        ProcessInstanceId = instance,
    };

    // The logic of the former GetHourAndPredecessor, kept here as the reference.
    private static SystemLoadSample[] Reference(IEnumerable<SystemLoadSample> sourceSamples, DateTime hourUtc)
    {
        var hourEndUtc = hourUtc.AddHours(1);
        return sourceSamples
            .GroupBy(static sample => sample.ProcessInstanceId)
            .SelectMany(processSamples =>
            {
                var ordered = processSamples.OrderBy(static sample => sample.SampledAtUtc).ThenBy(static sample => sample.Id).ToArray();
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
            .ThenBy(static sample => sample.Id)
            .ToArray();
    }
}
