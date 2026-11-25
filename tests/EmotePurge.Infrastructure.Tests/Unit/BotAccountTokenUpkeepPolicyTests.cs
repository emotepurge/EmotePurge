using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The pure decisions of the token upkeep (plan #487 2.8, T5): expiry at the exact second, which states are upkept at all,
// when a verification is due (renewal swap, version change, six hours, Unauthorized), what holds 7TV calls, and the
// warning at most once per UTC day across midnight.
public class BotAccountTokenUpkeepPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(BotAccountStatus.Unverified, true)]
    [InlineData(BotAccountStatus.Valid, true)]
    [InlineData(BotAccountStatus.None, false)]
    [InlineData(BotAccountStatus.Invalid, false)]
    [InlineData(BotAccountStatus.Expired, false)]
    public void OnlyUnverifiedAndValid_AreUpkept(string status, bool upkept) =>
        Assert.Equal(upkept, BotAccountTokenUpkeepPolicy.IsUpkept(status));

    [Fact]
    public void ExpiryExactlyNow_HasExpired_OneTickBefore_HasNot()
    {
        Assert.True(BotAccountTokenUpkeepPolicy.HasExpired(Now, Now));
        Assert.True(BotAccountTokenUpkeepPolicy.HasExpired(Now.AddTicks(-1), Now));
        Assert.False(BotAccountTokenUpkeepPolicy.HasExpired(Now.AddTicks(1), Now));
        Assert.False(BotAccountTokenUpkeepPolicy.HasExpired(null, Now));
    }

    [Fact]
    public void Unverified_IsAlwaysDue_InvalidAndExpiredNever()
    {
        Assert.True(BotAccountTokenUpkeepPolicy.IsVerificationDue(BotAccountStatus.Unverified, Now, 3, 3, Now, forced: false));
        Assert.False(BotAccountTokenUpkeepPolicy.IsVerificationDue(BotAccountStatus.Invalid, null, null, 3, Now, forced: true));
        Assert.False(BotAccountTokenUpkeepPolicy.IsVerificationDue(BotAccountStatus.Expired, null, null, 3, Now, forced: true));
        Assert.False(BotAccountTokenUpkeepPolicy.IsVerificationDue(BotAccountStatus.None, null, null, 0, Now, forced: true));
    }

    [Fact]
    public void Valid_FreshlyVerifiedOfThisVersion_IsNotDue()
    {
        Assert.False(BotAccountTokenUpkeepPolicy.IsVerificationDue(
            BotAccountStatus.Valid, Now.AddHours(-5).AddMinutes(-59), 3, 3, Now, forced: false));
    }

    [Fact]
    public void Valid_SixHoursOld_IsDue()
    {
        Assert.True(BotAccountTokenUpkeepPolicy.IsVerificationDue(BotAccountStatus.Valid, Now.AddHours(-6), 3, 3, Now, forced: false));
    }

    // Revision 4 and S5-9: the swap leaves LastVerifiedAtUtc empty and LastVerifiedCredentialVersion on the old value.
    [Fact]
    public void Valid_AfterASwap_IsDue_ByEitherMarker()
    {
        Assert.True(BotAccountTokenUpkeepPolicy.IsVerificationDue(BotAccountStatus.Valid, null, 3, 4, Now, forced: false));
        Assert.True(BotAccountTokenUpkeepPolicy.IsVerificationDue(BotAccountStatus.Valid, Now, 3, 4, Now, forced: false));
        Assert.True(BotAccountTokenUpkeepPolicy.IsVerificationDue(BotAccountStatus.Valid, Now, null, 4, Now, forced: false));
    }

    [Fact]
    public void Valid_AfterAnUnauthorized_IsDue()
    {
        Assert.True(BotAccountTokenUpkeepPolicy.IsVerificationDue(BotAccountStatus.Valid, Now, 3, 3, Now, forced: true));
    }

    [Fact]
    public void KillSwitchAndARunningPause_HoldEveryCall_AnEndedPauseDoesNot()
    {
        Assert.True(BotAccountTokenUpkeepPolicy.MayCallSevenTv(false, null, Now));
        Assert.False(BotAccountTokenUpkeepPolicy.MayCallSevenTv(true, null, Now));
        Assert.False(BotAccountTokenUpkeepPolicy.MayCallSevenTv(false, Now.AddSeconds(1), Now));
        Assert.True(BotAccountTokenUpkeepPolicy.MayCallSevenTv(false, Now, Now));
    }

    [Fact]
    public void Warning_StartsSevenDaysBefore_AndOnlyWhileTheExpiryIsAhead()
    {
        Assert.False(BotAccountTokenUpkeepPolicy.ShouldWarnAboutExpiry(Now.AddDays(7).AddSeconds(1), Now, null));
        Assert.True(BotAccountTokenUpkeepPolicy.ShouldWarnAboutExpiry(Now.AddDays(7), Now, null));
        Assert.True(BotAccountTokenUpkeepPolicy.ShouldWarnAboutExpiry(Now.AddSeconds(1), Now, null));
        Assert.False(BotAccountTokenUpkeepPolicy.ShouldWarnAboutExpiry(Now, Now, null));
        Assert.False(BotAccountTokenUpkeepPolicy.ShouldWarnAboutExpiry(null, Now, null));
    }

    [Fact]
    public void Warning_OncePerUtcDay_AgainRightAfterMidnight()
    {
        var expiry = new DateTime(2026, 11, 28, 6, 0, 0, DateTimeKind.Utc);
        var lateEvening = new DateTime(2026, 11, 24, 23, 59, 59, DateTimeKind.Utc);
        var afterMidnight = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc);
        var warnedOn = DateOnly.FromDateTime(lateEvening);

        Assert.False(BotAccountTokenUpkeepPolicy.ShouldWarnAboutExpiry(expiry, lateEvening, warnedOn));
        Assert.True(BotAccountTokenUpkeepPolicy.ShouldWarnAboutExpiry(expiry, afterMidnight, warnedOn));
    }
}
