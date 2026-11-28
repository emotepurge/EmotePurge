using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using NSubstitute;

namespace EmotePurge.Infrastructure.Tests.Fakes;

/// <summary>
/// The per-channel bot state readers the wish services take since #487 T6. <see cref="Unavailable"/> for every test that
/// does not care: a channel without an activation stays manual, an activated one falls back (the account is never
/// available). <see cref="Real"/> reads the real credential row through the real availability, with both switches on.
/// </summary>
public static class BotChannelStates
{
    public static IWishBotChannelStateReader Unavailable(TimeProvider? clock = null)
    {
        var availability = Substitute.For<IWishBotAvailability>();
        availability.GetStateAsync(Arg.Any<CancellationToken>()).Returns(BotAccountAvailabilityState.Unavailable);
        return new WishBotChannelStateReader(
            availability, new WishOptions { Enabled = true, PilotChannelIds = "*" }, Substitute.For<IGlobalAdminAllowlist>(), clock ?? TimeProvider.System);
    }

    public static WishBotChannelStateReader Real(
        AppDbContext db, TimeProvider clock, string pilotChannelIds = "*", IGlobalAdminAllowlist? admins = null)
    {
        var wishOptions = new WishOptions { Enabled = true, PilotChannelIds = pilotChannelIds };
        var account = new BotAccountAvailability(db, new BotAccountOptions { Enabled = true }, clock);
        return new WishBotChannelStateReader(
            new WishBotAvailability(wishOptions, account), wishOptions, admins ?? Substitute.For<IGlobalAdminAllowlist>(), clock);
    }
}
