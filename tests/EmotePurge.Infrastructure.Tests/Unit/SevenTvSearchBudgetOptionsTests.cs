using EmotePurge.Infrastructure.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The startup check on <c>SevenTv:SearchBudget</c>: a misconfiguration stops the container instead
/// of silently removing the leaderboard's reserve or letting our own traffic trip the low watermark.
/// </summary>
public class SevenTvSearchBudgetOptionsTests
{
    [Fact]
    public void TheDefaults_AreValid()
    {
        new SevenTvSearchBudgetOptions().Validate();
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(50, 51)]
    [InlineData(50, 0)]
    public void TheIdentityShare_MustStayBelowTheTotal_SoTheLeaderboardKeepsAReserve(int total, int identityShare)
    {
        var options = new SevenTvSearchBudgetOptions { MaxRequestsPerWindow = total, ChannelIdentityMaxRequestsPerWindow = identityShare };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(90, 10)]
    [InlineData(50, -1)]
    public void TheLowWatermark_MustStayBelowWhatOurOwnTrafficLeaves(int total, int watermark)
    {
        // With 90 permitted, our own traffic can leave 7TV's bucket at 10: a watermark of 10 would
        // read our own full window as a stranger draining it.
        var options = new SevenTvSearchBudgetOptions
        {
            MaxRequestsPerWindow = total,
            ChannelIdentityMaxRequestsPerWindow = total - 10,
            LowWatermark = watermark,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void TheLowWatermark_JustBelowTheGap_IsValid()
    {
        new SevenTvSearchBudgetOptions { MaxRequestsPerWindow = 90, ChannelIdentityMaxRequestsPerWindow = 80, LowWatermark = 9 }.Validate();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void TheTotal_MustLeaveRoomForAShareAndStayBelowSevenTvsBucket(int total)
    {
        var options = new SevenTvSearchBudgetOptions { MaxRequestsPerWindow = total, ChannelIdentityMaxRequestsPerWindow = 1, LowWatermark = 0 };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
