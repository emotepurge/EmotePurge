using EmotePurge.Infrastructure.ChatLogArchive;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class ChatLogBackfillOptionsTests
{
    [Fact]
    public void Defaults_AreTheSpecifiedOnesAndTheFeatureIsOff()
    {
        var options = new ChatLogBackfillOptions();

        options.Validate();

        Assert.False(options.Enabled);
        Assert.Equal(10, options.RequestDelaySeconds);
        Assert.Equal(256, options.MaxBlockMegabytes);
        Assert.Equal(900, options.MaxRetryAfterSeconds);
        Assert.Equal(3, options.TransportRetries);
        Assert.Equal(10, options.MaxConsecutivePauses);
        Assert.Equal(60, options.IdlePollSeconds);
        Assert.Equal(2, options.CancelPollSeconds);
    }

    [Theory]
    [InlineData(0, 256, 3, "ChatLogBackfill:RequestDelaySeconds")]
    [InlineData(10, 0, 3, "ChatLogBackfill:MaxBlockMegabytes")]
    [InlineData(10, 256, 0, "ChatLogBackfill:TransportRetries")]
    [InlineData(-1, 256, 3, "ChatLogBackfill:RequestDelaySeconds")]
    public void Validate_RejectsUnusableValues_NamingTheKey(int delay, int megabytes, int retries, string key)
    {
        var options = new ChatLogBackfillOptions { RequestDelaySeconds = delay, MaxBlockMegabytes = megabytes, TransportRetries = retries };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void ArchiveOptions_DefaultToCyexAndTheRangeLimits()
    {
        var options = new ChatLogArchiveOptions();

        Assert.Equal("https://logs.cyex.app/", options.BaseUrl);
        Assert.Equal(TimeSpan.FromMinutes(5), options.RangeBodyTimeout);
        Assert.Equal(16384, options.MaxLineBytes);
    }
}
