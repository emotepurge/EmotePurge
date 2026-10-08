using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="EmoteSetMembershipRule"/> — the one rule vote-session creation and the
/// tracked-channel set preview share to decide whether a set belongs to a channel.
/// </summary>
public class EmoteSetMembershipRuleTests
{
    [Fact]
    public void NormalSetInTheList_Belongs()
    {
        var list = ListOf(Set("set-a", "NORMAL"));

        Assert.True(EmoteSetMembershipRule.BelongsToChannel(list, "set-active", "set-a"));
    }

    [Fact]
    public void ActiveSetMissingFromTheList_StillBelongs()
    {
        var list = ListOf(Set("set-a", "NORMAL"));

        Assert.True(EmoteSetMembershipRule.BelongsToChannel(list, "set-active", "set-active"));
    }

    [Theory]
    [InlineData("PERSONAL")]
    [InlineData("GLOBAL")]
    [InlineData("SPECIAL")]
    public void ListedSetOfAnotherKind_DoesNotBelong(string kind)
    {
        var list = ListOf(Set("set-a", kind));

        Assert.False(EmoteSetMembershipRule.BelongsToChannel(list, "set-active", "set-a"));
    }

    [Fact]
    public void EmptyList_WithMatchingActiveId_Belongs()
    {
        Assert.True(EmoteSetMembershipRule.BelongsToChannel(ListOf(), "set-active", "set-active"));
    }

    [Fact]
    public void EmptyList_WithEmptyActiveId_DoesNotBelong()
    {
        Assert.False(EmoteSetMembershipRule.BelongsToChannel(ListOf(), "", "set-a"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("", null)]
    public void EmptyOrMissingSetId_NeverBelongs(string? activeId, string? emoteSetId)
    {
        Assert.False(EmoteSetMembershipRule.BelongsToChannel(ListOf(), activeId, emoteSetId));
    }

    [Fact]
    public void IdComparison_IsOrdinal()
    {
        var list = ListOf(Set("SetA", "NORMAL"));

        Assert.False(EmoteSetMembershipRule.BelongsToChannel(list, "SetActive", "seta"));
        Assert.False(EmoteSetMembershipRule.BelongsToChannel(list, "SetActive", "setactive"));
    }

    private static EmoteSetList ListOf(params EmoteSetSummary[] sets) => new(null, sets);

    private static EmoteSetSummary Set(string id, string kind) =>
        new(id, "Name", null, kind, kind == "PERSONAL", null);
}
