using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The flag of the tag runs (#201 T-C, spec 12.1): off unless the operator switches it on, bound from
// the Tags section the way AddEmotePurgeInfrastructure binds it.
public class EmoteTagOptionsTests
{
    [Fact]
    public void RunsEnabled_DefaultsToFalse()
    {
        Assert.False(new EmoteTagOptions().RunsEnabled);
    }

    [Fact]
    public void RunsEnabled_WithoutTheSection_StaysFalse()
    {
        var options = Bind(new Dictionary<string, string?>());

        Assert.False(options.RunsEnabled);
    }

    [Fact]
    public void RunsEnabled_BindsTrueFromConfiguration()
    {
        var options = Bind(new Dictionary<string, string?> { ["Tags:RunsEnabled"] = "true" });

        Assert.True(options.RunsEnabled);
    }

    private static EmoteTagOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new EmoteTagOptions();
        configuration.GetSection(EmoteTagOptions.SectionName).Bind(options);
        return options;
    }
}
