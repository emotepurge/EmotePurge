using EmotePurge.Worker.SevenTv;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Issue #59: one channel failing on a shared-set delta must not skip the channels after it.
public class PerChannelFanOutTests
{
    [Fact]
    public async Task ApplyAsync_OneChannelThrows_RemainingChannelsAreStillApplied()
    {
        var applied = new List<string>();

        await PerChannelFanOut.ApplyAsync(
            ["a", "b", "c"],
            name =>
            {
                if (name == "b")
                {
                    throw new InvalidOperationException("boom");
                }

                applied.Add(name);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["a", "c"], applied);
    }

    [Fact]
    public async Task ApplyAsync_CancellationRequested_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var applied = new List<string>();

        await Assert.ThrowsAsync<OperationCanceledException>(() => PerChannelFanOut.ApplyAsync(
            ["a", "b"],
            name =>
            {
                applied.Add(name);
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
            NullLogger.Instance,
            cts.Token));

        Assert.Equal(["a"], applied);
    }

    [Fact]
    public async Task ApplyAsync_CancellationNotRequested_IsTreatedAsFailureAndContinues()
    {
        var applied = new List<string>();

        await PerChannelFanOut.ApplyAsync(
            ["a", "b"],
            name =>
            {
                applied.Add(name);
                throw new OperationCanceledException();
            },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["a", "b"], applied);
    }
}
