using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class WishPilotListTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ")]
    [InlineData(",,")]
    public void EmptyList_CoversNoChannel(string? value)
    {
        var list = WishPilotList.Parse(value);

        Assert.True(list.IsEmpty);
        Assert.False(list.All);
        Assert.False(list.Covers("123"));
    }

    [Fact]
    public void Star_CoversEveryChannel_ButNotAMissingId()
    {
        var list = WishPilotList.Parse("*");

        Assert.True(list.All);
        Assert.True(list.Covers("123"));
        Assert.True(list.Covers(null));
    }

    [Fact]
    public void StarWinsOverIdsBesideIt()
    {
        var list = WishPilotList.Parse("* ,123");

        Assert.True(list.All);
        Assert.True(list.Covers("999"));
    }

    [Fact]
    public void Ids_AreTrimmed_EmptyEntriesIgnored_AndOnlyListedOnesCovered()
    {
        var list = WishPilotList.Parse(" 123 ,, 456,123 ");

        Assert.False(list.IsEmpty);
        Assert.True(list.Covers("123"));
        Assert.True(list.Covers("456"));
        Assert.False(list.Covers("12"));
        Assert.False(list.Covers("999"));
        Assert.False(list.Covers(null));
        Assert.False(list.Covers(""));
    }

    [Theory]
    [InlineData("123,abc")]
    [InlineData("abc")]
    [InlineData("12 3")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("**")]
    [InlineData("*,abc")]
    [InlineData("123456789012345678901")]
    public void InvalidEntry_FailsFast_NamingTheKey_AndNeverTheValue(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WishPilotList.Parse(value));

        Assert.Contains("Wishes:PilotChannelIds", ex.Message);
        Assert.DoesNotContain("abc", ex.Message);
    }

    [Fact]
    public void WishOptions_ValidateFailsFastOnAnInvalidList_AndAcceptsAValidOne()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new WishOptions { PilotChannelIds = "123,abc" }.Validate());
        Assert.Contains("Wishes:PilotChannelIds", ex.Message);

        var valid = new WishOptions { PilotChannelIds = "123" };
        valid.Validate();
        Assert.True(valid.PilotList.Covers("123"));
        Assert.True(new WishOptions().PilotList.IsEmpty);
    }
}
