using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The pure parts of step 3 and step 4 (#487 plan 2.5b, 2.8; T8b): the mapping of an editor entry (Revision 6d K6d-2),
// the verdict per activated channel, and the fallback of one wish.
public class WishBotChannelReviewTests
{
    private const string Bot = "01BOTACCOUNT";
    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------------------------ BotAccountEditorEntry.Of

    // Plan 2.5b and K6d-2: three results, plus the raw ACCEPTED for resolving an acceptance attempt.
    [Theory]
    [InlineData("ACCEPTED", true, BotAccountEditorStatus.Editor, true)]
    [InlineData("ACCEPTED", false, BotAccountEditorStatus.NotEditor, true)]
    [InlineData("PENDING", true, BotAccountEditorStatus.Invited, false)]
    [InlineData("PENDING", false, BotAccountEditorStatus.NotEditor, false)]
    [InlineData("REJECTED", true, BotAccountEditorStatus.NotEditor, false)]
    [InlineData("SOMETHING_NEW", true, BotAccountEditorStatus.NotEditor, false)]
    [InlineData(null, true, BotAccountEditorStatus.NotEditor, false)]
    [InlineData("accepted", true, BotAccountEditorStatus.NotEditor, false)]
    public void AnEntry_MapsToItsStatus(string? state, bool manage, BotAccountEditorStatus status, bool accepted)
    {
        var entry = BotAccountEditorEntry.Of(state, manage, "01OWNER");

        Assert.Equal(status, entry.Status);
        Assert.Equal(accepted, entry.Accepted);
        Assert.Equal("01OWNER", entry.OwnerSevenTvUserId);
    }

    [Fact]
    public void AnAvailableRead_AnswersAbsentForAChannelWithoutAnEntry_AndNullWhenUnavailable()
    {
        var read = BotAccountEditorRead.Available(
            Bot, 4, Now, new Dictionary<string, BotAccountEditorEntry> { ["111"] = BotAccountEditorEntry.Of("ACCEPTED", true, "01OWNER") });

        Assert.Equal(BotAccountEditorStatus.Editor, read.For("111")!.Value.Status);
        Assert.Equal(BotAccountEditorEntry.Absent, read.For("222"));
        Assert.Equal(BotAccountEditorEntry.Absent, read.For(null));
        Assert.Null(BotAccountEditorRead.Unavailable(Bot, 4, Now).For("111"));
    }

    // ------------------------------------------------------------------------------------ WishBotChannelReview.Decide

    [Fact]
    public void NotCovered_IsNotListed_WhateverElse() =>
        Assert.Equal(
            WishBotChannelReasons.NotListed,
            WishBotChannelReview.Decide(false, Settings(consent: "01OTHER"), Bot, Editor())!.Value.SuspendedReason);

    // S6-2: another bot account than the consented one suspends, also where the new account is an editor already.
    [Theory]
    [InlineData("01OTHER")]
    [InlineData(null)]
    public void ConsentToAnotherAccount_IsBotAccountChanged_EvenWhereTheNewOneIsEditor(string? consent) =>
        Assert.Equal(
            WishBotChannelReasons.BotAccountChanged,
            WishBotChannelReview.Decide(true, Settings(consent), Bot, Editor())!.Value.SuspendedReason);

    [Fact]
    public void AnEditor_Resumes_AndProvesTheEditor()
    {
        var verdict = WishBotChannelReview.Decide(true, Settings(), Bot, Editor())!.Value;

        Assert.Null(verdict.SuspendedReason);
        Assert.True(verdict.EditorProven);
    }

    // T8b: an invitation counts as not_editor until the invitation step (T8c) accepts it.
    [Theory]
    [InlineData("REJECTED", true)]
    [InlineData("ACCEPTED", false)]
    [InlineData("PENDING", true)]
    public void AnythingButEditor_IsNotEditor(string state, bool manage)
    {
        var verdict = WishBotChannelReview.Decide(true, Settings(), Bot, BotAccountEditorEntry.Of(state, manage, "01OWNER"))!.Value;

        Assert.Equal(WishBotChannelReasons.NotEditor, verdict.SuspendedReason);
        Assert.False(verdict.EditorProven);
    }

    [Fact]
    public void NoEntryInAnAvailableRead_IsNotEditor() =>
        Assert.Equal(
            WishBotChannelReasons.NotEditor,
            WishBotChannelReview.Decide(true, Settings(), Bot, BotAccountEditorEntry.Absent)!.Value.SuspendedReason);

    // No read, no knowledge: a 7TV outage suspends nothing and resumes nothing.
    [Fact]
    public void WithoutAUsableRead_NothingIsDecided() =>
        Assert.Null(WishBotChannelReview.Decide(true, Settings(), Bot, editor: null));

    // ------------------------------------------------------------------------------------ WishBotFallback.ApplyTo

    [Theory]
    [InlineData(WishStatus.Unconfirmed, false)]
    [InlineData(WishStatus.Approved, true)]
    public void AnOpenAdd_IsPresentedAsUnconfirmed(string status, bool withIntent)
    {
        var wish = Wish(status);
        wish.BotWriteAtUtc = withIntent || status == WishStatus.Unconfirmed ? Now.AddMinutes(-5) : null;

        Assert.True(WishBotFallback.ApplyTo(wish, Now));
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed, Now), (wish.Status, wish.StatusReason, wish.StatusChangedAtUtc));
    }

    [Fact]
    public void ApprovedWithoutAnIntent_Stays()
    {
        var wish = Wish(WishStatus.Approved);

        Assert.False(WishBotFallback.ApplyTo(wish, Now));
        Assert.Equal(WishStatus.Approved, wish.Status);
    }

    [Fact]
    public void AnActiveWishWithAQueuedRemoval_IsPresentedAsBotUnavailable_AndTheQueueIsCleared()
    {
        var wish = Wish(WishStatus.Active);
        wish.RemovalQueuedAtUtc = Now.AddHours(-1);
        wish.RemovalQueuedVersion = wish.DecisionVersion;

        Assert.True(WishBotFallback.ApplyTo(wish, Now));
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), (wish.Status, wish.StatusReason));
        Assert.Null(wish.RemovalQueuedAtUtc);
        Assert.Null(wish.RemovalQueuedVersion);
        Assert.Equal(3, wish.DecisionVersion);
    }

    [Fact]
    public void APresentedWishWithAnOrder_KeepsItsStateAndReason_TheOrderGoes()
    {
        var wish = Wish(WishStatus.Presented);
        wish.StatusReason = WishStatusReason.Protected;
        wish.RemovalQueuedAtUtc = Now.AddHours(-1);
        wish.RemovalQueuedVersion = wish.DecisionVersion;
        var changedAt = wish.StatusChangedAtUtc;

        Assert.True(WishBotFallback.ApplyTo(wish, Now));
        Assert.Equal((WishStatus.Presented, WishStatusReason.Protected, changedAt), (wish.Status, wish.StatusReason, wish.StatusChangedAtUtc));
        Assert.Null(wish.RemovalQueuedAtUtc);
    }

    // 2.5a (e) through WishBotAttemptResolution: attempt and queue go, the evidence stays as a repair phase.
    [Fact]
    public void AnOpenRemovalAttempt_IsResolvedWithout7Tv_TheEvidenceStaysAsARepairPhase()
    {
        var wish = Wish(WishStatus.Active);
        wish.RemovalQueuedAtUtc = Now.AddHours(-1);
        wish.RemovalQueuedVersion = wish.DecisionVersion;
        wish.BotRemoveAtUtc = Now.AddMinutes(-2);
        wish.BotAttemptVersion = wish.DecisionVersion;
        wish.BotRemoveEvidence = "{}";

        Assert.True(WishBotFallback.ApplyTo(wish, Now));
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), (wish.Status, wish.StatusReason));
        Assert.Null(wish.BotRemoveAtUtc);
        Assert.Null(wish.RemovalQueuedAtUtc);
        Assert.Equal("{}", wish.BotRemoveEvidence);
        Assert.Equal(Now, wish.BotRepairAtUtc);
    }

    // An open repair phase alone is left as it is, so is a wish without a bot action.
    [Fact]
    public void ARepairPhaseAlone_OrNothingOpen_ChangesNothing()
    {
        var repair = Wish(WishStatus.Presented);
        repair.BotRepairAtUtc = Now.AddMinutes(-3);
        repair.BotRemoveEvidence = "{}";

        Assert.False(WishBotFallback.ApplyTo(repair, Now));
        Assert.False(WishBotFallback.ApplyTo(Wish(WishStatus.Active), Now));
        Assert.Equal(Now.AddMinutes(-3), repair.BotRepairAtUtc);
    }

    private static BotAccountEditorEntry Editor() => BotAccountEditorEntry.Of(SevenTvEditorForEntry.Accepted, true, "01OWNER");

    private static ChannelWishSettings Settings(string? consent = Bot) => new()
    {
        ChannelId = "c1",
        BotModeEnabled = true,
        BotConsentSevenTvUserId = consent,
    };

    private static Wish Wish(string status) => new()
    {
        Id = 1,
        ChannelId = "c1",
        Status = status,
        StatusChangedAtUtc = Now.AddDays(-1),
        DecisionVersion = 3,
    };
}
