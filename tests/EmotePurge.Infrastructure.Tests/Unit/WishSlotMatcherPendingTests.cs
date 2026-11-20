using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// K6 (plan #486 T3, 2.5): the pending rule, the name match, the earlier approved window of a captured wish, and
// the hint table of DecidePending.
public class WishSlotMatcherPendingTests
{
    private static readonly DateTime T = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = T.AddDays(14);
    private const string Emote = "01EMOTE";
    private const string Other = "01OTHER";

    private static WishMatchInput Link(string? alias = null) =>
        new(WishStatus.Pending, Emote, alias, null, null, CreatedAtUtc: T, MatchKind: WishMatchKind.Link, PendingWindowEnd: End, Source: WishSource.Irc);

    private static WishMatchInput Name(string requested) =>
        new(WishStatus.Pending, "", "", null, null, CreatedAtUtc: T, MatchKind: WishMatchKind.Name, RequestedName: requested, PendingWindowEnd: End, Source: WishSource.Irc);

    private static WishMatchInput None() =>
        new(WishStatus.Pending, "", "", null, null, CreatedAtUtc: T, MatchKind: WishMatchKind.None, PendingWindowEnd: End, Source: WishSource.Irc);

    private static WishSlotCandidate Slot(string id, DateTime addedAt, string alias = "Kappa", string emote = Emote) =>
        new(id, emote, alias, addedAt);

    private static WishPendingDecision Decide(
        WishMatchInput wish, WishSlotCandidate[] free, WishSlotCandidate[]? held = null, params string[] running) =>
        WishSlotMatcher.DecidePending(wish, free, [.. free, .. held ?? []], id => running.Contains(id));

