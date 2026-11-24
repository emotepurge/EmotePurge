using EmotePurge.Core.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class BotAccountOptionsTests
{
    [Fact]
    public void Defaults_AreOff_AndValidate()
    {
        var options = new BotAccountOptions();

        options.Validate();

        Assert.Equal("BotAccount", BotAccountOptions.SectionName);
        Assert.False(options.Enabled);
        Assert.Equal(2, options.WriteDelaySeconds);
        Assert.Equal(300, options.EditorVisibilitySeconds);
        Assert.False(options.Renewal.Enabled);
        Assert.Equal(21, options.Renewal.LeadDays);
        Assert.Equal(26, options.Renewal.SilenceAlertHours);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(61)]
    public void WriteDelaySeconds_OutsideZeroToSixtyThrows_NamingTheKey(int value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new BotAccountOptions { WriteDelaySeconds = value }.Validate());

        Assert.Contains("BotAccount:WriteDelaySeconds", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void WriteDelaySeconds_AtTheBoundsIsValid(int value) => new BotAccountOptions { WriteDelaySeconds = value }.Validate();

    [Theory]
    [InlineData(59)]
    [InlineData(1801)]
    public void EditorVisibilitySeconds_OutsideSixtyToEighteenHundredThrows_NamingTheKey(int value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new BotAccountOptions { EditorVisibilitySeconds = value }.Validate());

        Assert.Contains("BotAccount:EditorVisibilitySeconds", ex.Message);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(1800)]
    public void EditorVisibilitySeconds_AtTheBoundsIsValid(int value) => new BotAccountOptions { EditorVisibilitySeconds = value }.Validate();

    [Theory]
    [InlineData(6)]
    [InlineData(29)]
    public void LeadDays_OutsideSevenToTwentyEightThrows_NamingTheKey(int value)
    {
        var options = new BotAccountOptions();
        options.Renewal.LeadDays = value;

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("BotAccount:Renewal:LeadDays", ex.Message);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(28)]
    public void LeadDays_AtTheBoundsIsValid(int value)
    {
        var options = new BotAccountOptions();
        options.Renewal.LeadDays = value;

        options.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(169)]
    public void SilenceAlertHours_OutsideOneToOneHundredSixtyEightThrows_NamingTheKey(int value)
    {
        var options = new BotAccountOptions();
        options.Renewal.SilenceAlertHours = value;

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("BotAccount:Renewal:SilenceAlertHours", ex.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(168)]
    public void SilenceAlertHours_AtTheBoundsIsValid(int value)
    {
        var options = new BotAccountOptions();
        options.Renewal.SilenceAlertHours = value;

        options.Validate();
    }

    // The way ServiceCollectionExtensions binds, from the keys the compose files set.
    [Fact]
    public void BindsFromTheNestedSection_AndIgnoresTheTokenKeys()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotAccount:Enabled"] = "true",
                ["BotAccount:WriteDelaySeconds"] = "5",
                ["BotAccount:Renewal:Enabled"] = "true",
                ["BotAccount:Renewal:LeadDays"] = "14",
                ["BotAccount:Renewal:SilenceAlertHours"] = "48",
                ["BotAccount:TokenPublicKey"] = "not-an-option",
            })
            .Build();

        var options = new BotAccountOptions();
        configuration.GetSection(BotAccountOptions.SectionName).Bind(options);
        options.Validate();

        Assert.True(options.Enabled);
        Assert.Equal(5, options.WriteDelaySeconds);
        Assert.True(options.Renewal.Enabled);
        Assert.Equal((14, 48), (options.Renewal.LeadDays, options.Renewal.SilenceAlertHours));
    }
}
