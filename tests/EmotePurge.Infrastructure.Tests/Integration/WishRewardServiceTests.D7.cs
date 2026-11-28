using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// D7 behind the channel lock at every writing reward route (#487 plan 2.9, R2-6, T6): the service checks the settings
// rule again on the settings row as it is behind the lock. In a manual channel every manager writes as before (live K6
// code); in an activated one only broadcaster, activator and a global admin, also when the activation was confirmed
// while the command waited for the channel.
public partial class WishRewardServiceTests
{
    private const string RewardActivator = "activator-id";

    public static TheoryData<string> WritingRoutes() => ["start", "confirm", "cancel", "patch", "delete"];

    [Theory]
    [MemberData(nameof(WritingRoutes))]
    public async Task D7_InAnActivatedChannel_APlainManagerIsRefused_AndNothingIsWritten(string route)
    {
        var channel = await SeedChannelAsync();
        await SeedSettingsAsync(channel, botMode: true, activatedBy: RewardActivator);
        var rewardId = await SeedRewardAsync(channel, label: "Before");
        var learning = await SeedLearningAsync(channel, withCandidate: true);

        var result = await RunRewardRouteAsync(route, channel, rewardId, learning, Actor, isAdmin: false);

        Assert.Equal(WishRewardOutcome.SettingsRestricted, result.Outcome);
        Assert.Equal(("Before", 1), (Assert.Single(await RewardsAsync(channel)).Label, (await RewardsAsync(channel)).Count));
        Assert.Equal(learning.SessionId, (await LearningRowAsync(channel))!.SessionId);
        Assert.Empty(PublishCalls());
    }

    [Theory]
    [InlineData("activator", false)]
    [InlineData("broadcaster", false)]
    [InlineData("plain", true)]
    public async Task D7_InAnActivatedChannel_TheActivatorTheBroadcasterAndAnAdminMayWrite(string caller, bool isAdmin)
    {
        var channel = await SeedChannelAsync();
        await SeedSettingsAsync(channel, botMode: true, activatedBy: RewardActivator);
        var rewardId = await SeedRewardAsync(channel, label: "Before");
        var actor = caller switch
        {
            "activator" => new AuditActor(RewardActivator, "activator"),
            "broadcaster" => new AuditActor(channel.Access.TwitchChannelId!, "broadcaster"),
            _ => Actor,
        };

        var result = await Sut().UpdateRewardAsync(channel.Access, rewardId, new UpdateWishRewardRequest("After", null, null), actor, isAdmin);

        Assert.Equal(WishRewardOutcome.Success, result.Outcome);
        Assert.Equal("After", Assert.Single(await RewardsAsync(channel)).Label);
    }

    // The regression of live K6 code: in a manual channel a plain manager changes and forgets a reward as before.
    [Fact]
    public async Task D7_InAManualChannel_APlainManagerChangesAndDeletesARewardAsBefore()
    {
        var channel = await SeedChannelAsync();
        var rewardId = await SeedRewardAsync(channel, label: "Before");

        var update = await Sut().UpdateRewardAsync(channel.Access, rewardId, new UpdateWishRewardRequest("After", 9, false), Actor);
        Assert.Equal(WishRewardOutcome.Success, update.Outcome);
        Assert.Equal(("After", 9, false), (update.Reward!.Label, update.Reward.DefaultDurationDays, update.Reward.IsActive));

        Assert.Equal(WishRewardOutcome.Success, (await Sut().DeleteRewardAsync(channel.Access, rewardId, Actor)).Outcome);
        Assert.Empty(await RewardsAsync(channel));
    }

    // R2-6: a plain manager passed the filter while the channel ran manually; the worker confirms an activation under the
    // channel lock while the PATCH waits for it. Behind the lock the service refuses, the reward stays as it was.
    [Fact]
    public async Task D7_AnActivationConfirmedWhileThePatchWaitsForTheChannel_RefusesIt()
    {
        const string tag = "wish-d7-reward-contender";
        var channel = await SeedChannelAsync();
        var rewardId = await SeedRewardAsync(channel, label: "Before");

        await using var worker = fixture.CreateDbContext();
        await using var workerTransaction = await worker.Database.BeginTransactionAsync();
        Assert.Single(await worker.LockChannelsByIdsAsync([channel.Id], CancellationToken.None));
        worker.ChannelWishSettings.Add(new ChannelWishSettings
        {
            ChannelId = channel.Id,
            BotModeEnabled = true,
            BotModeActivatedByTwitchUserId = RewardActivator
        });
        await worker.SaveChangesAsync();

        await using var contender = fixture.CreateTaggedDbContext(tag);
        var waiting = Sut(contender).UpdateRewardAsync(channel.Access, rewardId, new UpdateWishRewardRequest("After", null, null), Actor);
        await fixture.WaitUntilBlockedOnLockAsync(tag, waiting);
        await workerTransaction.CommitAsync();

        Assert.Equal(WishRewardOutcome.SettingsRestricted, (await waiting).Outcome);
        Assert.Equal("Before", Assert.Single(await RewardsAsync(channel)).Label);
    }

    private Task<WishRewardResult> RunRewardRouteAsync(
        string route, Seeded channel, long rewardId, WishRewardLearning learning, AuditActor actor, bool isAdmin, AppDbContext? db = null)
    {
        var sut = Sut(db);
        return route switch
        {
            "start" => sut.StartLearningAsync(channel.Access, actor, isAdmin),
            "confirm" => sut.ConfirmLearningAsync(
                channel.Access, new ConfirmWishRewardLearningRequest(learning.SessionId, learning.CandidateId, "Label", null), actor, isAdmin),
            "cancel" => sut.CancelLearningAsync(channel.Access, learning.SessionId, actor, isAdmin),
            "patch" => sut.UpdateRewardAsync(channel.Access, rewardId, new UpdateWishRewardRequest("After", null, null), actor, isAdmin),
            "delete" => sut.DeleteRewardAsync(channel.Access, rewardId, actor, isAdmin),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };
    }
}
