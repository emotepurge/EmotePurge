using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The transition matrix of the wish lifecycle (spec section 5). The expectations are written out as a
/// table below, independent of the implementation: every combination of state, reason, action and
/// context that the table does not list must be refused.
/// </summary>
public class WishTransitionsTests
{
    private enum Cond { Always, NoSlot, Permanent, NotPermanent, IrcAuto, ReasonStood, NoReasonStood, HasExpiry }

    private sealed record Expected(string From, string[]? Reasons, string Action, Cond When, string To);

    private const string Pen = WishStatus.Pending;
    private const string App = WishStatus.Approved;
    private const string Unc = WishStatus.Unconfirmed;
    private const string Fai = WishStatus.Failed;
    private const string Act = WishStatus.Active;
    private const string Pre = WishStatus.Presented;
    private const string Rem = WishStatus.Removed;
    private const string Ext = WishStatus.RemovedExternally;

    private static readonly string[] Adoptable = ["unconfirmed", "already_in_set"];
    private static readonly string[] Recheckable = ["unconfirmed"];
    private static readonly string[] Unlinkable = ["unconfirmed_start"];

    private static readonly Expected[] Table =
    [
        // Adopt with wishId (also permanent -> kept).
        new(Pen, null, "adopt", Cond.NotPermanent, "active"),
        new(Pen, null, "adopt", Cond.Permanent, "kept"),
        new(App, null, "adopt", Cond.NotPermanent, "active"),
        new(App, null, "adopt", Cond.Permanent, "kept"),
        new(Fai, null, "adopt", Cond.NotPermanent, "active"),
        new(Fai, null, "adopt", Cond.Permanent, "kept"),
        new(Pre, Adoptable, "adopt", Cond.NotPermanent, "active"),
        new(Pre, Adoptable, "adopt", Cond.Permanent, "kept"),

        // Reconcile link.
        new(Pen, null, "link", Cond.NotPermanent, "active"),
        new(Pen, null, "link", Cond.Permanent, "kept"),
        new(App, null, "link", Cond.NotPermanent, "active"),
        new(App, null, "link", Cond.Permanent, "kept"),

        // Return from an external removal.
        new(Ext, null, "return", Cond.ReasonStood, "presented"),
        new(Ext, null, "return", Cond.NoReasonStood, "active"),

        // PATCH (edit): a self-transition in the states before 7TV was written to.
        new(Pen, null, "edit", Cond.Always, "pending"),
        new(App, null, "edit", Cond.Always, "approved"),
        new(Fai, null, "edit", Cond.Always, "failed"),

        // Pending (K6).
        new(Pen, null, "approve", Cond.Always, "approved"),
        new(Pen, null, "reject", Cond.Always, "rejected"),
        new(Pen, null, "expire", Cond.Always, "expired"),
        new(Pen, null, "merge_into_running", Cond.Always, "merged"),

        // Channel merge: the running states only.
        new(App, null, "merge_channels", Cond.Always, "merged"),
        new(Unc, null, "merge_channels", Cond.Always, "merged"),
        new(Act, null, "merge_channels", Cond.Always, "merged"),
        new(Pre, null, "merge_channels", Cond.Always, "merged"),

        // Approved, bot (K4), close.
        new(App, null, "bot_write", Cond.Always, "unconfirmed"),
        new(App, null, "bot_reject", Cond.Always, "failed"),
        new(App, null, "bot_already_in_set", Cond.Always, "presented"),
        new(App, null, "close", Cond.Always, "closed"),

        // Unconfirmed (K4). A permanent wish ends in kept, like the reconcile's link (#487 plan 2.1 point 1).
        new(Unc, null, "bot_confirm", Cond.NotPermanent, "active"),
        new(Unc, null, "bot_confirm", Cond.Permanent, "kept"),
        new(Unc, null, "bot_unconfirmed", Cond.Always, "presented"),

        // Failed.
        new(Fai, null, "retry", Cond.Always, "approved"),
        new(Fai, null, "close", Cond.Always, "closed"),

        // Active.
        new(Act, null, "extend", Cond.HasExpiry, "active"),
        new(Act, null, "unlink", Cond.IrcAuto, "pending"),
        new(Act, null, "keep", Cond.Always, "kept"),
        new(Act, null, "remove", Cond.Always, "removed"),
        new(Act, null, "present", Cond.Always, "presented"),
        new(Act, null, "external_removal", Cond.Always, "removed_externally"),

        // Presented.
        new(Pre, null, "extend", Cond.HasExpiry, "active"),
        new(Pre, null, "keep", Cond.Always, "kept"),
        new(Pre, null, "remove", Cond.Always, "removed"),
        new(Pre, null, "close", Cond.NoSlot, "closed"),
        new(Pre, Recheckable, "recheck", Cond.Always, "unconfirmed"),
        new(Pre, Unlinkable, "unlink", Cond.IrcAuto, "pending"),
        new(Pre, null, "external_removal", Cond.Always, "removed_externally"),

        // Removed states.
        new(Ext, null, "remove", Cond.Always, "removed"),
        new(Rem, null, "readd", Cond.Always, "approved"),
        new(Ext, null, "readd", Cond.Always, "approved"),
    ];

    private static readonly (string Action, string To)[] NewRows =
    [
        ("create", "approved"),
        ("adopt_new", "active"),
        ("capture", "pending"),
    ];

    private static readonly string[] AllStates =
    [
        "pending", "approved", "unconfirmed", "failed", "active", "presented", "rejected", "expired",
        "removed", "removed_externally", "kept", "closed", "merged",
    ];

