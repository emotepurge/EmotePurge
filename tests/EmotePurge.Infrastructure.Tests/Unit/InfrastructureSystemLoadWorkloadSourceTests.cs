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
}
