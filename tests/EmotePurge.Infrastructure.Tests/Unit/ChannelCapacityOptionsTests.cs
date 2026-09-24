using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The fail-fast half of the active-channel cap: a misconfigured Channels:MaxActiveChannels must
// stop the container at startup (AddEmotePurgeInfrastructure calls Validate() eagerly), not hand
// ChannelService a capacity of zero or less that silently rejects every join.
public class ChannelCapacityOptionsTests
{
    [Fact]
    public void Validate_WithTheDefault_DoesNotThrow()
    {
        var options = new ChannelCapacityOptions();

        options.Validate();

        Assert.Equal(80, options.MaxActiveChannels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithANonPositiveCap_Throws(int maxActiveChannels)
    {
        var options = new ChannelCapacityOptions { MaxActiveChannels = maxActiveChannels };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("Channels:MaxActiveChannels", ex.Message);
    }
}