    private static readonly string[] AllActions =
    [
        "create", "adopt_new", "capture", "edit", "adopt", "extend", "keep", "close", "retry", "readd", "remove", "recheck",
        "approve", "reject", "unlink", "link", "return", "external_removal", "present", "expire",
        "merge_into_running", "merge_channels", "bot_write", "bot_reject", "bot_already_in_set", "bot_confirm",
        "bot_unconfirmed",
    ];

    private static readonly string?[] AllReasons =
    [
        null, "expired", "protected", "coverage_gap", "not_bot_owned", "ownership_lost", "unconfirmed",
        "bot_unavailable", "unconfirmed_start", "already_in_set", "repair_review", "alias_conflict", "set_full", "emote_gone",
        "not_editor", "unknown", "ambiguous_match", "already_running", "unparsed",
    ];

    [Fact]
    public void Constants_MatchTheSpecVocabulary()
    {
        Assert.Equal(AllStates, WishStatus.All);
        Assert.Equal(AllActions.OrderBy(a => a), WishAction.All.OrderBy(a => a));
        Assert.Equal(["approved", "unconfirmed", "active", "presented"], WishStatus.Running.OrderBy(s => AllStates.ToList().IndexOf(s)));
        Assert.Equal(
            ["rejected", "expired", "removed", "removed_externally", "kept", "closed", "merged"],
            WishStatus.Ended.OrderBy(s => AllStates.ToList().IndexOf(s)));
        Assert.Equal(["irc", "manual", "adopted"], [WishSource.Irc, WishSource.Manual, WishSource.Adopted]);
        Assert.Equal(["auto", "adopted", "bot"], [WishLinkOrigin.Auto, WishLinkOrigin.Adopted, WishLinkOrigin.Bot]);
    }

    [Fact]
    public void Matrix_AllowsExactlyTheListedTransitions_AndNothingElse()
    {
        var contexts = new List<WishTransitionContext>();
        foreach (var hasSlot in new[] { false, true })
            foreach (var permanent in new[] { false, true })
                foreach (var stood in new[] { false, true })
                    foreach (var ircAuto in new[] { false, true })
                        foreach (var hasExpiry in new[] { false, true })
                        {
                            contexts.Add(new WishTransitionContext(hasSlot, permanent, stood, ircAuto, hasExpiry));
                        }

        var checkedCombinations = 0;
        foreach (var state in AllStates)
            foreach (var reason in AllReasons)
                foreach (var action in AllActions)
                    foreach (var context in contexts)
                    {
                        var matches = Table
                            .Where(row => row.From == state
                                && row.Action == action
                                && (row.Reasons is null || (reason is not null && row.Reasons.Contains(reason)))
                                && Holds(row.When, context))
                            .Select(row => row.To)
                            .ToList();

                        // The table itself must never be ambiguous.
                        Assert.True(matches.Count <= 1, $"Ambiguous expectation for {state}/{reason}/{action}");

                        var expected = matches.SingleOrDefault();
                        var actual = WishTransitions.Target(state, reason, action, context);

                        Assert.True(
                            expected == actual,
                            $"{state} / {reason ?? "-"} / {action} / {context}: expected {expected ?? "not allowed"}, got {actual ?? "not allowed"}");
                        Assert.Equal(expected is not null, WishTransitions.IsAllowed(state, reason, action, context));
                        checkedCombinations++;
                    }

        Assert.Equal(13 * 19 * 27 * 32, checkedCombinations);
    }

    [Fact]
    public void EveryAction_HasAtLeastOneRow_SoNoChildHasToTouchTheTableAgain()
    {
        foreach (var action in AllActions.Where(a => NewRows.All(n => n.Action != a)))
        {
            Assert.Contains(Table, row => row.Action == action);
        }
    }

    [Theory]
    [InlineData("create", "approved")]
    [InlineData("adopt_new", "active")]
    [InlineData("capture", "pending")]
    public void NewWishes_StartInTheirState(string action, string expected)
    {
        Assert.Equal(expected, WishTransitions.TargetForNew(action));
        Assert.Contains((action, expected), NewRows);
    }

    [Fact]
    public void ActionsOnExistingWishes_AreNotNewWishActions()
    {
        foreach (var action in AllActions.Where(a => NewRows.All(n => n.Action != a)))
        {
            Assert.Null(WishTransitions.TargetForNew(action));
        }

        // And the creation actions never apply to an existing wish.
        foreach (var (action, _) in NewRows)
        {
            foreach (var state in AllStates)
            {
                Assert.Null(WishTransitions.Target(state, null, action));
            }
        }
    }

    [Fact]
    public void EndedStates_OnlyLeaveThroughReAddOrTheExternalRemovalReturn()
    {
        foreach (var state in WishStatus.Ended)
            foreach (var action in AllActions)
            {
                var allowed = WishTransitions.IsAllowed(state, null, action, new WishTransitionContext(true, true, true, true, true));
                var expectedAllowed =
                    (state is "removed" or "removed_externally" && action == "readd")
                    || (state == "removed_externally" && action is "return" or "remove");
                Assert.Equal(expectedAllowed, allowed);
            }
    }

    private static bool Holds(Cond cond, WishTransitionContext c) => cond switch
    {
        Cond.Always => true,
        Cond.NoSlot => !c.HasSlot,
        Cond.Permanent => c.Permanent,
        Cond.NotPermanent => !c.Permanent,
        Cond.IrcAuto => c.IrcAutoLinked,
        Cond.ReasonStood => c.HadPresentedReason,
        Cond.NoReasonStood => !c.HadPresentedReason,
        Cond.HasExpiry => c.HasExpiry,
        _ => throw new ArgumentOutOfRangeException(nameof(cond)),
    };
}
