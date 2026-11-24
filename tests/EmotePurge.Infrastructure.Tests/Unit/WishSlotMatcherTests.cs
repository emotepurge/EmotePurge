using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class WishSlotMatcherTests
{
    private static readonly DateTime T = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private const string Emote = "01EMOTE";

    private static WishMatchInput Approved(string? alias = "Kappa") =>
        new(WishStatus.Approved, Emote, alias, ApprovedAtUtc: T, EndedAtUtc: null);

    private static WishMatchInput Removed(string? alias = "Kappa") =>
        new(WishStatus.RemovedExternally, Emote, alias, ApprovedAtUtc: T.AddDays(-5), EndedAtUtc: T);

    private static WishSlotCandidate Slot(string id, DateTime addedAt, string alias = "Kappa", string emote = Emote) =>
        new(id, emote, alias, addedAt);

    [Fact]
    public void Approved_single_candidate_matches()
    {
        var r = WishSlotMatcher.Match(Approved(), [Slot("s1", T.AddMinutes(5), "Other")]);
        Assert.Equal("s1", r.SlotId);
    }

    [Fact]
    public void Approved_slot_one_second_before_approval_is_no_candidate()
    {
        var r = WishSlotMatcher.Match(Approved(), [Slot("s1", T.AddSeconds(-1))]);
        Assert.Equal(WishMatchResult.ReasonNone, r.NoMatchReason);
    }

    [Fact]
    public void Approved_slot_exactly_at_approval_is_candidate()
    {
        Assert.Equal("s1", WishSlotMatcher.Match(Approved(), [Slot("s1", T)]).SlotId);
    }

    [Fact]
    public void Approved_two_candidates_with_one_alias_hit_picks_it()
    {
        var r = WishSlotMatcher.Match(Approved(), [Slot("a", T.AddHours(1), "Kappa"), Slot("b", T.AddHours(2), "Kappa2")]);
        Assert.Equal("a", r.SlotId);
    }

    [Fact]
    public void Approved_two_candidates_without_alias_hit_is_ambiguous()
    {
        var r = WishSlotMatcher.Match(Approved(), [Slot("a", T.AddHours(1), "X"), Slot("b", T.AddHours(2), "Y")]);
        Assert.Equal(WishMatchResult.ReasonAmbiguous, r.NoMatchReason);
    }

    [Fact]
    public void Approved_two_candidates_without_wish_alias_is_ambiguous()
    {
        var r = WishSlotMatcher.Match(Approved(null), [Slot("a", T.AddHours(1)), Slot("b", T.AddHours(2), "Y")]);
        Assert.Equal(WishMatchResult.ReasonAmbiguous, r.NoMatchReason);
    }

    [Fact]
    public void Approved_alias_comparison_is_ordinal()
    {
        var r = WishSlotMatcher.Match(Approved("kappa"), [Slot("a", T.AddHours(1), "Kappa"), Slot("b", T.AddHours(2), "KAPPA")]);
        Assert.False(r.IsMatch);
    }

    [Fact]
    public void Approved_other_emote_id_is_ignored()
    {
        Assert.False(WishSlotMatcher.Match(Approved(), [Slot("a", T.AddHours(1), emote: "OTHER")]).IsMatch);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void RemovedExternally_adoption_window_hours(int hoursAfter, bool expected)
    {
        var r = WishSlotMatcher.Match(Removed(), [Slot("s1", T.AddHours(hoursAfter))]);
        Assert.Equal(expected, r.IsMatch);
    }

    [Theory]
    [InlineData(-29, true)]
    [InlineData(-30, true)]
    [InlineData(-31, false)]
    public void RemovedExternally_look_back_minutes(int minutes, bool expected)
    {
        var r = WishSlotMatcher.Match(Removed(), [Slot("s1", T.AddMinutes(minutes))]);
        Assert.Equal(expected, r.IsMatch);
    }

    [Fact]
    public void RemovedExternally_two_replacements_only_one_with_alias_picks_it()
    {
        var r = WishSlotMatcher.Match(Removed(), [Slot("a", T.AddHours(1), "Other"), Slot("b", T.AddHours(2), "Kappa")]);
        Assert.Equal("b", r.SlotId);
    }

    [Fact]
    public void RemovedExternally_two_replacements_without_alias_match_nothing()
    {
        var r = WishSlotMatcher.Match(Removed(), [Slot("a", T.AddHours(1), "X"), Slot("b", T.AddHours(2), "Y")]);
        Assert.False(r.IsMatch);
    }

    [Fact]
    public void RemovedExternally_single_replacement_under_other_alias_matches_nothing()
    {
        Assert.False(WishSlotMatcher.Match(Removed(), [Slot("a", T.AddHours(1), "Other")]).IsMatch);
    }

    [Fact]
    public void RemovedExternally_alias_case_difference_matches_nothing()
    {
        Assert.False(WishSlotMatcher.Match(Removed(), [Slot("a", T.AddHours(1), "kappa")]).IsMatch);
    }

    [Fact]
    public void RemovedExternally_resumed_own_slot_matches()
    {
        Assert.Equal("own", WishSlotMatcher.Match(Removed(), [Slot("own", T.AddMinutes(1))]).SlotId);
    }

    [Fact]
    public void RemovedExternally_without_alias_matches_nothing()
    {
        Assert.False(WishSlotMatcher.Match(Removed(null), [Slot("a", T.AddHours(1))]).IsMatch);
    }

    [Fact]
    public void Other_states_have_no_rule()
    {
        var wish = new WishMatchInput(WishStatus.Active, Emote, "Kappa", T, null);
        Assert.False(WishSlotMatcher.Match(wish, [Slot("a", T.AddHours(1))]).IsMatch);
        Assert.Null(WishSlotMatcher.WindowStart(wish));
        Assert.Null(WishSlotMatcher.WindowEnd(wish));
    }

    [Fact]
    public void Windows_per_state()
    {
        Assert.Equal(T, WishSlotMatcher.WindowStart(Approved()));
        Assert.Null(WishSlotMatcher.WindowEnd(Approved()));
        Assert.Equal(T.AddMinutes(-30), WishSlotMatcher.WindowStart(Removed()));
        Assert.Equal(T.AddHours(6), WishSlotMatcher.WindowEnd(Removed()));
    }
}

public class WishScheduleTests
{
    private static readonly DateTime T = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ExpiresAt_adds_whole_days() => Assert.Equal(T.AddDays(30), WishSchedule.ExpiresAt(T, 30));

    [Fact]
    public void Probation_starts_lead_days_before_expiry()
    {
        var expires = T.AddDays(30);
        Assert.Equal(expires.AddDays(-7), WishSchedule.ProbationStartsAt(T, expires, 7));
    }

    [Fact]
    public void Probation_for_duration_shorter_than_lead_starts_at_start()
    {
        Assert.Equal(T, WishSchedule.ProbationStartsAt(T, T.AddDays(3), 7));
    }

    [Fact]
    public void Probation_for_duration_equal_to_lead_starts_at_start()
    {
        Assert.Equal(T, WishSchedule.ProbationStartsAt(T, T.AddDays(7), 7));
    }

    [Fact]
    public void Extend_counts_from_current_expiry_when_in_future()
    {
        Assert.Equal(T.AddDays(10).AddDays(30), WishSchedule.ExtendedExpiresAt(T.AddDays(10), T, 30));
    }

    [Fact]
    public void Extend_counts_from_now_when_expired()
    {
        Assert.Equal(T.AddDays(30), WishSchedule.ExtendedExpiresAt(T.AddDays(-2), T, 30));
    }

    [Fact]
    public void Shift_moves_start_and_expiry_by_same_difference()
    {
        var shifted = WishSchedule.Shift(T, T.AddDays(30), T.AddHours(-5));
        Assert.Equal(T.AddHours(-5), shifted.StartsAtUtc);
        Assert.Equal(T.AddDays(30).AddHours(-5), shifted.ExpiresAtUtc);
    }

    [Fact]
    public void Shift_forward_works()
    {
        var shifted = WishSchedule.Shift(T, T.AddDays(1), T.AddHours(2));
        Assert.Equal(T.AddDays(1).AddHours(2), shifted.ExpiresAtUtc);
    }
}

public class WishUsageWindowTests
{
    private static readonly DateTime T = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private static WishCoverageFacts Ok(DateTime trackedSince) => new(trackedSince, null, true);

    [Fact]
    public void Window_start_is_start_for_young_wish() => Assert.Equal(T.AddDays(-2), WishUsageWindow.WindowStart(T.AddDays(-2), T));

    [Fact]
    public void Window_start_is_seven_days_back_for_old_wish() => Assert.Equal(T.AddDays(-7), WishUsageWindow.WindowStart(T.AddDays(-20), T));

    [Fact]
    public void Length_supports_fractions() => Assert.Equal(1.5, WishUsageWindow.LengthInDays(T.AddHours(-36), T), 6);

    [Fact]
    public void Length_is_never_negative() => Assert.Equal(0d, WishUsageWindow.LengthInDays(T.AddHours(1), T));

    [Fact]
    public void Mean_per_day_divides_total_by_length() => Assert.Equal(10m, WishUsageWindow.MeanPerDay(70, 7d));

    [Fact]
    public void Mean_for_empty_window_is_zero() => Assert.Equal(0m, WishUsageWindow.MeanPerDay(5, 0d));

    [Fact]
    public void Mean_is_withheld_one_second_below_24_hours() =>
        Assert.True(WishUsageWindow.IsMeanWithheld(T.AddHours(-24).AddSeconds(1), T));

    [Fact]
    public void Mean_is_shown_at_exactly_24_hours() =>
        Assert.False(WishUsageWindow.IsMeanWithheld(T.AddHours(-24), T));

    [Fact]
    public void Mean_is_withheld_for_an_empty_window() =>
        Assert.True(WishUsageWindow.IsMeanWithheld(T, T));

    [Fact]
    public void Exactly_24_hours_is_no_gap() =>
        Assert.False(WishUsageWindow.HasCoverageGap(Ok(T.AddDays(-30)), T.AddHours(-24), T));

    [Fact]
    public void Under_24_hours_is_gap() =>
        Assert.True(WishUsageWindow.HasCoverageGap(Ok(T.AddDays(-30)), T.AddHours(-23), T));

    [Fact]
    public void Tracking_started_after_window_start_is_gap() =>
        Assert.True(WishUsageWindow.HasCoverageGap(Ok(T.AddDays(-1)), T.AddDays(-3), T));

    [Fact]
    public void Tracking_started_exactly_at_window_start_is_no_gap() =>
        Assert.False(WishUsageWindow.HasCoverageGap(Ok(T.AddDays(-3)), T.AddDays(-3), T));

    [Fact]
    public void Deactivated_channel_is_gap() =>
        Assert.True(WishUsageWindow.HasCoverageGap(new WishCoverageFacts(T.AddDays(-30), T.AddDays(-1), true), T.AddDays(-3), T));

    [Fact]
    public void Inactive_bot_is_gap() =>
        Assert.True(WishUsageWindow.HasCoverageGap(new WishCoverageFacts(T.AddDays(-30), null, false), T.AddDays(-3), T));

    [Fact]
    public void Rank_ties_share_the_better_rank()
    {
        long[] totals = [50, 30, 30, 10];
        Assert.Equal(1, WishUsageWindow.Rank(50, totals));
        Assert.Equal(2, WishUsageWindow.Rank(30, totals));
        Assert.Equal(4, WishUsageWindow.Rank(10, totals));
    }
}
