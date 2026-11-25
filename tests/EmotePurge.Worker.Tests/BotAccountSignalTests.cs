using EmotePurge.Worker.BotAccount;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The BOTACCOUNT: nudge's latch (plan #487 2.5): set before or during a wait, taken exactly once; the cadence timeout
// runs on the injected clock and a timed-out wait does not swallow the next nudge.
public class BotAccountSignalTests
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task SeveralSetsBeforeTheWait_CollapseIntoOneWakeUp()
    {
        var clock = new FakeTimeProvider();
        var signal = new BotAccountSignal();
        signal.Set();
        signal.Set();

        Assert.True(await signal.WaitAsync(Cadence, clock, CancellationToken.None));

        var second = signal.WaitAsync(Cadence, clock, CancellationToken.None);
        Assert.False(second.IsCompleted);
        clock.Advance(Cadence);
        Assert.False(await second);
    }

    [Fact]
    public async Task ASetDuringTheWait_WakesIt()
    {
        var signal = new BotAccountSignal();
        var waiting = signal.WaitAsync(Cadence, new FakeTimeProvider(), CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        signal.Set();

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ATimedOutWait_DoesNotSwallowTheNextSet()
    {
        var clock = new FakeTimeProvider();
        var signal = new BotAccountSignal();
        var waiting = signal.WaitAsync(Cadence, clock, CancellationToken.None);
        clock.Advance(Cadence - TimeSpan.FromSeconds(1));
        Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));

        signal.Set();

        Assert.True(await signal.WaitAsync(TimeSpan.Zero, clock, CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_Throws()
    {
        var signal = new BotAccountSignal();
        using var cancellation = new CancellationTokenSource();
        var waiting = signal.WaitAsync(Cadence, new FakeTimeProvider(), cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
