using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class SlotReconcileOptionsTests
{
    [Fact]
    public void Section_IsTheDocumentedKey()
    {
        Assert.Equal("SevenTv:SlotReconcile", SlotReconcileOptions.SectionName);
    }

    [Fact]
    public void Bind_WithoutTheKey_StaysOff()
    {
        var options = Bind(new Dictionary<string, string?>());

        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Bind_ReadsTheEnabledKey(string value, bool expected)
    {
        var options = Bind(new Dictionary<string, string?> { ["SevenTv:SlotReconcile:Enabled"] = value });

        Assert.Equal(expected, options.Enabled);
    }

    private static SlotReconcileOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new SlotReconcileOptions();
        configuration.GetSection(SlotReconcileOptions.SectionName).Bind(options);
        return options;
    }
}
