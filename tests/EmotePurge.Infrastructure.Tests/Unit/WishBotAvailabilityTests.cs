using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The wishes' view of the bot account (#487, plan 2.3, T4): only the link with Wishes:Enabled. The account's own
// answer is pinned in BotAccountAvailabilityTests.
public class WishBotAvailabilityTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task WishesOn_DelegatesToTheAccount(bool wishesEnabled, bool accountAvailable, bool expected)
    {
        var account = Account(accountAvailable);

        var available = await new WishBotAvailability(new WishOptions { Enabled = wishesEnabled }, account).IsAvailableAsync();

        Assert.Equal(expected, available);
        await account.Received(1).IsAvailableAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WishesOff_IsNeverAvailable_AndDoesNotAskTheAccount(bool accountAvailable)
    {
        var account = Account(accountAvailable);

        var available = await new WishBotAvailability(new WishOptions { Enabled = false }, account).IsAvailableAsync();

        Assert.False(available);
        Assert.Empty(account.ReceivedCalls());
    }

    private static IBotAccountAvailability Account(bool available)
    {
        var account = Substitute.For<IBotAccountAvailability>();
        account.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(available);
        return account;
    }
}
