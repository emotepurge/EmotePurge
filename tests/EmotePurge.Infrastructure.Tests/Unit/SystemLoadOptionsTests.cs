using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class SystemLoadOptionsTests
{
    [Fact]
    public void DefaultsEnableThirtySecondSamplingAndFiveMinuteRollupGrace()
    {
        var options = new SystemLoadOptions();

        options.Validate();
        Assert.True(options.Enabled);
        Assert.Equal(30, options.IntervalSeconds);
        Assert.Equal(5, options.RollupGraceMinutes);
    }

    [Fact]
    public void ValidateAcceptsTheMaximumSamplingInterval()
    {
        var options = new SystemLoadOptions { IntervalSeconds = SystemLoadOptions.MaximumIntervalSeconds };

        options.Validate();
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(3601, 5)]
    [InlineData(30, -1)]
    public void ValidateRejectsAnInvalidIntervalOrGracePeriod(int intervalSeconds, int graceMinutes)
    {
        var options = new SystemLoadOptions
        {
            IntervalSeconds = intervalSeconds,
            RollupGraceMinutes = graceMinutes,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
