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

    // K6b-5: the state with the wishes on is the account's, passed through unchanged.
    [Fact]
    public async Task GetState_WishesOn_PassesTheAccountStateThrough()
    {
        var account = Account(true);
        var state = new BotAccountAvailabilityState(true, "01BOT", 7);
        account.GetStateAsync(Arg.Any<CancellationToken>()).Returns(state);

        Assert.Equal(state, await new WishBotAvailability(new WishOptions { Enabled = true }, account).GetStateAsync());
        await account.Received(1).GetStateAsync(Arg.Any<CancellationToken>());
    }

    // With the wishes off: unavailable, without asking the account (so without a read).
    [Fact]
    public async Task GetState_WishesOff_IsUnavailable_AndDoesNotAskTheAccount()
    {
        var account = Account(true);

        var state = await new WishBotAvailability(new WishOptions { Enabled = false }, account).GetStateAsync();

        Assert.Equal(BotAccountAvailabilityState.Unavailable, state);
        Assert.Empty(account.ReceivedCalls());
    }

    private static IBotAccountAvailability Account(bool available)
    {
        var account = Substitute.For<IBotAccountAvailability>();
        account.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(available);
        return account;
    }
}
