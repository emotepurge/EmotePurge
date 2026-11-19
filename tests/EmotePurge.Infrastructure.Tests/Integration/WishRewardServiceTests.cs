using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishRewardService (wish emotes K6, plan #486 T4) against real Postgres with the migrated schema: learning with
// session and candidate ids, the learned rewards, the read model and the channel binding. The races with two
// connections live in WishRewardServiceTests.Races. Every test seeds its own channel and looks at its rows only.
[Collection("Postgres")]
public partial class WishRewardServiceTests(PostgresFixture fixture)
{
    private static readonly AuditActor Actor = new("4242", "wishmod");

    private readonly IRedisPublisher _publisher = Substitute.For<IRedisPublisher>();
    private readonly HandWoundTimeProvider _clock = new();

    private DateTime Now => _clock.Now.UtcDateTime;

    [Fact]
    public void AuditActions_AreTheThreeRewardStrings()
    {
        Assert.Equal(
            ["wish.rewardAdd", "wish.rewardChange", "wish.rewardRemove"],
            [AuditActions.WishRewardAdd, AuditActions.WishRewardChange, AuditActions.WishRewardRemove]);
    }

    // ------------------------------------------------------------------------------------ start

    [Fact]
    public async Task Start_OpensATenMinuteSession_WithoutAudit_AndPublishesOnce()
    {
        var channel = await SeedChannelAsync();

        var result = await Sut().StartLearningAsync(channel.Access, Actor);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
        var learning = result.Learning!;
        Assert.NotEqual(Guid.Empty, learning.SessionId);
        Assert.Equal((Now, Now.AddMinutes(10)), (learning.StartedAtUtc, learning.ExpiresAtUtc));
        Assert.Null(learning.Candidate);

        await using var db = fixture.CreateDbContext();
        var row = await db.WishRewardLearnings.SingleAsync(l => l.ChannelId == channel.Id);
        Assert.Equal((learning.SessionId, (Guid?)null), (row.SessionId, row.CandidateId));
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        AssertPublishedOnce(channel);
    }

    [Fact]
    public async Task Start_WhileAnUnexpiredSessionLies_IsLearningActive_AndKeepsIt()
    {
        var channel = await SeedChannelAsync();
        var first = (await Sut().StartLearningAsync(channel.Access, Actor)).Learning!;
        _clock.Advance(TimeSpan.FromMinutes(9));

        var second = await Sut().StartLearningAsync(channel.Access, Actor);

        Assert.Equal(WishRewardOutcome.LearningActive, second.Outcome);
        Assert.Equal(first.SessionId, (await LearningRowAsync(channel))!.SessionId);
    }

    [Fact]
    public async Task Start_ReplacesAnExpiredSession_WithAFreshOne()
    {
        var channel = await SeedChannelAsync();
        var stale = await SeedLearningAsync(channel, withCandidate: true);
        _clock.Advance(TimeSpan.FromMinutes(10));

        var result = await Sut().StartLearningAsync(channel.Access, Actor);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
        var row = (await LearningRowAsync(channel))!;
        Assert.NotEqual(stale.SessionId, row.SessionId);
        Assert.Equal(result.Learning!.SessionId, row.SessionId);
        Assert.Null(row.CandidateId);
        Assert.Null(row.CandidateText);
    }

    [Fact]
    public async Task Start_WithTenRewards_IsRewardLimit_AndOpensNothing()
    {
        var channel = await SeedChannelAsync();
        for (var i = 0; i < WishLimits.MaxRewardsPerChannel; i++)
        {
            await SeedRewardAsync(channel);
        }

        var result = await Sut().StartLearningAsync(channel.Access, Actor);

        Assert.Equal(WishRewardOutcome.RewardLimit, result.Outcome);
        Assert.Null(await LearningRowAsync(channel));
        Assert.Empty(PublishCalls());
    }

    [Fact]
    public async Task Start_InAnUnboundChannel_IsChannelUnbound_AndWritesNothing()
    {
        var channel = await SeedChannelAsync(bound: false);

        var result = await Sut().StartLearningAsync(channel.Access, Actor);

        Assert.Equal(WishRewardOutcome.ChannelUnbound, result.Outcome);
        Assert.Null(await LearningRowAsync(channel));
        Assert.Empty(PublishCalls());
    }

    [Fact]
    public async Task Start_WithAnAccessThatIsNotConfirmed_OrConfirmedForAnotherId_IsAccessDenied()
    {
        var channel = await SeedChannelAsync();

        var unconfirmed = await Sut().StartLearningAsync(channel.Access with { IdConfirmed = false }, Actor);
        var otherId = await Sut().StartLearningAsync(channel.Access with { TwitchChannelId = "someone-else" }, Actor);

        Assert.Equal((WishRewardOutcome.AccessDenied, WishRewardOutcome.AccessDenied), (unconfirmed.Outcome, otherId.Outcome));
        Assert.Null(await LearningRowAsync(channel));
    }

    [Fact]
    public async Task Start_ForAChannelThatIsGone_IsChannelNotFound()
    {
        var result = await Sut().StartLearningAsync(new WishCaptureAccess(Guid.NewGuid().ToString(), "1", true), Actor);

        Assert.Equal(WishRewardOutcome.ChannelNotFound, result.Outcome);
    }

    [Fact]
    public async Task Start_Succeeds_WhenRedisCannotBeReached()
    {
        var channel = await SeedChannelAsync();
        _publisher.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var result = await Sut().StartLearningAsync(channel.Access, Actor);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
        Assert.NotNull(await LearningRowAsync(channel));
    }

    // ------------------------------------------------------------------------------------ confirm

    [Fact]
    public async Task Confirm_TurnsTheSeenCandidateIntoAReward_DeletesTheRow_AndAuditsOnlyTheRowId()
    {
        var channel = await SeedChannelAsync();
        var learning = await SeedLearningAsync(channel, withCandidate: true, rewardTag: "tag-a");
        _clock.Advance(TimeSpan.FromMinutes(3));

        var result = await Sut().ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(learning.SessionId, learning.CandidateId, "  Wunsch-Emote  ", 7), Actor);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
        var reward = result.Reward!;
        Assert.Equal(("Wunsch-Emote", 7, true, Now, (DateTime?)null), (reward.Label, reward.DefaultDurationDays, reward.IsActive, reward.CreatedAtUtc, reward.LastRedemptionSeenAtUtc));

        await using var db = fixture.CreateDbContext();
        var row = await db.ChannelWishRewards.SingleAsync(r => r.ChannelId == channel.Id);
        Assert.Equal((reward.Id, "tag-a"), (row.Id, row.RewardId));
        Assert.Null(await LearningRowAsync(channel));
        Assert.False(await db.Wishes.AnyAsync(w => w.ChannelId == channel.Id));

        var audit = await db.AuditLogEntries.SingleAsync(a => a.ChannelName == channel.Name);
        Assert.Equal((AuditActions.WishRewardAdd, "wishReward", reward.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), Actor.TwitchUserId),
            (audit.Action, audit.TargetType, audit.TargetId, audit.ActorTwitchUserId));
        AssertDetailsAreOnlyTheRowId(audit, reward.Id);
        AssertPublishedOnce(channel);
    }

    [Fact]
    public async Task Confirm_WithoutADuration_TakesTheChannelDefault()
    {
        var channel = await SeedChannelAsync();
        await SeedSettingsAsync(channel, defaultDurationDays: 21);
        var learning = await SeedLearningAsync(channel, withCandidate: true);

        var result = await Sut().ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(learning.SessionId, learning.CandidateId, "Label", null), Actor);

        Assert.Equal(21, result.Reward!.DefaultDurationDays);
    }

    public static TheoryData<bool, bool, string?, int?, WishRewardOutcome> InvalidConfirmBodies => new()
    {
        { false, true, "Label", 7, WishRewardOutcome.RequestInvalid },
        { true, false, "Label", 7, WishRewardOutcome.RequestInvalid },
        { true, true, null, 7, WishRewardOutcome.RequestInvalid },
        { true, true, "   ", 7, WishRewardOutcome.RequestInvalid },
        { true, true, new string('x', 51), 7, WishRewardOutcome.RequestInvalid },
        { true, true, "tab\tlabel", 7, WishRewardOutcome.RequestInvalid },
        { true, true, "Label", 0, WishRewardOutcome.DurationOutOfRange },
        { true, true, "Label", 366, WishRewardOutcome.DurationOutOfRange },
    };

    [Theory]
    [MemberData(nameof(InvalidConfirmBodies))]
    public async Task Confirm_RefusesAnInvalidBody_BeforeTouchingTheRow(
        bool withSession, bool withCandidate, string? label, int? days, WishRewardOutcome expected)
    {
        var channel = await SeedChannelAsync();
        var learning = await SeedLearningAsync(channel, withCandidate: true);

        var result = await Sut().ConfirmLearningAsync(
            channel.Access,
            new ConfirmWishRewardLearningRequest(withSession ? learning.SessionId : null, withCandidate ? learning.CandidateId : null, label, days),
            Actor);

        Assert.Equal(expected, result.Outcome);
        Assert.NotNull(await LearningRowAsync(channel));
        Assert.Empty(await RewardsAsync(channel));
    }

    [Fact]
    public async Task Confirm_AcceptsALabelOfFiftyCodePoints_AndThreeHundredSixtyFiveDays()
    {
        var channel = await SeedChannelAsync();
        var learning = await SeedLearningAsync(channel, withCandidate: true);
        var label = string.Concat(Enumerable.Repeat("😀", 50));

        var result = await Sut().ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(learning.SessionId, learning.CandidateId, label, 365), Actor);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
        Assert.Equal(label, (await RewardsAsync(channel)).Single().Label);
    }

    [Fact]
    public async Task Confirm_ASessionWithoutACandidate_IsNoCandidate()
    {
        var channel = await SeedChannelAsync();
        var learning = await SeedLearningAsync(channel, withCandidate: false);

        // The dialog opened before the candidate arrived and sends a made-up candidate id.
        var result = await Sut().ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(learning.SessionId, Guid.NewGuid(), "Label", 7), Actor);

        Assert.Equal(WishRewardOutcome.NoCandidate, result.Outcome);
        Assert.NotNull(await LearningRowAsync(channel));
    }

    [Fact]
    public async Task Confirm_WithoutAnyRow_IsNoCandidate()
    {
        var channel = await SeedChannelAsync();

        var result = await Sut().ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(Guid.NewGuid(), Guid.NewGuid(), "Label", 7), Actor);

        Assert.Equal(WishRewardOutcome.NoCandidate, result.Outcome);
    }

    // AK50: after ten minutes confirm answers "no candidate" although the row still lies, and the row is gone after.
    [Fact]
    public async Task Confirm_AfterTenMinutes_IsNoCandidate_AndDeletesTheStillLyingRow()
    {
        var channel = await SeedChannelAsync();
        var learning = await SeedLearningAsync(channel, withCandidate: true);
        _clock.Advance(TimeSpan.FromMinutes(10));

        var result = await Sut().ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(learning.SessionId, learning.CandidateId, "Label", 7), Actor);

        Assert.Equal(WishRewardOutcome.NoCandidate, result.Outcome);
        Assert.Null(await LearningRowAsync(channel));
        Assert.Empty(await RewardsAsync(channel));
        Assert.Empty(PublishCalls());
    }

    [Fact]
    public async Task Confirm_JustBeforeTheExpiry_StillSucceeds()
    {
        var channel = await SeedChannelAsync();
        var learning = await SeedLearningAsync(channel, withCandidate: true);
        _clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromMicroseconds(1));

        var result = await Sut().ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(learning.SessionId, learning.CandidateId, "Label", 7), Actor);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task Confirm_WithAnotherCandidateOfTheSameSession_IsStale()
    {
        var channel = await SeedChannelAsync();
        var learning = await SeedLearningAsync(channel, withCandidate: true);

        var result = await Sut().ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(learning.SessionId, Guid.NewGuid(), "Label", 7), Actor);

        Assert.Equal(WishRewardOutcome.LearningStale, result.Outcome);
        Assert.NotNull(await LearningRowAsync(channel));
        Assert.Empty(await RewardsAsync(channel));
    }

    // Sol-P2-4, two managers: A sees candidate 1; B cancels, starts anew, candidate 2 arrives; A confirms and
    // cancels with what A saw. Neither touches B's session, and no reward exists.
    [Fact]
    public async Task TwoManagers_TheDialogBehindARestart_GetsStaleOnConfirmAndOnCancel()
    {
        var channel = await SeedChannelAsync();
        var seenByA = await SeedLearningAsync(channel, withCandidate: true, rewardTag: "tag-1");

        Assert.Equal(WishRewardOutcome.Success, (await Sut().CancelLearningAsync(channel.Access, seenByA.SessionId, Actor)).Outcome);
        var sessionB = (await Sut().StartLearningAsync(channel.Access, Actor)).Learning!.SessionId;
        var candidate2 = await SetCandidateAsync(channel, sessionB, "tag-2");

        var confirm = await Sut().ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(seenByA.SessionId, seenByA.CandidateId, "Label", 7), Actor);
        var cancel = await Sut().CancelLearningAsync(channel.Access, seenByA.SessionId, Actor);

        Assert.Equal((WishRewardOutcome.LearningStale, WishRewardOutcome.LearningStale), (confirm.Outcome, cancel.Outcome));
        Assert.Empty(await RewardsAsync(channel));
        var row = (await LearningRowAsync(channel))!;
        Assert.Equal((sessionB, (Guid?)candidate2, "tag-2"), (row.SessionId, row.CandidateId, row.CandidateRewardId));
    }

    [Fact]
    public async Task Confirm_ARewardTheChannelAlreadyKnows_IsAlreadyKnown_AndKeepsTheRow()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, tag: "tag-known");
        var learning = await SeedLearningAsync(channel, withCandidate: true, rewardTag: "tag-known");

        var result = await Sut().ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(learning.SessionId, learning.CandidateId, "Label", 7), Actor);

        Assert.Equal(WishRewardOutcome.AlreadyKnown, result.Outcome);
        Assert.Single(await RewardsAsync(channel));
        Assert.NotNull(await LearningRowAsync(channel));
        Assert.Empty(PublishCalls());
    }

    [Fact]
    public async Task Confirm_TheEleventhReward_IsRewardLimit()
    {
        var channel = await SeedChannelAsync();
        var learning = await SeedLearningAsync(channel, withCandidate: true);
        for (var i = 0; i < WishLimits.MaxRewardsPerChannel; i++)
        {
            await SeedRewardAsync(channel);
        }

        var result = await Sut().ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(learning.SessionId, learning.CandidateId, "Label", 7), Actor);

        Assert.Equal(WishRewardOutcome.RewardLimit, result.Outcome);
        Assert.Equal(WishLimits.MaxRewardsPerChannel, (await RewardsAsync(channel)).Count);
        Assert.NotNull(await LearningRowAsync(channel));
    }

    [Fact]
    public async Task Confirm_InAnUnboundChannel_IsChannelUnbound_EvenWithAHandSeededCandidate()
    {
        var channel = await SeedChannelAsync(bound: false);
        var learning = await SeedLearningAsync(channel, withCandidate: true);

        var result = await Sut().ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(learning.SessionId, learning.CandidateId, "Label", 7), Actor);

        Assert.Equal(WishRewardOutcome.ChannelUnbound, result.Outcome);
        Assert.Empty(await RewardsAsync(channel));
    }

    // ------------------------------------------------------------------------------------ cancel

    [Fact]
    public async Task Cancel_TheOwnSession_DeletesIt_WithoutAudit_AndPublishesOnce()
    {
        var channel = await SeedChannelAsync();
        var learning = await SeedLearningAsync(channel, withCandidate: true);

        var result = await Sut().CancelLearningAsync(channel.Access, learning.SessionId, Actor);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
        Assert.Null(await LearningRowAsync(channel));
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        AssertPublishedOnce(channel);
    }

    [Fact]
    public async Task Cancel_WithNothingLying_IsIdempotentSuccess_WithoutAnEvent()
    {
        var channel = await SeedChannelAsync();

        var result = await Sut().CancelLearningAsync(channel.Access, Guid.NewGuid(), Actor);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
        Assert.Empty(PublishCalls());
    }

    [Fact]
    public async Task Cancel_WhileOnlyAnExpiredRowOfAnotherSessionLies_IsSuccess_AndClearsIt()
    {
        var channel = await SeedChannelAsync();
        await SeedLearningAsync(channel, withCandidate: true);
        _clock.Advance(TimeSpan.FromMinutes(11));

        var result = await Sut().CancelLearningAsync(channel.Access, Guid.NewGuid(), Actor);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
        Assert.Null(await LearningRowAsync(channel));
    }

    [Fact]
    public async Task Cancel_WithoutASessionId_IsRequestInvalid()
    {
        var channel = await SeedChannelAsync();
        await SeedLearningAsync(channel, withCandidate: false);

        Assert.Equal(WishRewardOutcome.RequestInvalid, (await Sut().CancelLearningAsync(channel.Access, null, Actor)).Outcome);
        Assert.Equal(WishRewardOutcome.RequestInvalid, (await Sut().CancelLearningAsync(channel.Access, Guid.Empty, Actor)).Outcome);
        Assert.NotNull(await LearningRowAsync(channel));
    }

    // ------------------------------------------------------------------------------------ read

    [Fact]
    public async Task Get_ReturnsRewardsInCreationOrder_TheCandidate_RecentGapsNewestFirst_AndTheChannelValues()
    {
        var channel = await SeedChannelAsync();
        await SeedSettingsAsync(channel, pendingExpiryDays: 5);
        var older = await SeedRewardAsync(channel, label: "Older", createdAt: Now.AddDays(-2));
        var newer = await SeedRewardAsync(channel, label: "Newer", createdAt: Now.AddDays(-1));
        var learning = await SeedLearningAsync(channel, withCandidate: true, text: "7tv.app/emotes/x please", login: "viewer");
        await SeedGapAsync(channel, Now.AddDays(-15), Now.AddDays(-14).AddSeconds(-1));
        var oldButReaching = await SeedGapAsync(channel, Now.AddDays(-14).AddHours(-1), Now.AddDays(-14).AddMinutes(1));
        var recent = await SeedGapAsync(channel, Now.AddHours(-2), Now.AddHours(-1), WishCaptureGapReason.WorkerRestart);

        var result = await Sut().GetAsync(channel.Access, "99", isGlobalAdmin: false);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
        var dto = result.Rewards!;
        Assert.True(dto.ChannelBound);
        Assert.Equal([older, newer], dto.Rewards.Select(r => r.Id));
        Assert.Equal(("Older", 30, true), (dto.Rewards[0].Label, dto.Rewards[0].DefaultDurationDays, dto.Rewards[0].IsActive));
        Assert.Equal(WishLimits.MaxRewardsPerChannel, dto.Limit);
        Assert.Equal(new WishRewardCandidateDto(learning.CandidateId!.Value, "7tv.app/emotes/x please", "viewer", learning.CandidateSeenAtUtc!.Value), dto.Learning!.Candidate);
        Assert.Equal((learning.SessionId, learning.StartedAtUtc, learning.ExpiresAtUtc), (dto.Learning.SessionId, dto.Learning.StartedAtUtc, dto.Learning.ExpiresAtUtc));
        Assert.Equal([recent.FromUtc, oldButReaching.FromUtc], dto.Gaps.Select(g => g.FromUtc));
        Assert.Equal(WishCaptureGapReason.WorkerRestart, dto.Gaps[0].Reason);
        Assert.Equal(5, dto.PendingExpiryDays);
        Assert.True(dto.CanEdit);
    }

    [Fact]
    public async Task Get_WithoutSettingsRowOrRewards_AnswersTheDefaults()
    {
        var channel = await SeedChannelAsync();

        var dto = (await Sut().GetAsync(channel.Access, "99", false)).Rewards!;

        Assert.Equal((true, 0, (WishRewardLearningDto?)null, 0, WishLimits.DefaultPendingExpiryDays),
            (dto.ChannelBound, dto.Rewards.Count, dto.Learning, dto.Gaps.Count, dto.PendingExpiryDays));
    }

    // AK66: a read never shows an expired session and deletes it.
    [Fact]
    public async Task Get_AfterTheExpiry_ShowsNoLearning_AndDeletesTheRowWithItsText()
    {
        var channel = await SeedChannelAsync();
        await SeedLearningAsync(channel, withCandidate: true);
        _clock.Advance(TimeSpan.FromMinutes(10));

        var dto = (await Sut().GetAsync(channel.Access, "99", false)).Rewards!;

        Assert.Null(dto.Learning);
        Assert.Null(await LearningRowAsync(channel));
        Assert.Empty(PublishCalls());
    }

    // Sol-P1-1, the second wall: an unbound row gives out nothing, also not a candidate seeded by hand.
    [Fact]
    public async Task Get_InAnUnboundChannel_IsNotBound_AndGivesOutNoK6Data()
    {
        var channel = await SeedChannelAsync(bound: false);
        await SeedLearningAsync(channel, withCandidate: true);
        await SeedRewardAsync(channel);
        await SeedGapAsync(channel, Now.AddHours(-2), Now.AddHours(-1));

        var dto = (await Sut().GetAsync(channel.Access, "99", false)).Rewards!;

        Assert.False(dto.ChannelBound);
        Assert.Null(dto.Learning);
        Assert.Empty(dto.Rewards);
        Assert.Empty(dto.Gaps);
    }

    [Fact]
    public async Task Get_WithAnAccessNotConfirmedForTheRowsId_IsAccessDenied()
    {
        var channel = await SeedChannelAsync();
        await SeedLearningAsync(channel, withCandidate: true);

        Assert.Equal(WishRewardOutcome.AccessDenied, (await Sut().GetAsync(channel.Access with { IdConfirmed = false }, "99", false)).Outcome);
        Assert.Equal(WishRewardOutcome.AccessDenied, (await Sut().GetAsync(channel.Access with { TwitchChannelId = "other" }, "99", false)).Outcome);
    }

    [Fact]
    public async Task Get_ForAnUnknownChannel_IsChannelNotFound()
    {
        var result = await Sut().GetAsync(new WishCaptureAccess(Guid.NewGuid().ToString(), "1", true), "99", false);

        Assert.Equal(WishRewardOutcome.ChannelNotFound, result.Outcome);
    }

    // canEdit asks the settings filter's question: anyone in a manual channel; in bot mode the broadcaster, the
    // activating manager and a global admin.
    [Theory]
    [InlineData(false, "mod", false, true)]
    [InlineData(true, "mod", false, false)]
    [InlineData(true, "broadcaster", false, true)]
    [InlineData(true, "activator", false, true)]
    [InlineData(true, "mod", true, true)]
    public async Task Get_CanEdit_FollowsTheSettingsRule(bool botMode, string caller, bool isAdmin, bool expected)
    {
        var channel = await SeedChannelAsync();
        await SeedSettingsAsync(channel, botMode: botMode, activatedBy: "activator-id");
        var callerId = caller switch
        {
            "broadcaster" => channel.Access.TwitchChannelId!,
            "activator" => "activator-id",
            _ => "some-mod-id"
        };

        var dto = (await Sut().GetAsync(channel.Access, callerId, isAdmin)).Rewards!;

        Assert.Equal(expected, dto.CanEdit);
    }

    [Fact]
    public async Task Get_CarriesTheCapturesLifeStamp()
    {
        var channel = await SeedChannelAsync();
        await using var db = fixture.CreateDbContext();
        var previous = await db.WishCaptureState.AsNoTracking().SingleOrDefaultAsync();
        var stamp = Now.AddSeconds(-30);
        try
        {
            await db.WishCaptureState.Where(s => s.Id == WishCaptureState.SingletonId).ExecuteDeleteAsync();
            Assert.Null((await Sut().GetAsync(channel.Access, "99", false)).Rewards!.CaptureAliveAtUtc);

            db.WishCaptureState.Add(new WishCaptureState { LastAliveAtUtc = stamp });
            await db.SaveChangesAsync();
            Assert.Equal(stamp, (await Sut().GetAsync(channel.Access, "99", false)).Rewards!.CaptureAliveAtUtc);
        }
        finally
        {
            await db.WishCaptureState.Where(s => s.Id == WishCaptureState.SingletonId).ExecuteDeleteAsync();
            if (previous is not null)
            {
                db.ChangeTracker.Clear();
                db.WishCaptureState.Add(previous);
                await db.SaveChangesAsync();
            }
        }
    }

    // ------------------------------------------------------------------------------------ update and delete

    [Fact]
    public async Task Update_ChangesOnlyTheSentFields_AndAuditsTheirNamesWithoutTheLabel()
    {
        var channel = await SeedChannelAsync();
        var id = await SeedRewardAsync(channel, label: "Before", days: 30);

        var result = await Sut().UpdateRewardAsync(channel.Access, id, new UpdateWishRewardRequest(" After ", null, false), Actor);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
        Assert.Equal(("After", 30, false), (result.Reward!.Label, result.Reward.DefaultDurationDays, result.Reward.IsActive));
        var stored = (await RewardsAsync(channel)).Single();
        Assert.Equal(("After", 30, false), (stored.Label, stored.DefaultDurationDays, stored.IsActive));

        await using var db = fixture.CreateDbContext();
        var audit = await db.AuditLogEntries.SingleAsync(a => a.ChannelName == channel.Name);
        Assert.Equal(AuditActions.WishRewardChange, audit.Action);
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        Assert.Equal(id, details.RootElement.GetProperty("rewardRowId").GetInt64());
        Assert.Equal(["label", "isActive"], details.RootElement.GetProperty("fields").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("After", audit.DetailsJson);
        AssertPublishedOnce(channel);
    }

    [Fact]
    public async Task Update_WithNothingChanged_WritesAndPublishesNothing()
    {
        var channel = await SeedChannelAsync();
        var id = await SeedRewardAsync(channel, label: "Same", days: 30);

        var result = await Sut().UpdateRewardAsync(channel.Access, id, new UpdateWishRewardRequest("Same", 30, true), Actor);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        Assert.Empty(PublishCalls());
    }

    [Fact]
    public async Task Update_PausesAndResumesAReward()
    {
        var channel = await SeedChannelAsync();
        var id = await SeedRewardAsync(channel);

        await Sut().UpdateRewardAsync(channel.Access, id, new UpdateWishRewardRequest(null, null, false), Actor);
        Assert.False((await RewardsAsync(channel)).Single().IsActive);
        await Sut().UpdateRewardAsync(channel.Access, id, new UpdateWishRewardRequest(null, 7, true), Actor);
        var stored = (await RewardsAsync(channel)).Single();
        Assert.Equal((true, 7), (stored.IsActive, stored.DefaultDurationDays));
    }

    [Theory]
    [InlineData("  ", null, WishRewardOutcome.RequestInvalid)]
    [InlineData(null, 0, WishRewardOutcome.DurationOutOfRange)]
    [InlineData(null, 366, WishRewardOutcome.DurationOutOfRange)]
    public async Task Update_RefusesAnInvalidField(string? label, int? days, WishRewardOutcome expected)
    {
        var channel = await SeedChannelAsync();
        var id = await SeedRewardAsync(channel, label: "Kept", days: 30);

        var result = await Sut().UpdateRewardAsync(channel.Access, id, new UpdateWishRewardRequest(label, days, null), Actor);

        Assert.Equal(expected, result.Outcome);
        var stored = (await RewardsAsync(channel)).Single();
        Assert.Equal(("Kept", 30), (stored.Label, stored.DefaultDurationDays));
    }

    [Fact]
    public async Task Update_AndDelete_OfAnUnknownOrForeignReward_AreRewardNotFound()
    {
        var channel = await SeedChannelAsync();
        var other = await SeedChannelAsync();
        var foreign = await SeedRewardAsync(other);

        Assert.Equal(WishRewardOutcome.RewardNotFound, (await Sut().UpdateRewardAsync(channel.Access, foreign, new UpdateWishRewardRequest("X", null, null), Actor)).Outcome);
        Assert.Equal(WishRewardOutcome.RewardNotFound, (await Sut().DeleteRewardAsync(channel.Access, foreign, Actor)).Outcome);
        Assert.Equal(WishRewardOutcome.RewardNotFound, (await Sut().DeleteRewardAsync(channel.Access, 987654321, Actor)).Outcome);
        Assert.Single(await RewardsAsync(other));
    }

    [Fact]
    public async Task Delete_ForgetsTheReward_AndItsWishesStayWithoutIt()
    {
        var channel = await SeedChannelAsync();
        var id = await SeedRewardAsync(channel);
        var wishId = await SeedPendingWishAsync(channel, id);

        var result = await Sut().DeleteRewardAsync(channel.Access, id, Actor);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
        Assert.Empty(await RewardsAsync(channel));
        await using var db = fixture.CreateDbContext();
        var wish = await db.Wishes.SingleAsync(w => w.Id == wishId);
        Assert.Equal((WishStatus.Pending, (long?)null), (wish.Status, wish.RewardId));
        var audit = await db.AuditLogEntries.SingleAsync(a => a.ChannelName == channel.Name);
        Assert.Equal(AuditActions.WishRewardRemove, audit.Action);
        AssertDetailsAreOnlyTheRowId(audit, id);
        AssertPublishedOnce(channel);
    }

    [Fact]
    public async Task Update_AndDelete_InAnUnboundChannel_AreChannelUnbound()
    {
        var channel = await SeedChannelAsync(bound: false);
        var id = await SeedRewardAsync(channel);

        Assert.Equal(WishRewardOutcome.ChannelUnbound, (await Sut().UpdateRewardAsync(channel.Access, id, new UpdateWishRewardRequest("X", null, null), Actor)).Outcome);
        Assert.Equal(WishRewardOutcome.ChannelUnbound, (await Sut().DeleteRewardAsync(channel.Access, id, Actor)).Outcome);
        Assert.Single(await RewardsAsync(channel));
    }

    // ------------------------------------------------------------------------------------ helpers

    private WishRewardService Sut(AppDbContext? db = null) =>
        new(db ?? fixture.CreateDbContext(), new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance), _clock);

    private List<NSubstitute.Core.ICall> PublishCalls() =>
        _publisher.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IRedisPublisher.PublishAsync)).ToList();

    private void AssertPublishedOnce(Seeded channel)
    {
        var call = Assert.Single(PublishCalls());
        Assert.Equal(LiveEvents.Channel, call.GetArguments()[0]);
        Assert.Equal(new LiveEvent(LiveEvents.WishChanged, channel.Name), LiveEvent.TryParse((string?)call.GetArguments()[1]));
    }

    private static void AssertDetailsAreOnlyTheRowId(AuditLogEntry audit, long rewardRowId)
    {
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        var property = Assert.Single(details.RootElement.EnumerateObject());
        Assert.Equal(("rewardRowId", rewardRowId), (property.Name, property.Value.GetInt64()));
    }

    private async Task<WishRewardLearning?> LearningRowAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WishRewardLearnings.AsNoTracking().SingleOrDefaultAsync(l => l.ChannelId == channel.Id);
    }

    private async Task<List<ChannelWishReward>> RewardsAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        return await db.ChannelWishRewards.AsNoTracking().Where(r => r.ChannelId == channel.Id).OrderBy(r => r.Id).ToListAsync();
    }

    private async Task<Seeded> SeedChannelAsync(bool bound = true)
    {
        var name = $"r{Guid.NewGuid():N}"[..18];
        var twitchId = bound ? Guid.NewGuid().ToString("N")[..12] : null;
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = name, TwitchChannelId = twitchId, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return new Seeded(channel.Id, name, new WishCaptureAccess(channel.Id, twitchId, IdConfirmed: bound));
    }

    private async Task SeedSettingsAsync(
        Seeded channel, int defaultDurationDays = 30, int pendingExpiryDays = 14, bool botMode = false, string? activatedBy = null)
    {
        await using var db = fixture.CreateDbContext();
        db.ChannelWishSettings.Add(new ChannelWishSettings
        {
            ChannelId = channel.Id,
            DefaultDurationDays = defaultDurationDays,
            PendingExpiryDays = pendingExpiryDays,
            BotModeEnabled = botMode,
            BotModeActivatedByTwitchUserId = activatedBy
        });
        await db.SaveChangesAsync();
    }

    // A learning row as the start (and, with a candidate, the capture: all five fields in one write) leaves it.
    private async Task<WishRewardLearning> SeedLearningAsync(
        Seeded channel, bool withCandidate, string? rewardTag = null, string text = "some text", string login = "someviewer")
    {
        var row = new WishRewardLearning
        {
            ChannelId = channel.Id,
            SessionId = Guid.NewGuid(),
            StartedAtUtc = Now,
            ExpiresAtUtc = Now + WishLimits.LearningWindow
        };
        if (withCandidate)
        {
            row.CandidateId = Guid.NewGuid();
            row.CandidateRewardId = rewardTag ?? Guid.NewGuid().ToString();
            row.CandidateText = text;
            row.CandidateLogin = login;
            row.CandidateSeenAtUtc = Now.AddSeconds(5);
        }

        await using var db = fixture.CreateDbContext();
        db.WishRewardLearnings.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    // The capture's candidate write as T5 contracts it: one conditional statement, all five fields, on the session it
    // read and without a candidate yet.
    private async Task<Guid> SetCandidateAsync(Seeded channel, Guid sessionId, string rewardTag)
    {
        var candidateId = Guid.NewGuid();
        var seenAt = Now;
        await using var db = fixture.CreateDbContext();
        var hit = await db.WishRewardLearnings
            .Where(l => l.ChannelId == channel.Id && l.SessionId == sessionId && l.CandidateId == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(l => l.CandidateId, candidateId)
                .SetProperty(l => l.CandidateRewardId, rewardTag)
                .SetProperty(l => l.CandidateText, "text")
                .SetProperty(l => l.CandidateLogin, "viewer")
                .SetProperty(l => l.CandidateSeenAtUtc, seenAt));
        Assert.Equal(1, hit);
        return candidateId;
    }

    private async Task<long> SeedRewardAsync(
        Seeded channel, string? tag = null, string label = "Reward", int days = 30, DateTime? createdAt = null)
    {
        await using var db = fixture.CreateDbContext();
        var reward = new ChannelWishReward
        {
            ChannelId = channel.Id,
            RewardId = tag ?? Guid.NewGuid().ToString(),
            Label = label,
            DefaultDurationDays = days,
            CreatedAtUtc = createdAt ?? Now
        };
        db.ChannelWishRewards.Add(reward);
        await db.SaveChangesAsync();
        return reward.Id;
    }

    private async Task<WishCaptureGap> SeedGapAsync(Seeded channel, DateTime from, DateTime to, string reason = WishCaptureGapReason.Disconnect)
    {
        await using var db = fixture.CreateDbContext();
        var gap = new WishCaptureGap { ChannelId = channel.Id, FromUtc = from, ToUtc = to, Reason = reason };
        db.WishCaptureGaps.Add(gap);
        await db.SaveChangesAsync();
        return gap;
    }

    private async Task<long> SeedPendingWishAsync(Seeded channel, long rewardRowId)
    {
        await using var db = fixture.CreateDbContext();
        var wish = new Wish
        {
            ChannelId = channel.Id,
            Status = WishStatus.Pending,
            Source = WishSource.Irc,
            DurationDays = 30,
            RewardId = rewardRowId,
            CreatedAtUtc = Now,
            StatusChangedAtUtc = Now
        };
        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }

    private sealed record Seeded(string Id, string Name, WishCaptureAccess Access);
}
