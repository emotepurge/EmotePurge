using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class InfrastructureSystemLoadWorkloadSourceTests
{
    [Fact]
    public async Task ReturnsActiveChannelsAndLeavesWorkerOnlyValuesUnknown()
    {
        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["alpha", "beta"]));
        var source = new InfrastructureSystemLoadWorkloadSource(channelService);

        var result = await source.ReadAsync();

        Assert.Equal(2, result.ActiveChannels);
        Assert.Null(result.LiveChannels);
        Assert.Null(result.ChatLines);
        Assert.Null(result.EmoteMatches);
    }

    [Fact]
    public async Task PropagatesCanceledChannelLookupWithoutInventingWorkloadCounts()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(cancellation.Token)
            .Returns(Task.FromCanceled<IReadOnlyList<string>>(cancellation.Token));
        var source = new InfrastructureSystemLoadWorkloadSource(channelService);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(cancellation.Token));
    }

    [Fact]
    public async Task PropagatesChannelLookupFailureWithoutInventingZeroWorkloadCounts()
    {
        var expected = new InvalidOperationException("Injected channel lookup failure.");
        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<string>>(expected));
        var source = new InfrastructureSystemLoadWorkloadSource(channelService);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync());

        Assert.Same(expected, actual);
    }
}
