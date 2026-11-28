using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The per-channel bot state (#487 plan 2.3, T6), pure: manual without an activation, bot only while every condition
// holds, fallback as soon as one fails, also before the runner books a reason.
public class WishBotChannelStateTests
{
    private const string Bot = "01BOTACCOUNT";
    private const int Identity = 4;

    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly BotAccountAvailabilityState Up = new(true, Bot, Identity);

    [Fact]
    public void WithoutASettingsRow_OrWithoutTheActivation_IsManual_WhateverTheAccount()
    {
        Assert.Equal(WishBotChannelMode.Manual, WishBotChannelState.Of(null, Up, listed: true, activatorIsAdminById: true, Now));
        Assert.Equal(
            WishBotChannelMode.Manual,
            WishBotChannelState.Of(Activated(s => s.BotModeEnabled = false), Up, listed: true, activatorIsAdminById: false, Now));
        Assert.Equal(
            WishBotChannelMode.Manual,
            WishBotChannelState.Of(Activated(s => s.BotModeEnabled = false), BotAccountAvailabilityState.Unavailable, false, false, Now));
    }

    [Fact]
    public void Activated_Available_Listed_Consented_FreshlyProven_IsBot() =>
        Assert.Equal(WishBotChannelMode.Bot, WishBotChannelState.Of(Activated(), Up, listed: true, activatorIsAdminById: false, Now));

    // E8: the pilot list or the activator being a global admin by id covers the channel; neither is fallback.
    [Theory]
    [InlineData(true, false, WishBotChannelMode.Bot)]
    [InlineData(false, true, WishBotChannelMode.Bot)]
    [InlineData(false, false, WishBotChannelMode.Fallback)]
    public void Coverage_IsTheListOrTheActivatorAsAdminById(bool listed, bool activatorIsAdmin, WishBotChannelMode expected) =>
        Assert.Equal(expected, WishBotChannelState.Of(Activated(), Up, listed, activatorIsAdmin, Now));

    public static TheoryData<string> FallbackCauses() =>
    [
        "account unavailable",
        "suspended",
        "no consent",
        "consent to another account",
        "never checked",
        "check older than 15 minutes",
        "check against no identity",
        "check against an older identity",
        "check against a newer identity",
    ];

    [Theory]
    [MemberData(nameof(FallbackCauses))]
    public void AnyFailingCondition_IsFallback(string cause)
    {
        var account = Up;
        var settings = Activated();
        switch (cause)
        {
            case "account unavailable":
                account = Up with { IsAvailable = false };
                break;
            case "suspended":
                settings.BotSuspendedReason = "not_editor";
                break;
            case "no consent":
                settings.BotConsentSevenTvUserId = null;
                break;
            case "consent to another account":
                // S6-2: a hand PUT with another 7TV account; fallback before the runner books bot_account_changed.
                account = Up with { BotSevenTvUserId = "01OTHERBOT" };
                break;
            case "never checked":
                settings.BotEditorCheckedAtUtc = null;
                break;
            case "check older than 15 minutes":
                settings.BotEditorCheckedAtUtc = Now - WishBotChannelState.EditorCheckFreshness - TimeSpan.FromTicks(1);
                break;
            case "check against no identity":
                settings.BotEditorCheckedIdentityVersion = null;
                break;
            case "check against an older identity":
                // R2-5: the check was booked at I, a hand PUT raised the account to I+1.
                account = Up with { IdentityVersion = Identity + 1 };
                break;
            case "check against a newer identity":
                settings.BotEditorCheckedIdentityVersion = Identity + 1;
                break;
        }

        Assert.Equal(WishBotChannelMode.Fallback, WishBotChannelState.Of(settings, account, listed: true, activatorIsAdminById: true, Now));
    }

    // [F4]: older than 15 minutes is unknown; exactly 15 minutes is still fresh.
    [Fact]
    public void ACheckExactlyFifteenMinutesOld_IsStillFresh() =>
        Assert.Equal(
            WishBotChannelMode.Bot,
            WishBotChannelState.Of(Activated(s => s.BotEditorCheckedAtUtc = Now - WishBotChannelState.EditorCheckFreshness), Up, true, false, Now));

    // Revision 3: a renewal swap raises only the credential version; the identity stays, so does the state. The state
    // has no input for the credential version at all.
    [Fact]
    public void TheSameIdentityAfterASwap_StaysBot() =>
        Assert.Equal(WishBotChannelMode.Bot, WishBotChannelState.Of(Activated(), new BotAccountAvailabilityState(true, Bot, Identity), true, false, Now));

    [Fact]
    public void TheConsentComparesOrdinal() =>
        Assert.Equal(
            WishBotChannelMode.Fallback,
            WishBotChannelState.Of(Activated(s => s.BotConsentSevenTvUserId = Bot.ToLowerInvariant()), Up, true, false, Now));

    // The one account rule (K6d-7): switches, status, expiry strictly in the future, kill switch; the pause is no part.
    [Theory]
    [InlineData(true, BotAccountStatus.Valid, 60, false, true)]
    [InlineData(false, BotAccountStatus.Valid, 60, false, false)]
    [InlineData(true, BotAccountStatus.Unverified, 60, false, false)]
    [InlineData(true, BotAccountStatus.Invalid, 60, false, false)]
    [InlineData(true, BotAccountStatus.Expired, 60, false, false)]
    [InlineData(true, BotAccountStatus.Valid, 0, false, false)]
    [InlineData(true, BotAccountStatus.Valid, -1, false, false)]
    [InlineData(true, BotAccountStatus.Valid, 60, true, false)]
    public void TheAccountRule(bool switchesOn, string status, int expiresInSeconds, bool killSwitch, bool expected)
    {
        var state = BotAccountAvailabilityState.Of(switchesOn, status, Now.AddSeconds(expiresInSeconds), killSwitch, Bot, Identity, Now);

        Assert.Equal(expected, state.IsAvailable);
        Assert.Equal((Bot, Identity), (state.BotSevenTvUserId, state.IdentityVersion));
        Assert.False(BotAccountAvailabilityState.IsUsable(true, BotAccountStatus.Valid, null, false, Now));
        Assert.Equal(new BotAccountAvailabilityState(false, null, 0), BotAccountAvailabilityState.Unavailable);
    }

    private static ChannelWishSettings Activated(Action<ChannelWishSettings>? configure = null)
    {
        var settings = new ChannelWishSettings
        {
            ChannelId = "channel",
            BotModeEnabled = true,
            BotModeActivatedByTwitchUserId = "7777",
            BotConsentSevenTvUserId = Bot,
            BotEditorCheckedAtUtc = Now.AddMinutes(-1),
            BotEditorCheckedIdentityVersion = Identity,
        };
        configure?.Invoke(settings);
        return settings;
    }
}
