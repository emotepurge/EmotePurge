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

    // ------------------------------------------------------------------ chat capture (K6, plan #486 2.8)

    [Fact]
    public void Capture_DefaultsToOff_WithThreeOpenWishesPerViewer()
    {
        var options = new WishOptions();

        options.Validate();

        Assert.False(options.Capture.Enabled);
        Assert.Equal(3, options.Capture.MaxPendingPerRequester);
        Assert.False(options.CaptureActive);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void CaptureActive_NeedsBothSwitches(bool enabled, bool captureEnabled, bool expected)
    {
        var options = new WishOptions { Enabled = enabled };
        options.Capture.Enabled = captureEnabled;

        Assert.Equal(expected, options.CaptureActive);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(-1)]
    public void MaxPendingPerRequester_OutsideOneToFiftyThrows_NamingTheKey(int value)
    {
        var options = new WishOptions();
        options.Capture.MaxPendingPerRequester = value;

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Wishes:Capture:MaxPendingPerRequester", ex.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void MaxPendingPerRequester_AtTheBoundsIsValid(int value)
    {
        var options = new WishOptions();
        options.Capture.MaxPendingPerRequester = value;

        options.Validate();
    }

    // The way ServiceCollectionExtensions binds (Bind into an instance), from the keys the compose files set
    // (Wishes__Capture__Enabled is Wishes:Capture:Enabled once the environment provider has read it).
    [Fact]
    public void Capture_BindsFromTheNestedSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Wishes:Enabled"] = "true",
                ["Wishes:Capture:Enabled"] = "true",
                ["Wishes:Capture:MaxPendingPerRequester"] = "7",
            })
            .Build();

        var options = new WishOptions();
        configuration.GetSection(WishOptions.SectionName).Bind(options);
        options.Validate();

        Assert.True(options.CaptureActive);
        Assert.Equal(7, options.Capture.MaxPendingPerRequester);
    }

    [Fact]
    public void CaptureLimits_HoldThePlannedValues()
    {
        Assert.Equal((10, 200), (WishLimits.MaxRewardsPerChannel, WishLimits.MaxPendingPerChannel));
        Assert.Equal((1, 50, 3), (WishLimits.MaxPendingPerRequesterMin, WishLimits.MaxPendingPerRequesterMax, WishLimits.DefaultMaxPendingPerRequester));
        Assert.Equal((1, 90, 14), (WishLimits.PendingExpiryDaysMin, WishLimits.PendingExpiryDaysMax, WishLimits.DefaultPendingExpiryDays));
        Assert.Equal((50, 500, 100, 64, 64), (
            WishLimits.RewardLabelMaxLength, WishLimits.RequestTextMaxLength, WishLimits.RequestedNameMaxLength,
            WishLimits.RewardIdMaxLength, WishLimits.IrcMessageIdMaxLength));
        Assert.Equal(TimeSpan.FromMinutes(10), WishLimits.LearningWindow);
        Assert.Equal(TimeSpan.FromDays(7), WishLimits.CaptureReceiptRetention);
        Assert.Equal(TimeSpan.FromDays(14), WishLimits.CaptureGapDisplayWindow);
        Assert.Equal(TimeSpan.FromSeconds(60), WishLimits.MinCaptureGap);
        Assert.Equal(["link", "name", "none"], WishMatchKind.All);
        Assert.Equal(["disconnect", "worker_restart", "not_joined"], WishCaptureGapReason.All);
    }

    // ------------------------------------------------------------------ pilot list (#487, spec E8)

    [Fact]
    public void PilotChannelIds_DefaultsToEmpty_AndBindsFromTheSection()
    {
        Assert.Null(new WishOptions().PilotChannelIds);
        Assert.True(new WishOptions().PilotList.IsEmpty);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Wishes:PilotChannelIds"] = "123, 456" })
            .Build();
        var options = new WishOptions();
        configuration.GetSection(WishOptions.SectionName).Bind(options);
        options.Validate();

        Assert.True(options.PilotList.Covers("456"));
        Assert.False(options.PilotList.Covers("789"));
    }

    [Fact]
    public void PilotChannelIds_InvalidEntryThrowsOnValidate_NamingTheKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new WishOptions { PilotChannelIds = "123,abc" }.Validate());

        Assert.Contains("Wishes:PilotChannelIds", ex.Message);
    }

    [Fact]
    public void WishChanged_IsAChannelEventOnly()
    {
        Assert.Equal("wish.changed", LiveEvents.WishChanged);
        Assert.Contains(LiveEvents.WishChanged, LiveEvents.ChannelTypes);
        Assert.DoesNotContain(LiveEvents.WishChanged, LiveEvents.AdminTypes);
    }
}
