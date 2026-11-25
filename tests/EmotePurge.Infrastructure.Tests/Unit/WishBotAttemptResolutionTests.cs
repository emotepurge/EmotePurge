using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// "Resolution without 7TV" (#487 plan 2.5a (e), K6b-11), one case per starting point: an add of unknown outcome becomes
// presented (unconfirmed) and keeps what recheck needs; an open removal becomes presented (bot_unavailable) with its
// evidence turned into a repair phase; nothing open changes nothing. The field values of the channel (K6b-8) close it.
public class WishBotAttemptResolutionTests
{
    private const string AttemptSet = "01ATTEMPTSET00000000000000";
    private const string Evidence = """{"setId":"01ATTEMPTSET00000000000000","target":{}}""";

    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Written = Now.AddMinutes(-3);
    private static readonly DateTime Earlier = Now.AddDays(-1);

    [Fact]
    public void ACommittedAddIntent_BecomesPresentedUnconfirmed_KeepingSetAnchorAndAck()
    {
        var wish = Wish(WishStatus.Approved, w =>
        {
            w.BotWriteAtUtc = Written;
            w.BotSetId = AttemptSet;
            w.BotAttemptVersion = 2;
            w.BotNextAttemptAtUtc = Now.AddSeconds(30);
            w.BotConfirmDeadlineAtUtc = Written + WishLimits.BotConfirmWindow;
        });

        Assert.True(WishBotAttemptResolution.Apply(wish, Now));

        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed, Now), (wish.Status, wish.StatusReason, wish.StatusChangedAtUtc));
        Assert.Equal((AttemptSet, Written, 2), (wish.BotSetId, wish.BotWriteAtUtc, wish.BotAttemptVersion));
        Assert.Null(wish.BotNextAttemptAtUtc);
        Assert.Null(wish.BotConfirmDeadlineAtUtc);
        Assert.Equal(2, wish.DecisionVersion);
    }

    [Fact]
    public void AnUnconfirmedWrite_BecomesPresentedUnconfirmed_KeepingTheAck()
    {
        var wish = Wish(WishStatus.Unconfirmed, w =>
        {
            w.BotWriteAtUtc = Written;
            w.BotSetId = AttemptSet;
            w.BotAddAckAtUtc = Written;
            w.BotAttemptCount = 3;
        });

        Assert.True(WishBotAttemptResolution.Apply(wish, Now));

        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), (wish.Status, wish.StatusReason));
        Assert.Equal((AttemptSet, Written, Written), (wish.BotSetId, wish.BotWriteAtUtc, wish.BotAddAckAtUtc));
    }

    [Theory]
    [InlineData(WishStatus.Active, null)]
    [InlineData(WishStatus.Presented, WishStatusReason.Protected)]
    public void AnOpenRemovalWithEvidence_BecomesPresentedBotUnavailable_AndTheEvidenceARepairPhase(string status, string? reason)
    {
        var wish = Wish(status, w =>
        {
            w.StatusReason = reason;
            w.BotOwned = true;
            w.BotSetId = AttemptSet;
            w.BotRemoveAtUtc = Written;
            w.BotAttemptVersion = 2;
            w.BotRemoveEvidence = Evidence;
            w.RemovalQueuedAtUtc = Written;
            w.RemovalQueuedVersion = 2;
        });

        Assert.True(WishBotAttemptResolution.Apply(wish, Now));

        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable, Now), (wish.Status, wish.StatusReason, wish.StatusChangedAtUtc));
        Assert.Null(wish.BotRemoveAtUtc);
        Assert.Null(wish.BotAttemptVersion);
        Assert.Null(wish.RemovalQueuedAtUtc);
        Assert.Null(wish.RemovalQueuedVersion);
        Assert.Equal((Evidence, Now), (wish.BotRemoveEvidence, wish.BotRepairAtUtc));
        // Ownership is the caller's business (DELETE ends it, the fallback run keeps it).
        Assert.True(wish.BotOwned);
        Assert.Equal(AttemptSet, wish.BotSetId);
    }

    [Fact]
    public void AnOpenRemovalWithoutEvidence_OpensNoRepairPhase()
    {
        var wish = Wish(WishStatus.Active, w => w.BotRemoveAtUtc = Written);

        Assert.True(WishBotAttemptResolution.Apply(wish, Now));

        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), (wish.Status, wish.StatusReason));
        Assert.Null(wish.BotRepairAtUtc);
    }

    [Fact]
    public void ARepairPhaseAlreadyOpen_KeepsItsStamp()
    {
        var wish = Wish(WishStatus.Active, w =>
        {
            w.BotRemoveAtUtc = Written;
            w.BotRemoveEvidence = Evidence;
            w.BotRepairAtUtc = Earlier;
        });

        WishBotAttemptResolution.Apply(wish, Now);

        Assert.Equal(Earlier, wish.BotRepairAtUtc);
    }

    [Fact]
    public void APresentedWishAlreadyBotUnavailable_KeepsItsStateClock()
    {
        var wish = Wish(WishStatus.Presented, w =>
        {
            w.StatusReason = WishStatusReason.BotUnavailable;
            w.BotRemoveAtUtc = Written;
        });

        WishBotAttemptResolution.Apply(wish, Now);

        Assert.Equal(Earlier, wish.StatusChangedAtUtc);
    }

    public static TheoryData<string, string> NothingOpen => new()
    {
        { WishStatus.Approved, "no intent" },
        { WishStatus.Active, "owned, queued" },
        { WishStatus.Presented, "repair phase only" },
        { WishStatus.Presented, "unconfirmed, waiting for recheck" },
        { WishStatus.Removed, "ended with an old write stamp" },
        { WishStatus.Kept, "plain" },
    };

    [Theory]
    [MemberData(nameof(NothingOpen))]
    public void NothingOpen_ChangesNothing(string status, string shape)
    {
        var wish = Wish(status, w =>
        {
            switch (shape)
            {
                case "owned, queued":
                    w.BotOwned = true;
                    w.BotSetId = AttemptSet;
                    w.RemovalQueuedAtUtc = Written;
                    w.RemovalQueuedVersion = 2;
                    break;
                case "repair phase only":
                    w.StatusReason = WishStatusReason.BotUnavailable;
                    w.BotRemoveEvidence = Evidence;
                    w.BotRepairAtUtc = Written;
                    break;
                case "unconfirmed, waiting for recheck":
                    w.StatusReason = WishStatusReason.Unconfirmed;
                    w.BotWriteAtUtc = Written;
                    w.BotSetId = AttemptSet;
                    break;
                case "ended with an old write stamp":
                    w.BotWriteAtUtc = Written;
                    break;
            }
        });
        var before = JsonSerializer.Serialize(wish);

        Assert.False(WishBotAttemptResolution.Apply(wish, Now));
        Assert.Equal(before, JsonSerializer.Serialize(wish));
    }

    [Fact]
    public void HasOpenAdd_IsAnApprovedIntentOrAnUnconfirmedWrite()
    {
        foreach (var status in WishStatus.All)
        {
            Assert.Equal(status is WishStatus.Approved or WishStatus.Unconfirmed, WishBotAttemptResolution.HasOpenAdd(Wish(status, w => w.BotWriteAtUtc = Written)));
            Assert.Equal(status == WishStatus.Unconfirmed, WishBotAttemptResolution.HasOpenAdd(Wish(status)));
        }
    }

    // K6b-8: the field values as the plan names them, the suspension reasons and the activation errors.
    [Fact]
    public void ChannelReasons_AreTheFieldValuesOfThePlan()
    {
        Assert.Equal(
            ["not_editor", "not_listed", "invitation_pending", "bot_account_changed"],
            WishBotChannelReasons.Suspended);
        Assert.Equal(
            ["not_editor", "invitation_not_accepted", "bot_account_changed", "invitation_accept_unresolved", "invitation_accepted_after_abort"],
            WishBotChannelReasons.ActivationErrors);
        Assert.Equal(WishStatusReason.NotEditor, WishBotChannelReasons.NotEditor);
    }

    private static Wish Wish(string status, Action<Wish>? configure = null)
    {
        var wish = new Wish
        {
            Id = 7,
            ChannelId = "channel",
            SevenTvEmoteId = "01EMOTE0000000000000000000",
            EmoteName = "Emote",
            Alias = "Alias",
            Source = WishSource.Manual,
            Status = status,
            StatusChangedAtUtc = Earlier,
            CreatedAtUtc = Earlier,
            DecisionVersion = 2,
            EndedAtUtc = WishStatus.IsEnded(status) ? Earlier : null,
        };
        configure?.Invoke(wish);
        return wish;
    }
}
