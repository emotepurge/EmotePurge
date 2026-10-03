using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The pure parameter rule behind <c>GET /usage-stats/daily</c>: <c>setScope</c> and
/// <c>emoteSetId</c> are alternatives, only lowercase <c>active</c>/<c>all</c> are words, ordinal.
/// </summary>
public class EmoteSetScopeParserTests
{
    [Fact]
    public void NeitherParameter_IsTheActiveSet()
    {
        Assert.True(EmoteSetScopeParser.TryParse(null, null, out var scope));
        Assert.Equal(EmoteSetScope.ActiveSet, scope);
    }

    [Fact]
    public void ScopeActive_IsTheActiveSet()
    {
        Assert.True(EmoteSetScopeParser.TryParse("active", null, out var scope));
        Assert.Equal(EmoteSetScope.ActiveSet, scope);
    }

    [Fact]
    public void ScopeAll_IsEverySet()
    {
        Assert.True(EmoteSetScopeParser.TryParse("all", null, out var scope));
        Assert.Equal(EmoteSetScope.AllSets, scope);
    }

    [Fact]
    public void AnEmoteSetId_IsThatSet()
    {
        Assert.True(EmoteSetScopeParser.TryParse(null, "x", out var scope));
        Assert.Equal(EmoteSetScope.Set("x"), scope);
    }

    [Theory]
    [InlineData("all", "x")]
    [InlineData("active", "x")]
    [InlineData("bogus", null)]
    [InlineData("All", null)]
    [InlineData("ALL", null)]
    [InlineData("Active", null)]
    [InlineData("", null)]
    [InlineData("bogus", "x")]
    [InlineData("", "x")]
    public void Rejects(string? setScope, string? emoteSetId)
    {
        Assert.False(EmoteSetScopeParser.TryParse(setScope, emoteSetId, out _));
    }
}
