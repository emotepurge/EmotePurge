using EmotePurge.Worker.ChatLogBackfill;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The BACKFILL: nudge's latch (spec 4.8, D8): set before or during a wait, taken exactly once; the idle
// timeout runs on the injected clock.
public class ChatLogBackfillSignalTests
{
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task ASetBeforeTheWait_IsTakenAtOnce()
    {
        var signal = new ChatLogBackfillSignal();
        signal.Set();

        Assert.True(await signal.WaitAsync(Long, new FakeTimeProvider(), CancellationToken.None));
    }

    [Fact]
    public async Task SeveralSets_CollapseIntoOneWakeUp()
    {
        var clock = new FakeTimeProvider();
        var signal = new ChatLogBackfillSignal();
        signal.Set();
        signal.Set();
        signal.Set();

        Assert.True(await signal.WaitAsync(Long, clock, CancellationToken.None));

        var second = signal.WaitAsync(Long, clock, CancellationToken.None);
        Assert.False(second.IsCompleted);
        clock.Advance(Long);
        Assert.False(await second);
    }

    [Fact]
    public async Task ASetDuringTheWait_WakesIt()
    {
        var signal = new ChatLogBackfillSignal();
        var waiting = signal.WaitAsync(Long, new FakeTimeProvider(), CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        signal.Set();

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WithoutASet_TheWaitEndsWithTheTimeoutOnTheInjectedClock()
    {
        var clock = new FakeTimeProvider();
        var signal = new ChatLogBackfillSignal();
        var waiting = signal.WaitAsync(Long, clock, CancellationToken.None);

        clock.Advance(Long - TimeSpan.FromSeconds(1));
        Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ATimedOutWait_DoesNotSwallowTheNextSet()
    {
        var clock = new FakeTimeProvider();
        var signal = new ChatLogBackfillSignal();
        var waiting = signal.WaitAsync(Long, clock, CancellationToken.None);
        clock.Advance(Long);
        Assert.False(await waiting);

        signal.Set();

        Assert.True(await signal.WaitAsync(TimeSpan.Zero, clock, CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_Throws()
    {
        var signal = new ChatLogBackfillSignal();
        using var cancellation = new CancellationTokenSource();
        var waiting = signal.WaitAsync(Long, new FakeTimeProvider(), cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
