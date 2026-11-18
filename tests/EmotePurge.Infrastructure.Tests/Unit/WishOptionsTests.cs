using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class WishOptionsTests
{
    [Fact]
    public void Defaults_AreOff_AndValidate()
    {
        var options = new WishOptions();

        options.Validate();

        Assert.False(options.Enabled);
        Assert.Equal("Wishes", WishOptions.SectionName);
    }

    [Fact]
    public void ExpiryInterval_DefaultsToFifteenMinutes()
    {
        Assert.Equal(15, new WishOptions().ExpiryIntervalMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    [InlineData(-5)]
    public void ExpiryInterval_OutsideOneToFourteenHundredFortyThrows_NamingTheKey(int minutes)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new WishOptions { ExpiryIntervalMinutes = minutes }.Validate());
        Assert.Contains("Wishes:ExpiryIntervalMinutes", ex.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1440)]
    public void ExpiryInterval_AtTheBoundsIsValid(int minutes)
    {
        new WishOptions { ExpiryIntervalMinutes = minutes }.Validate();
    }

    [Fact]
    public void WishDue_IsTheAuditActionString_AndTheGraceIsOneDay()
    {
        Assert.Equal("wish.due", AuditActions.WishDue);
        Assert.Equal(TimeSpan.FromHours(24), WishLimits.ProvisionalStartGrace);
        Assert.Contains("unconfirmed_start", WishStatusReason.Presented);
    }

    [Fact]
    public void BindsFromTheWishesSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Wishes:Enabled"] = "true" })
            .Build();

        var options = configuration.GetSection(WishOptions.SectionName).Get<WishOptions>()!;
        options.Validate();

        Assert.True(options.Enabled);
    }

    [Fact]
    public void Limits_HoldTheSpecifiedBoundaries()
    {
        Assert.Equal((1, 365), (WishLimits.DurationDaysMin, WishLimits.DurationDaysMax));
        Assert.Equal((1, 30), (WishLimits.ProbationLeadDaysMin, WishLimits.ProbationLeadDaysMax));
        Assert.Equal(0m, WishLimits.ProtectionThresholdExclusiveMin);
        Assert.Equal(100, WishLimits.AliasMaxLength);
        Assert.Equal(100, WishLimits.EmoteNameMaxLength);
        Assert.Equal(25, WishLimits.RequesterLoginMaxLength);
        Assert.Equal(32, WishLimits.SevenTvEmoteIdMaxLength);
        Assert.Equal(TimeSpan.FromHours(6), WishLimits.AdoptionWindow);
        Assert.Equal(TimeSpan.FromMinutes(30), WishLimits.ReturnLookBack);
    }

    [Fact]
    public void ChannelSettings_DefaultToThirtyDaysAndSevenDaysLead_AndNoProtection()
    {
        var settings = new ChannelWishSettings();

        Assert.Equal(30, settings.DefaultDurationDays);
        Assert.Equal(7, settings.ProbationLeadDays);
        Assert.Null(settings.ProtectionThresholdPerDay);
        Assert.False(settings.BotModeEnabled);
        Assert.Equal(30, WishLimits.DefaultDurationDays);
        Assert.Equal(7, WishLimits.DefaultProbationLeadDays);
    }

    [Fact]
    public void RetentionConstants_AreTheSpecifiedPeriods()
    {
        Assert.Equal(TimeSpan.FromDays(30), RetentionPolicy.WishRequester);
        Assert.Equal(TimeSpan.FromDays(365), RetentionPolicy.EndedWish);
        Assert.Equal(TimeSpan.FromDays(120), RetentionPolicy.WishNeverActiveRequesterCap);
    }

    [Fact]
    public void AuditActions_AreTheTenWishStrings()
    {
        // The spec's nine plus wish.update for the PATCH (see DECISIONS "Wish emotes, child 1", (j)).
        Assert.Equal(
            ["wish.create", "wish.adopt", "wish.extend", "wish.keep", "wish.remove", "wish.close", "wish.retry", "wish.readd", "wish.settings", "wish.update"],
            [
                AuditActions.WishCreate, AuditActions.WishAdopt, AuditActions.WishExtend, AuditActions.WishKeep,
                AuditActions.WishRemove, AuditActions.WishClose, AuditActions.WishRetry, AuditActions.WishReAdd,
                AuditActions.WishSettings, AuditActions.WishUpdate,
            ]);
    }

    [Fact]
    public void WishChanged_IsAChannelEventOnly()
    {
        Assert.Equal("wish.changed", LiveEvents.WishChanged);
        Assert.Contains(LiveEvents.WishChanged, LiveEvents.ChannelTypes);
        Assert.DoesNotContain(LiveEvents.WishChanged, LiveEvents.AdminTypes);
    }
}
