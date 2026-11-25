using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class ChannelJoinHealthOptionsTests
{
    [Fact]
    public void Default_IsThreeHundredSecondsAndValid()
    {
        var options = new ChannelJoinHealthOptions();

        options.Validate();

        Assert.Equal(300, options.GraceSeconds);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(3600)]
    public void Validate_AcceptsTheBounds(int seconds) => new ChannelJoinHealthOptions { GraceSeconds = seconds }.Validate();

    [Theory]
    [InlineData(59)]
    [InlineData(3601)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_RejectsOutsideTheRange(int seconds)
    {
        var ex = Assert.Throws<InvalidOperationException>(new ChannelJoinHealthOptions { GraceSeconds = seconds }.Validate);

        Assert.Contains("Channels:JoinHealth:GraceSeconds", ex.Message);
    }

    [Fact]
    public void BindsFromTheConfigurationSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Channels:JoinHealth:GraceSeconds"] = "120" })
            .Build();
        var options = new ChannelJoinHealthOptions();

        configuration.GetSection(ChannelJoinHealthOptions.SectionName).Bind(options);

        Assert.Equal(120, options.GraceSeconds);
    }
}
