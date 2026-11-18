using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class WishExpiryPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    // A fact set that reaches row 7 unless a test turns something off.
    private static WishExpiryFacts Facts(
        bool botMode = true,
        string? suspended = null,
        bool owned = true,
        bool available = true,
        bool authoritative = true,
        decimal? threshold = null,
        decimal? mean = null,
        bool? gap = null) =>
        new(botMode, suspended, owned, available, authoritative, threshold, mean, gap);

    private static void AssertPresented(string reason, WishExpiryFacts facts)
    {
        var d = WishExpiryPolicy.Decide(facts);
        Assert.False(d.Queue);
        Assert.Equal(reason, d.Reason);
        Assert.Equal(reason, d.Outcome);
    }

    [Fact]
    public void Row1_BotModeOff_IsExpired_EvenWithoutOwnership() =>
        AssertPresented("expired", Facts(botMode: false, owned: false));

    [Fact]
    public void Row1_ManualModeStaysExpired_WithProvisionalStart() =>
        AssertPresented("expired", Facts(botMode: false, authoritative: false));

    [Fact]
    public void Row2_NoOwnership_IsNotBotOwned() => AssertPresented("not_bot_owned", Facts(owned: false));

    [Fact]
    public void Row2_BeatsRow6() =>
        AssertPresented("not_bot_owned", Facts(owned: false, threshold: 5m, mean: 9m, gap: false));

    [Fact]
    public void Row3_ProvisionalStart_IsUnconfirmedStart() => AssertPresented("unconfirmed_start", Facts(authoritative: false));

    [Fact]
    public void Row3_BeatsRow4_BothWhenTheBotIsDownAndWhenItIsUp()
    {
        AssertPresented("unconfirmed_start", Facts(authoritative: false, available: false));
        AssertPresented("unconfirmed_start", Facts(authoritative: false, available: true));
        AssertPresented("unconfirmed_start", Facts(authoritative: false, suspended: "x"));
    }

    [Fact]
    public void Row3_BeatsRow6() =>
        AssertPresented("unconfirmed_start", Facts(authoritative: false, threshold: 5m, mean: 9m, gap: false));

    [Fact]
    public void Row4_GloballyUnavailable_IsBotUnavailable() => AssertPresented("bot_unavailable", Facts(available: false));

    [Fact]
    public void Row4_SuspendedChannelWithOwnership_IsBotUnavailable() =>
        AssertPresented("bot_unavailable", Facts(suspended: "token_revoked"));

    [Fact]
    public void Row4_SuspendedChannelWithoutOwnership_IsNotBotOwned() =>
        AssertPresented("not_bot_owned", Facts(suspended: "token_revoked", owned: false));

    [Fact]
    public void Row4_BeatsRow5() =>
        AssertPresented("bot_unavailable", Facts(available: false, threshold: 5m, mean: 0m, gap: true));

    [Fact]
    public void Row5_GapWithThreshold_IsCoverageGap() =>
        AssertPresented("coverage_gap", Facts(threshold: 5m, mean: 0m, gap: true));

    [Fact]
    public void Row5_BeatsRow6() =>
        AssertPresented("coverage_gap", Facts(threshold: 5m, mean: 99m, gap: true));

    [Fact]
    public void Row6_MeanEqualToThreshold_IsProtected() =>
        AssertPresented("protected", Facts(threshold: 5m, mean: 5m, gap: false));

    [Fact]
    public void Row6_MeanAboveThreshold_IsProtected() =>
        AssertPresented("protected", Facts(threshold: 5m, mean: 5.01m, gap: false));

    [Fact]
    public void Row7_MeanBelowThreshold_Queues()
    {
        var d = WishExpiryPolicy.Decide(Facts(threshold: 5m, mean: 4.99m, gap: false));
        Assert.True(d.Queue);
        Assert.Null(d.Reason);
        Assert.Equal("queued", d.Outcome);
    }

    [Fact]
    public void Row7_ProtectionOff_QueuesDespiteGapAndHighUsage()
    {
        // AK25, third part: no threshold means no protection and no gap rule; the evaluation is not even needed.
        var facts = Facts(threshold: null, mean: 1000m, gap: true);
        Assert.False(WishExpiryPolicy.NeedsUsage(facts));
        Assert.True(WishExpiryPolicy.Decide(facts).Queue);
    }

    [Fact]
    public void Decide_WithoutEvaluation_WhereItIsNeeded_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => WishExpiryPolicy.Decide(Facts(threshold: 5m)));
        Assert.Throws<InvalidOperationException>(() => WishExpiryPolicy.Decide(Facts(threshold: 5m, mean: 1m)));
        Assert.Throws<InvalidOperationException>(() => WishExpiryPolicy.Decide(Facts(threshold: 5m, gap: false)));
    }

    [Fact]
    public void Decide_WithoutEvaluation_WhereItIsNotNeeded_DoesNotThrow()
    {
        WishExpiryPolicy.Decide(Facts(botMode: false, threshold: 5m));
        WishExpiryPolicy.Decide(Facts(owned: false, threshold: 5m));
        WishExpiryPolicy.Decide(Facts(authoritative: false, threshold: 5m));
        WishExpiryPolicy.Decide(Facts(available: false, threshold: 5m));
    }

    [Fact]
    public void Decide_NullFacts_Throws() => Assert.Throws<ArgumentNullException>(() => WishExpiryPolicy.Decide(null!));

    [Fact]
    public void FullCombinatorics_ProvisionalStartIsNeverQueuedAndNeverCarriesABotReason_AndNeedsUsageMatchesTheTable()
    {
        bool[] bools = [false, true];
        string?[] suspended = [null, "token_revoked"];
        decimal?[] thresholds = [null, 5m];
        decimal[] means = [0m, 5m, 9m];
        var count = 0;

        foreach (var botMode in bools)
            foreach (var susp in suspended)
                foreach (var owned in bools)
                    foreach (var available in bools)
                        foreach (var authoritative in bools)
                            foreach (var threshold in thresholds)
                                foreach (var mean in means)
                                    foreach (var gap in bools)
                                    {
                                        var facts = new WishExpiryFacts(botMode, susp, owned, available, authoritative, threshold, mean, gap);
                                        var needs = WishExpiryPolicy.NeedsUsage(facts);
                                        var expectedNeeds = botMode && owned && authoritative && available && susp is null && threshold is not null;
                                        Assert.Equal(expectedNeeds, needs);

                                        var d = WishExpiryPolicy.Decide(facts);
                                        count++;
                                        if (!authoritative)
                                        {
                                            Assert.False(d.Queue);
                                            Assert.DoesNotContain(
                                                d.Reason,
                                                new[] { "bot_unavailable", "coverage_gap", "protected" });
                                        }

                                        if (!botMode)
                                        {
                                            Assert.Equal("expired", d.Reason);
                                        }
                                    }

        Assert.Equal(2 * 2 * 2 * 2 * 2 * 2 * 3 * 2, count);
    }

    // Hold-back rule (plan 2.1).

    [Fact]
    public void HeldBack_AuthoritativeStart_Never() =>
        Assert.False(WishExpiryPolicy.IsHeldBack(true, Now.AddMinutes(-1), Now.AddHours(-1), Now));

    [Fact]
    public void HeldBack_ProvisionalWithFreshSyncAndFreshExpiry_Yes() =>
        Assert.True(WishExpiryPolicy.IsHeldBack(false, Now.AddMinutes(-1), Now.AddHours(-1), Now));

    [Fact]
    public void HeldBack_SyncOlderThan24Hours_No() =>
        Assert.False(WishExpiryPolicy.IsHeldBack(false, Now.AddMinutes(-1), Now.AddHours(-25), Now));

    [Fact]
    public void HeldBack_NoSyncAtAll_No() =>
        Assert.False(WishExpiryPolicy.IsHeldBack(false, Now.AddMinutes(-1), null, Now));

    [Fact]
    public void HeldBack_FreshSyncButExpiryOlderThan24Hours_No() =>
        Assert.False(WishExpiryPolicy.IsHeldBack(false, Now.AddHours(-25), Now.AddHours(-1), Now));

    [Fact]
    public void HeldBack_SyncExactly24HoursAgo_IsNotHeldBack() =>
        Assert.False(WishExpiryPolicy.IsHeldBack(false, Now.AddMinutes(-1), Now.AddHours(-24), Now));

    [Fact]
    public void HeldBack_SyncJustUnder24Hours_IsHeldBack() =>
        Assert.True(WishExpiryPolicy.IsHeldBack(false, Now.AddMinutes(-1), Now.AddHours(-24).AddTicks(1), Now));

    [Fact]
    public void HeldBack_ExpiryExactly24HoursAgo_IsNotHeldBack() =>
        Assert.False(WishExpiryPolicy.IsHeldBack(false, Now.AddHours(-24), Now.AddHours(-1), Now));

    [Fact]
    public void HeldBack_ExpiryJustUnder24Hours_IsHeldBack() =>
        Assert.True(WishExpiryPolicy.IsHeldBack(false, Now.AddHours(-24).AddTicks(1), Now.AddHours(-1), Now));

    // AK23: the chain from WishUsageWindow into the policy.

    private static WishExpiryDecision Chain(TimeSpan lifetime, long total, decimal threshold)
    {
        var start = Now - lifetime;
        var windowStart = WishUsageWindow.WindowStart(start, Now);
        var length = WishUsageWindow.LengthInDays(windowStart, Now);
        var mean = WishUsageWindow.MeanPerDay(total, length);
        var gap = WishUsageWindow.HasCoverageGap(new WishCoverageFacts(Now.AddDays(-400), null, true), windowStart, Now);
        return WishExpiryPolicy.Decide(Facts(threshold: threshold, mean: mean, gap: gap));
    }

    [Fact]
    public void Ak23_ThirtyDayWish_WindowIsExactlySevenDays()
    {
        var windowStart = WishUsageWindow.WindowStart(Now.AddDays(-30), Now);
        Assert.Equal(7d, WishUsageWindow.LengthInDays(windowStart, Now));
        Assert.True(Chain(TimeSpan.FromDays(30), 70, 10m).Queue is false);   // mean 10 >= 10: protected
        Assert.True(Chain(TimeSpan.FromDays(30), 69, 10m).Queue);            // mean < 10: queued
    }

    [Fact]
    public void Ak23_TwoDayWish_MeanIsTotalOverTwo()
    {
        var windowStart = WishUsageWindow.WindowStart(Now.AddDays(-2), Now);
        Assert.Equal(2d, WishUsageWindow.LengthInDays(windowStart, Now));
        Assert.Equal(5m, WishUsageWindow.MeanPerDay(10, 2d));
        Assert.Equal("protected", Chain(TimeSpan.FromDays(2), 10, 5m).Reason);
        Assert.True(Chain(TimeSpan.FromDays(2), 9, 5m).Queue);
    }

    [Fact]
    public void Ak23_OneDayWish_IsValid_TwentyThreeHoursIsAGap()
    {
        Assert.True(Chain(TimeSpan.FromDays(1), 0, 5m).Queue);
        Assert.Equal("coverage_gap", Chain(TimeSpan.FromHours(23), 1000, 5m).Reason);
    }
}