    // ------------------------------------------------------------------------------------ window

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(60, true)]
    public void Link_window_starts_at_the_redemption_without_tolerance(int seconds, bool hit) =>
        Assert.Equal(hit, WishSlotMatcher.Match(Link(), [Slot("s", T.AddSeconds(seconds))]).IsMatch);

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Link_window_ends_at_the_nominal_expiry(int seconds, bool hit) =>
        Assert.Equal(hit, WishSlotMatcher.Match(Link(), [Slot("s", End.AddSeconds(seconds))]).IsMatch);

    [Fact]
    public void Window_functions_answer_for_pending()
    {
        Assert.Equal((T, End), (WishSlotMatcher.WindowStart(Link()), WishSlotMatcher.WindowEnd(Link())));
        Assert.Equal(T.AddDays(14), WishSlotMatcher.PendingWindowEndFor(T, 14));
    }

    // ------------------------------------------------------------------------------------ by id

    [Fact]
    public void Link_two_slots_of_the_emote_without_alias_is_ambiguous() =>
        Assert.Equal(
            WishMatchResult.ReasonAmbiguous,
            WishSlotMatcher.Match(Link(), [Slot("a", T.AddHours(1), "A"), Slot("b", T.AddHours(2), "B")]).NoMatchReason);

    [Fact]
    public void Link_ignores_slots_of_other_emotes() =>
        Assert.Equal("a", WishSlotMatcher.Match(Link(), [Slot("a", T.AddHours(1)), Slot("b", T.AddHours(1), emote: Other)]).SlotId);

    // ------------------------------------------------------------------------------------ by name

    [Fact]
    public void Name_matches_the_slots_alias_exactly() =>
        Assert.Equal("a", WishSlotMatcher.Match(Name("catJAM"), [Slot("a", T.AddHours(1), "catJAM"), Slot("b", T.AddHours(1), "other", Other)]).SlotId);

    [Fact]
    public void Name_falls_back_to_case_insensitive_when_there_is_no_ordinal_hit() =>
        Assert.Equal("a", WishSlotMatcher.Match(Name("CATJAM"), [Slot("a", T.AddHours(1), "catJAM")]).SlotId);

    [Fact]
    public void Name_ordinal_hit_wins_over_a_case_variant() =>
        Assert.Equal("b", WishSlotMatcher.Match(Name("kappa"), [Slot("a", T.AddHours(1), "Kappa"), Slot("b", T.AddHours(1), "kappa", Other)]).SlotId);

    [Fact]
    public void Name_two_case_variants_without_ordinal_hit_are_ambiguous() =>
        Assert.Equal(
            WishMatchResult.ReasonAmbiguous,
            WishSlotMatcher.Match(Name("KAPPA"), [Slot("a", T.AddHours(1), "Kappa"), Slot("b", T.AddHours(1), "kappa", Other)]).NoMatchReason);

    [Fact]
    public void Name_outside_the_window_is_no_match() =>
        Assert.False(WishSlotMatcher.Match(Name("Kappa"), [Slot("a", T.AddSeconds(-1))]).IsMatch);

    [Fact]
    public void None_is_never_a_match() =>
        Assert.False(WishSlotMatcher.Match(None(), [Slot("a", T.AddHours(1))]).IsMatch);

    // ------------------------------------------------------------------------------------ approved from the capture (W11)

    [Theory]
    [InlineData(WishSource.Irc, false, true)]
    [InlineData(WishSource.Manual, false, false)]
    [InlineData(WishSource.Adopted, false, false)]
    [InlineData(WishSource.Irc, true, false)]
    public void Approved_capture_never_active_starts_at_the_redemption(string source, bool wasActive, bool hit)
    {
        var wish = new WishMatchInput(
            WishStatus.Approved, Emote, "Kappa", ApprovedAtUtc: T.AddHours(1), EndedAtUtc: null, CreatedAtUtc: T,
            FirstActivatedAtUtc: wasActive ? T.AddMinutes(1) : null, Source: source);

        Assert.Equal(hit, WishSlotMatcher.Match(wish, [Slot("s", T.AddMinutes(30))]).IsMatch);
    }

    // ------------------------------------------------------------------------------------ hints (DecidePending)

    [Fact]
    public void Hint_unparsed_for_none() =>
        Assert.Equal(new WishPendingDecision(null, WishStatusReason.Unparsed), Decide(None(), [Slot("a", T.AddHours(1))]));

    [Fact]
    public void Hint_none_and_a_link_for_one_free_candidate() =>
        Assert.Equal(new WishPendingDecision("a", null), Decide(Link(), [Slot("a", T.AddHours(1))]));

    [Fact]
    public void Hint_ambiguous_match_for_several_candidates() =>
        Assert.Equal(
            new WishPendingDecision(null, WishStatusReason.AmbiguousMatch),
            Decide(Link(), [Slot("a", T.AddHours(1), "A"), Slot("b", T.AddHours(1), "B")], running: Emote));

    [Fact]
    public void Hint_already_running_when_the_one_candidates_emote_runs()
    {
        Assert.Equal(new WishPendingDecision(null, WishStatusReason.AlreadyRunning), Decide(Link(), [Slot("a", T.AddHours(1))], running: Emote));
        Assert.Equal(
            new WishPendingDecision(null, WishStatusReason.AlreadyRunning),
            Decide(Name("Kappa"), [Slot("a", T.AddHours(1))], running: Emote));
    }

    [Fact]
    public void Hint_already_running_when_the_wish_knows_the_id_and_it_runs_without_a_candidate() =>
        Assert.Equal(new WishPendingDecision(null, WishStatusReason.AlreadyRunning), Decide(Link(), [], running: Emote));

    [Fact]
    public void Hint_already_running_for_a_name_whose_slot_in_the_window_is_held_by_the_running_wish() =>
        Assert.Equal(
            new WishPendingDecision(null, WishStatusReason.AlreadyRunning),
            Decide(Name("Kappa"), [], held: [Slot("held", T.AddHours(1))], running: Emote));

    // Sol R2: a viewer asks by the exact alias of an emote whose wish already runs. Its slot predates the redemption,
    // so it is no candidate; the hint still says "running" (approve books the extension), never "in the set".
    [Fact]
    public void Hint_already_running_for_a_name_whose_older_slot_is_held_by_the_running_wish() =>
        Assert.Equal(
            new WishPendingDecision(null, WishStatusReason.AlreadyRunning),
            Decide(Name("Kappa"), [], held: [Slot("held", T.AddDays(-3))], running: Emote));

    // The hint never makes the older slot eligible: free, of a running emote, before the window, it is no link.
    [Fact]
    public void Hint_already_running_for_a_name_whose_older_free_slot_belongs_to_a_running_emote() =>
        Assert.Equal(
            new WishPendingDecision(null, WishStatusReason.AlreadyRunning),
            Decide(Name("kappa"), [Slot("old", T.AddSeconds(-1))], running: Emote));

    // Only an unambiguous older alias counts: the ordinal hit decides, and two case variants without one stay "in set".
    [Fact]
    public void Hint_for_an_older_name_follows_the_name_rule_ordinal_first_and_ambiguity_is_no_running_hint()
    {
        WishSlotCandidate[] variants = [Slot("a", T.AddDays(-3), "Kappa", Emote), Slot("b", T.AddDays(-2), "KAPPA", Other)];
        Assert.Equal(new WishPendingDecision(null, WishStatusReason.AlreadyRunning), Decide(Name("Kappa"), variants, running: Emote));
        Assert.Equal(new WishPendingDecision(null, WishStatusReason.AlreadyInSet), Decide(Name("Kappa"), variants, running: Other));
        Assert.Equal(new WishPendingDecision(null, WishStatusReason.AlreadyInSet), Decide(Name("kappa"), variants, running: Emote));
    }

    [Fact]
    public void Hint_already_in_set_for_a_slot_before_the_redemption()
    {
        Assert.Equal(new WishPendingDecision(null, WishStatusReason.AlreadyInSet), Decide(Link(), [Slot("a", T.AddSeconds(-1))]));
        Assert.Equal(new WishPendingDecision(null, WishStatusReason.AlreadyInSet), Decide(Name("KAPPA"), [Slot("a", T.AddDays(-3))]));
    }

    [Fact]
    public void Hint_null_without_any_matching_slot() =>
        Assert.Equal(new WishPendingDecision(null, null), Decide(Name("Kappa"), [Slot("a", T.AddDays(-3), "Other", Other)]));

    [Fact]
    public void Hint_null_for_a_slot_after_the_window() =>
        Assert.Equal(new WishPendingDecision(null, null), Decide(Link(), [Slot("a", End.AddSeconds(1))]));
}
