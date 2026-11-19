using EmotePurge.Core.Services;
using EmotePurge.Worker.WishCapture;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The bounded hand-over between the chat handler and the capture worker (plan #486 2.1, Sol-R2-P2-8): a refusal is
// visible and counted, a write never waits, and a closed queue refuses without throwing.
public class RewardObservationQueueTests
{
    [Fact]
    public void FullQueue_RefusesTheNextObservation_AndCountsTheOverflow()
    {
        var stats = new WorkerStats();
        var queue = new RewardObservationQueue(stats);

        for (var index = 0; index < RewardObservationQueue.Capacity; index++)
        {
            Assert.True(queue.TryEnqueue(Observation(index)));
        }

        // The 1,001st: a DropWrite channel would answer true here and lose it silently.
        Assert.False(queue.TryEnqueue(Observation(RewardObservationQueue.Capacity)));
        Assert.Equal(RewardObservationQueue.Capacity, queue.Count);

        var counters = stats.TakeRewardCaptureCounters();
        Assert.Equal(1, counters.Overflowed);
        Assert.Equal(0, counters.RejectedAtShutdown);
    }

    [Fact]
    public void FullQueue_AcceptsAgainAfterARead_AndKeepsTheOrder()
    {
        var stats = new WorkerStats();
        var queue = new RewardObservationQueue(stats);
        for (var index = 0; index < RewardObservationQueue.Capacity; index++)
        {
            queue.TryEnqueue(Observation(index));
        }

        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.TryEnqueue(Observation(RewardObservationQueue.Capacity)));

        Assert.Equal("m-0", first.MessageId);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal("m-1", second.MessageId);
        Assert.Equal(0, stats.TakeRewardCaptureCounters().Overflowed);
    }

    [Fact]
    public async Task Complete_RefusesNewObservations_CountsThem_AndKeepsTheQueuedOnesReadable()
    {
        var stats = new WorkerStats();
        var queue = new RewardObservationQueue(stats);
        queue.TryEnqueue(Observation(1));
        queue.TryEnqueue(Observation(2));

        queue.Complete();
        queue.Complete();
        var exception = Record.Exception(() => Assert.False(queue.TryEnqueue(Observation(3))));

        Assert.Null(exception);
        Assert.True(queue.IsCompleted);
        var counters = stats.TakeRewardCaptureCounters();
        Assert.Equal(1, counters.RejectedAtShutdown);
        Assert.Equal(0, counters.Overflowed);

        Assert.True(await queue.WaitToReadAsync(CancellationToken.None));
        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(["m-1", "m-2"], new[] { first.MessageId, second.MessageId });
        Assert.False(queue.TryDequeue(out _));
        Assert.False(await queue.WaitToReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TryEnqueue_DoesNotRunTheWaitingReadersContinuationOnTheWritersThread()
    {
        // The writer is TwitchLib's read loop. A reader whose continuation blocks must not block the write: with
        // synchronous continuations the write below would hang until the gate opens.
        var queue = new RewardObservationQueue(new WorkerStats());
        using var gate = new ManualResetEventSlim(false);
        var reader = Task.Run(async () =>
        {
            await queue.WaitToReadAsync(CancellationToken.None);
            gate.Wait(TimeSpan.FromSeconds(30));
        });
        await Task.Delay(50);

        var write = Task.Run(() => queue.TryEnqueue(Observation(1)));
        var finished = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(5)));
        gate.Set();
        await reader;

        Assert.Same(write, finished);
        Assert.True(await write);
    }

    [Fact]
    public async Task ConcurrentWriters_AcceptExactlyTheCapacity_AndCountEveryRefusal()
    {
        var stats = new WorkerStats();
        var queue = new RewardObservationQueue(stats);
        const int Writers = 4;
        const int PerWriter = 500;

        var accepted = await Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(() =>
        {
            var count = 0;
            for (var index = 0; index < PerWriter; index++)
            {
                if (queue.TryEnqueue(Observation((writer * PerWriter) + index)))
                {
                    count++;
                }
            }

            return count;
        })));

        Assert.Equal(RewardObservationQueue.Capacity, accepted.Sum());
        Assert.Equal((Writers * PerWriter) - RewardObservationQueue.Capacity, stats.TakeRewardCaptureCounters().Overflowed);
    }

    private static RewardObservation Observation(int index) => new(
        "111111111",
        "aaaa1111-2222-3333-4444-555555555555",
        "m-" + index,
        "333333333",
        "testviewer1",
        "sadge",
        new DateTime(2026, 11, 19, 18, 30, 0, DateTimeKind.Utc));
}
