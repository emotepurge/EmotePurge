using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Worker;
using EmotePurge.Worker.WishCapture;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Wish emotes K6 (plan #486 T9, AK68, Sol-P2-7, Sol-R2-P1-2, Sol-P2-10): the identity merge carries the chat-capture
// rows of the loser (rewards, learning row, gaps, receipts, slot exclusions through their wish) to the survivor, keeps
// exactly one owner of an IRC message id, and needs no wish switch. Every row is built by hand against the migrated
// schema, so each CHECK and unique index of AddWishCapture judges the end state.
public partial class ChannelIdentityServiceTests
{
    // AK68: a loser without emotes, two rewards, an open wish and a running learning session; a survivor with one of
    // the two rewards. Afterwards the survivor has both, each Twitch reward id once, the wish points at an existing
    // reward row, the loser row is gone, and the next redemption in the survivor becomes a wish.
    [Fact]
    public async Task Merge_CarriesRewardsLearningGapsAndReceipts_AndTheNextRedemptionBecomesAWish()
    {
        var pair = await SeedMergePairAsync();
        var now = DateTime.UtcNow;
        long survivorRewardA, loserRewardA, loserRewardB, wishOnA;
        var sessionId = Guid.NewGuid();
        await using (var seed = fixture.CreateDbContext())
        {
            var sA = CaptureReward(pair.SurvivorId, "tag-a", "Survivor A", lastSeen: now.AddDays(-3), days: 21);
            var lA = CaptureReward(pair.LoserId, "tag-a", "Loser A", lastSeen: now.AddDays(-1), days: 7);
            var lB = CaptureReward(pair.LoserId, "tag-b", "Loser B", lastSeen: null, days: 9);
            seed.ChannelWishRewards.AddRange(sA, lA, lB);
            await seed.SaveChangesAsync();
            var wish = OpenWish(pair.LoserId, lA.Id, "msg-loser-1", requester: "5001");
            seed.Wishes.Add(wish);
            seed.WishRewardLearnings.Add(new WishRewardLearning
            {
                ChannelId = pair.LoserId,
                SessionId = sessionId,
                StartedAtUtc = now.AddMinutes(-1),
                ExpiresAtUtc = now.AddMinutes(9),
                CandidateId = Guid.NewGuid(),
                CandidateRewardId = "tag-c",
                CandidateText = "learn me",
                CandidateLogin = "learner",
                CandidateSeenAtUtc = now.AddSeconds(-30)
            });
            seed.WishCaptureGaps.AddRange(
                Gap(pair.LoserId, now.AddDays(-2), WishCaptureGapReason.Disconnect),
                Gap(pair.LoserId, now.AddDays(-1), WishCaptureGapReason.NotJoined),
                Gap(pair.SurvivorId, now.AddDays(-1), WishCaptureGapReason.WorkerRestart));
            seed.WishCaptureReceipts.AddRange(
                Receipt(pair.LoserId, "msg-loser-1", now.AddMinutes(-5)),
                Receipt(pair.LoserId, "msg-shared", now.AddMinutes(-4)),
                Receipt(pair.SurvivorId, "msg-shared", now.AddMinutes(-6)));
            await seed.SaveChangesAsync();
            (survivorRewardA, loserRewardA, loserRewardB, wishOnA) = (sA.Id, lA.Id, lB.Id, wish.Id);
        }

        await using var db = fixture.CreateDbContext();
        var summary = await CreateHarness(db, [pair.Identity]).Service.ReconcileActiveChannelsAsync();

        Assert.Equal(1, summary?.Merged);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Channels.AnyAsync(c => c.Id == pair.LoserId));

        var rewards = await verify.ChannelWishRewards.AsNoTracking().Where(r => r.ChannelId == pair.SurvivorId).OrderBy(r => r.RewardId).ToListAsync();
        Assert.Equal(["tag-a", "tag-b"], rewards.Select(r => r.RewardId));
        var a = rewards[0];
        // The survivor's row wins (label, duration), "last seen" is the later of the two.
        Assert.Equal((survivorRewardA, "Survivor A", 21), (a.Id, a.Label, a.DefaultDurationDays));
        Assert.Equal(now.AddDays(-1), a.LastRedemptionSeenAtUtc!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal((loserRewardB, "Loser B"), (rewards[1].Id, rewards[1].Label));
        Assert.False(await verify.ChannelWishRewards.AnyAsync(r => r.Id == loserRewardA));

        var moved = await verify.Wishes.AsNoTracking().SingleAsync(w => w.Id == wishOnA);
        Assert.Equal((pair.SurvivorId, (long?)survivorRewardA, WishStatus.Pending), (moved.ChannelId, moved.RewardId, moved.Status));
        Assert.Equal((string.Empty, "msg-loser-1"), (moved.SevenTvEmoteId, moved.IrcMessageId));

        var learning = await verify.WishRewardLearnings.AsNoTracking().SingleAsync(l => l.ChannelId == pair.SurvivorId);
        Assert.Equal((sessionId, "tag-c"), (learning.SessionId, learning.CandidateRewardId));
        Assert.False(await verify.WishRewardLearnings.AnyAsync(l => l.ChannelId == pair.LoserId));

        Assert.Equal(3, await verify.WishCaptureGaps.CountAsync(g => g.ChannelId == pair.SurvivorId));
        var receipts = await verify.WishCaptureReceipts.AsNoTracking().Where(r => r.ChannelId == pair.SurvivorId).OrderBy(r => r.IrcMessageId).ToListAsync();
        Assert.Equal(["msg-loser-1", "msg-shared"], receipts.Select(r => r.IrcMessageId));
        // On the same key the survivor's receipt stays.
        Assert.Equal(now.AddMinutes(-6), receipts[1].SeenAtUtc, TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, await verify.WishCaptureReceipts.CountAsync(r => r.ChannelId == pair.LoserId));

        using (var details = await MergeAuditAsync(verify, pair))
        {
            var root = details.RootElement;
            Assert.Equal(1, root.GetProperty("movedWishes").GetInt32());
            Assert.Equal(1, root.GetProperty("movedWishRewards").GetInt32());
            Assert.Equal(1, root.GetProperty("collapsedWishRewards").GetInt32());
            Assert.Equal(1, root.GetProperty("reattachedWishes").GetInt32());
            Assert.Equal(1, root.GetProperty("movedWishLearning").GetInt32());
            Assert.Equal(0, root.GetProperty("droppedWishLearning").GetInt32());
            Assert.Equal(2, root.GetProperty("movedWishCaptureGaps").GetInt32());
            Assert.Equal(1, root.GetProperty("movedWishCaptureReceipts").GetInt32());
            Assert.Equal(1, root.GetProperty("collapsedWishCaptureReceipts").GetInt32());
            Assert.Equal(0, root.GetProperty("deletedDuplicateWishes").GetInt32());
            Assert.Equal(0, root.GetProperty("releasedIrcMessageIds").GetInt32());
        }

        // The next redemption in the surviving channel, on the reward that came from the loser.
        var capture = CaptureService();
        Assert.Equal(
            WishCaptureOutcome.WishCreated,
            await capture.ProcessAsync(new RewardObservation(pair.Identity.Id, "tag-b", "msg-after", "5002", "viewer", "KEKW", DateTime.UtcNow)));
        var created = await verify.Wishes.AsNoTracking().SingleAsync(w => w.ChannelId == pair.SurvivorId && w.IrcMessageId == "msg-after");
        Assert.Equal(((long?)loserRewardB, 9), (created.RewardId, created.DurationDays));
        // The moved receipt: the loser's redemption again is a duplicate.
        Assert.Equal(
            WishCaptureOutcome.Duplicate,
            await capture.ProcessAsync(new RewardObservation(pair.Identity.Id, "tag-a", "msg-loser-1", "5001", "viewer", "KEKW", DateTime.UtcNow)));
    }

    // A receipt with no wish behind it (a learning candidate, or a wish since cleared) is the only thing that keeps a
    // buffered redemption from becoming a wish again; it has to move, or the repeat after the merge is a new wish.
    [Fact]
    public async Task Merge_MovesAReceiptWithoutAWish_SoTheRepeatIsRefusedByTheReceipt()
    {
        var pair = await SeedMergePairAsync();
        await using (var seed = fixture.CreateDbContext())
        {
            seed.ChannelWishRewards.Add(CaptureReward(pair.SurvivorId, "tag-a", "A"));
            seed.WishCaptureReceipts.Add(Receipt(pair.LoserId, "msg-receipt-only", DateTime.UtcNow.AddMinutes(-2)));
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, (await CreateHarness(db, [pair.Identity]).Service.ReconcileActiveChannelsAsync())?.Merged);

        var outcome = await CaptureService().ProcessAsync(
            new RewardObservation(pair.Identity.Id, "tag-a", "msg-receipt-only", "5003", "viewer", "KEKW", DateTime.UtcNow));

        Assert.Equal(WishCaptureOutcome.Duplicate, outcome);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Wishes.AnyAsync(w => w.ChannelId == pair.SurvivorId));
    }

    // Spec K6: when both channels are learning, the survivor's session stays.
    [Fact]
    public async Task Merge_WhenBothAreLearning_KeepsTheSurvivorsSession_AndDropsTheLosers()
    {
        var pair = await SeedMergePairAsync();
        var now = DateTime.UtcNow;
        var survivorSession = Guid.NewGuid();
        await using (var seed = fixture.CreateDbContext())
        {
            seed.WishRewardLearnings.AddRange(
                new WishRewardLearning { ChannelId = pair.SurvivorId, SessionId = survivorSession, StartedAtUtc = now, ExpiresAtUtc = now.AddMinutes(10) },
                new WishRewardLearning { ChannelId = pair.LoserId, SessionId = Guid.NewGuid(), StartedAtUtc = now, ExpiresAtUtc = now.AddMinutes(10) });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, (await CreateHarness(db, [pair.Identity]).Service.ReconcileActiveChannelsAsync())?.Merged);

        await using var verify = fixture.CreateDbContext();
        var only = await verify.WishRewardLearnings.AsNoTracking()
            .Where(l => l.ChannelId == pair.SurvivorId || l.ChannelId == pair.LoserId)
            .SingleAsync();
        Assert.Equal((pair.SurvivorId, survivorSession), (only.ChannelId, only.SessionId));
        using var details = await MergeAuditAsync(verify, pair);
        Assert.Equal((0, 1), (details.RootElement.GetProperty("movedWishLearning").GetInt32(), details.RootElement.GetProperty("droppedWishLearning").GetInt32()));
    }

    // More than ten rewards afterwards are allowed (only learning a new one is refused, spec K6), and every one of
    // them still captures.
    [Fact]
    public async Task Merge_MayLeaveMoreThanTenRewards()
    {
        var pair = await SeedMergePairAsync();
        await using (var seed = fixture.CreateDbContext())
        {
            for (var i = 0; i < 6; i++)
            {
                seed.ChannelWishRewards.Add(CaptureReward(pair.SurvivorId, $"tag-s{i}", $"S{i}"));
                seed.ChannelWishRewards.Add(CaptureReward(pair.LoserId, $"tag-l{i}", $"L{i}"));
            }

            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, (await CreateHarness(db, [pair.Identity]).Service.ReconcileActiveChannelsAsync())?.Merged);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(12, await verify.ChannelWishRewards.CountAsync(r => r.ChannelId == pair.SurvivorId));
        Assert.True(12 > WishLimits.MaxRewardsPerChannel);
        Assert.Equal(
            WishCaptureOutcome.WishCreated,
            await CaptureService().ProcessAsync(new RewardObservation(pair.Identity.Id, "tag-l5", "msg-l5", "5004", "viewer", "KEKW", DateTime.UtcNow)));
    }

    // T1 hand-over: the placeholder "" is no emote. A loser with only open wishes without an emote is still moved
    // (the move hangs on "has wishes", not on "has emote ids"), the survivor's open wishes without an emote are not
    // pulled into the running collision, and no CHECK or index is violated. The per-requester limit applies to
    // creation only: open wishes of one viewer in both channels stay above it, a new one is refused.
    [Fact]
    public async Task Merge_MovesALoserWithOnlyOpenWishesWithoutAnEmote_AndLeavesTheSurvivorsAsTheyAre()
    {
        var pair = await SeedMergePairAsync();
        long rewardRow;
        long[] loserWishes, survivorWishes;
        await using (var seed = fixture.CreateDbContext())
        {
            var reward = CaptureReward(pair.SurvivorId, "tag-a", "A");
            seed.ChannelWishRewards.Add(reward);
            await seed.SaveChangesAsync();
            rewardRow = reward.Id;
            var l1 = OpenWish(pair.LoserId, rewardId: null, "msg-l1", requester: "6001");
            var l2 = OpenWish(pair.LoserId, rewardId: null, "msg-l2", requester: "6001");
            var s1 = OpenWish(pair.SurvivorId, rewardRow, "msg-s1", requester: "6001");
            var s2 = OpenWish(pair.SurvivorId, rewardRow, "msg-s2", requester: "6001");
            seed.Wishes.AddRange(l1, l2, s1, s2);
            await seed.SaveChangesAsync();
            (loserWishes, survivorWishes) = ([l1.Id, l2.Id], [s1.Id, s2.Id]);
        }

        var survivorBefore = await WishRowsAsync(survivorWishes);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, (await CreateHarness(db, [pair.Identity]).Service.ReconcileActiveChannelsAsync())?.Merged);

        var movedRows = await WishRowsAsync(loserWishes);
        Assert.All(movedRows, w => Assert.Equal((pair.SurvivorId, WishStatus.Pending, string.Empty), (w.ChannelId, w.Status, w.SevenTvEmoteId)));
        Assert.Equal(
            survivorBefore.Select(w => Serialize(w, channelId: null)),
            (await WishRowsAsync(survivorWishes)).Select(w => Serialize(w, channelId: null)));
        await using var verify = fixture.CreateDbContext();
        using (var details = await MergeAuditAsync(verify, pair))
        {
            Assert.Equal((2, 0), (details.RootElement.GetProperty("movedWishes").GetInt32(), details.RootElement.GetProperty("mergedWishes").GetInt32()));
        }

        Assert.Equal(4, await verify.Wishes.CountAsync(w => w.ChannelId == pair.SurvivorId && w.Status == WishStatus.Pending && w.RequesterTwitchUserId == "6001"));
        Assert.Equal(
            WishCaptureOutcome.RequesterLimit,
            await CaptureService().ProcessAsync(new RewardObservation(pair.Identity.Id, "tag-a", "msg-new", "6001", "viewer", "KEKW", DateTime.UtcNow)));
    }

    // Sol-P2-7, Sol-R2-P1-2: the same IRC message in both channels ends with exactly one owner of the id and a valid
    // state. An open duplicate is deleted (with its slot exclusions), never ended as merged; two decided ones both
    // stay and the greater id gives the IRC id up. Decisions already booked, their slot exclusions and every audit
    // entry stay. Afterwards the same redemption once more is a duplicate and creates nothing.
    [Theory]
    // loser status, survivor status, loser seeded first, expected: which side keeps the IRC id, which row is deleted
    [InlineData(WishStatus.Pending, WishStatus.Approved, false, "survivor", "loser")]
    [InlineData(WishStatus.Rejected, WishStatus.Pending, false, "loser", "survivor")]
    [InlineData(WishStatus.Pending, WishStatus.Pending, false, "survivor", "loser")]
    [InlineData(WishStatus.Pending, WishStatus.Pending, true, "loser", "survivor")]
    [InlineData(WishStatus.Rejected, WishStatus.Approved, false, "survivor", "none")]
    [InlineData(WishStatus.Approved, WishStatus.Expired, true, "loser", "none")]
    public async Task Merge_SameIrcMessageInBothChannels_LeavesExactlyOneOwner(
        string loserStatus, string survivorStatus, bool loserFirst, string keeper, string deleted)
    {
        var pair = await SeedMergePairAsync();
        const string messageId = "msg-same";
        long loserWish, survivorWish;
        await using (var seed = fixture.CreateDbContext())
        {
            var reward = CaptureReward(pair.SurvivorId, "tag-a", "A");
            seed.ChannelWishRewards.Add(reward);
            await seed.SaveChangesAsync();
            var l = IrcWish(pair.LoserId, loserStatus, messageId);
            var s = IrcWish(pair.SurvivorId, survivorStatus, messageId);
            // Seeded one after the other, so the first gets the smaller id.
            seed.Wishes.Add(loserFirst ? l : s);
            await seed.SaveChangesAsync();
            seed.Wishes.Add(loserFirst ? s : l);
            await seed.SaveChangesAsync();
            (loserWish, survivorWish) = (l.Id, s.Id);
            seed.WishSlotExclusions.AddRange(
                new WishSlotExclusion { WishId = loserWish, SlotId = "slot-of-loser", CreatedAtUtc = DateTime.UtcNow },
                new WishSlotExclusion { WishId = survivorWish, SlotId = "slot-of-survivor", CreatedAtUtc = DateTime.UtcNow });
            seed.WishCaptureReceipts.Add(Receipt(pair.LoserId, messageId, DateTime.UtcNow.AddMinutes(-1)));
            seed.AddAuditEntry(WishActor, AuditActions.WishCreate, channelName: pair.LoserName, targetType: "wish", targetId: loserWish.ToString());
            seed.AddAuditEntry(WishActor, AuditActions.WishCreate, channelName: pair.SurvivorName, targetType: "wish", targetId: survivorWish.ToString());
            await seed.SaveChangesAsync();
        }

        var before = (await WishRowsAsync(loserWish, survivorWish)).ToDictionary(w => w.Id);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, (await CreateHarness(db, [pair.Identity]).Service.ReconcileActiveChannelsAsync())?.Merged);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.Wishes.AsNoTracking().Where(w => w.Id == loserWish || w.Id == survivorWish).ToDictionaryAsync(w => w.Id);
        var keeperId = keeper == "loser" ? loserWish : survivorWish;
        var otherId = keeperId == loserWish ? survivorWish : loserWish;
        var owner = Assert.Single(
            await verify.Wishes.AsNoTracking().Where(w => w.ChannelId == pair.SurvivorId && w.IrcMessageId == messageId).ToListAsync());
        Assert.Equal(keeperId, owner.Id);
        Assert.Equal(pair.SurvivorId, rows[keeperId].ChannelId);
        // The keeper is the row it was, moved at most: no decision rewritten, nothing extended, no merged.
        Assert.Equal(Serialize(before[keeperId], channelId: null), Serialize(rows[keeperId], channelId: null));
        Assert.True(await verify.WishSlotExclusions.AnyAsync(x => x.WishId == keeperId));

        if (deleted == "none")
        {
            var other = rows[otherId];
            Assert.Equal(pair.SurvivorId, other.ChannelId);
            Assert.Null(other.IrcMessageId);
            Assert.Equal(before[otherId].Status, other.Status);
            Assert.Equal(before[otherId].DecisionVersion, other.DecisionVersion);
            Assert.True(await verify.WishSlotExclusions.AnyAsync(x => x.WishId == otherId));
        }
        else
        {
            Assert.Equal(deleted == "loser" ? loserWish : survivorWish, otherId);
            Assert.False(rows.ContainsKey(otherId));
            Assert.False(await verify.WishSlotExclusions.AnyAsync(x => x.WishId == otherId));
        }

        // Every audit entry of both wishes stays, also of a deleted one.
        Assert.Equal(
            2,
            await verify.AuditLogEntries.CountAsync(e => e.TargetType == "wish"
                && (e.TargetId == loserWish.ToString() || e.TargetId == survivorWish.ToString())));
        using (var details = await MergeAuditAsync(verify, pair))
        {
            Assert.Equal(deleted == "none" ? 0 : 1, details.RootElement.GetProperty("deletedDuplicateWishes").GetInt32());
            Assert.Equal(deleted == "none" ? 1 : 0, details.RootElement.GetProperty("releasedIrcMessageIds").GetInt32());
        }

        var repeat = await CaptureService().ProcessAsync(
            new RewardObservation(pair.Identity.Id, "tag-a", messageId, "7001", "viewer", "KEKW", DateTime.UtcNow));
        Assert.Equal(WishCaptureOutcome.Duplicate, repeat);
        Assert.Equal(
            deleted == "none" ? 2 : 1,
            await verify.Wishes.CountAsync(w => w.ChannelId == pair.SurvivorId));
    }

    // AK68 and Sol-P2-5 (last sentence) through the real pipeline, and Sol-P2-10: the merge runs from the composed
    // container the worker uses (AddEmotePurgeInfrastructure, real ChannelIdentityService, real capture service), with
    // the wish switches on and off. It is behind no switch, so with wishes off it still carries every capture row
    // instead of letting the cascade delete them. With capture on, an observation for the survivor's room id right
    // after the merge goes through the worker's queue and consumer (WishCaptureWorker, no snapshot in between) and
    // becomes a wish on the reward that came from the loser.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Merge_ThroughTheComposedPipeline_CarriesCaptureRowsWhateverTheSwitch_AndTheWorkerCapturesRightAfter(bool wishesEnabled)
    {
        var pair = await SeedMergePairAsync();
        var now = DateTime.UtcNow;
        long loserReward, openWish;
        await using (var seed = fixture.CreateDbContext())
        {
            var reward = CaptureReward(pair.LoserId, "tag-p", "Pipeline", days: 5);
            seed.ChannelWishRewards.Add(reward);
            await seed.SaveChangesAsync();
            var wish = OpenWish(pair.LoserId, reward.Id, "msg-p1", requester: "8001");
            seed.Wishes.Add(wish);
            seed.WishRewardLearnings.Add(new WishRewardLearning { ChannelId = pair.LoserId, SessionId = Guid.NewGuid(), StartedAtUtc = now, ExpiresAtUtc = now.AddMinutes(10) });
            seed.WishCaptureGaps.Add(Gap(pair.LoserId, now.AddHours(-3), WishCaptureGapReason.Disconnect));
            seed.WishCaptureReceipts.Add(Receipt(pair.LoserId, "msg-p1", now.AddMinutes(-3)));
            await seed.SaveChangesAsync();
            seed.WishSlotExclusions.Add(new WishSlotExclusion { WishId = wish.Id, SlotId = "slot-p", CreatedAtUtc = now });
            await seed.SaveChangesAsync();
            (loserReward, openWish) = (reward.Id, wish.Id);
        }

        await using var provider = BuildComposedProvider(wishesEnabled, [pair.Identity]);
        Assert.Equal(wishesEnabled, provider.GetRequiredService<WishOptions>().CaptureActive);
        using (var scope = provider.CreateScope())
        {
            var summary = await scope.ServiceProvider.GetRequiredService<IChannelIdentityService>().ReconcileActiveChannelsAsync();
            Assert.Equal(1, summary?.Merged);
        }

        await using (var verify = fixture.CreateDbContext())
        {
            Assert.False(await verify.Channels.AnyAsync(c => c.Id == pair.LoserId));
            Assert.Equal(pair.SurvivorId, (await verify.ChannelWishRewards.AsNoTracking().SingleAsync(r => r.Id == loserReward)).ChannelId);
            var wish = await verify.Wishes.AsNoTracking().SingleAsync(w => w.Id == openWish);
            Assert.Equal((pair.SurvivorId, (long?)loserReward, "msg-p1"), (wish.ChannelId, wish.RewardId, wish.IrcMessageId));
            Assert.True(await verify.WishSlotExclusions.AnyAsync(x => x.WishId == openWish));
            Assert.True(await verify.WishRewardLearnings.AnyAsync(l => l.ChannelId == pair.SurvivorId));
            Assert.Equal(1, await verify.WishCaptureGaps.CountAsync(g => g.ChannelId == pair.SurvivorId));
            Assert.True(await verify.WishCaptureReceipts.AnyAsync(r => r.ChannelId == pair.SurvivorId && r.IrcMessageId == "msg-p1"));
        }

        if (!wishesEnabled)
        {
            return;
        }

        // One consumer, strictly in order: once the second has become a wish, the first was decided.
        await RunCaptureWorkerAsync(provider, [
            new RewardObservation(pair.Identity.Id, "tag-p", "msg-p1", "8001", "viewer", "again", DateTime.UtcNow),
            new RewardObservation(pair.Identity.Id, "tag-p", "msg-p2", "8002", "viewer", "7tv.app/emotes/01ARZ3NDEKTSV4RRFFQ69G5FAV", DateTime.UtcNow)
        ], until: async db => await db.Wishes.AnyAsync(w => w.ChannelId == pair.SurvivorId && w.IrcMessageId == "msg-p2"));

        await using var after = fixture.CreateDbContext();
        var created = await after.Wishes.AsNoTracking().SingleAsync(w => w.ChannelId == pair.SurvivorId && w.IrcMessageId == "msg-p2");
        Assert.Equal((WishStatus.Pending, (long?)loserReward, 5), (created.Status, created.RewardId, created.DurationDays));
        Assert.Equal("01ARZ3NDEKTSV4RRFFQ69G5FAV", created.SevenTvEmoteId);
        // The loser's redemption, repeated through the worker, met the moved receipt.
        Assert.Equal(2, await after.Wishes.CountAsync(w => w.ChannelId == pair.SurvivorId));
    }

    // ------------------------------------------------------------------------------------ helpers

    // The container the worker composes: AddEmotePurgeInfrastructure on the fixture's database, only the outside
    // world substituted (Redis, Helix, the app token).
    private ServiceProvider BuildComposedProvider(bool wishesEnabled, IReadOnlyList<TwitchUserIdentity> identities)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Wishes:Enabled"] = wishesEnabled ? "true" : "false",
            ["Wishes:Capture:Enabled"] = wishesEnabled ? "true" : "false",
            ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionStringFor("emotepurge"),
            ["Redis:ConnectionString"] = "localhost:6379"
        }).Build();

        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetUsersAsync(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(identities);
        var appToken = Substitute.For<ITwitchAppTokenProvider>();
        appToken.GetTokenAsync(Arg.Any<CancellationToken>()).Returns("identity-app-token");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEmotePurgeInfrastructure(configuration);
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        services.AddSingleton(helix);
        services.AddSingleton(appToken);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    // Starts the real capture worker on the composed container, hands it the observations through its queue, waits
    // until the condition holds, and stops it. The worker writes the shared life stamp, which
    // is put back afterwards.
    private async Task RunCaptureWorkerAsync(
        ServiceProvider provider, IReadOnlyList<RewardObservation> observations, Func<AppDbContext, Task<bool>> until)
    {
        await using var db = fixture.CreateDbContext();
        var previousStamp = await db.WishCaptureState.AsNoTracking().SingleOrDefaultAsync();
        var stats = new WorkerStats();
        var queue = new RewardObservationQueue(stats);
        var chat = Substitute.For<ITwitchChatManager>();
        chat.GetRoster().Returns([]);
        var worker = new WishCaptureWorker(
            NullLogger<WishCaptureWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            queue,
            new CaptureGapTracker(),
            chat,
            stats,
            provider.GetRequiredService<WishOptions>(),
            TimeProvider.System);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            foreach (var observation in observations)
            {
                Assert.True(queue.TryEnqueue(observation));
            }

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!await until(db))
            {
                Assert.True(DateTime.UtcNow < deadline, "the capture worker did not process the observations in time");
                await Task.Delay(50);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            await db.WishCaptureState.Where(s => s.Id == WishCaptureState.SingletonId).ExecuteDeleteAsync();
            if (previousStamp is not null)
            {
                db.ChangeTracker.Clear();
                db.WishCaptureState.Add(previousStamp);
                await db.SaveChangesAsync();
            }
        }
    }

    private WishCaptureService CaptureService() =>
        new(
            fixture.CreateDbContext(),
            new WishChangedPublisher(Substitute.For<IRedisPublisher>(), NullLogger<WishChangedPublisher>.Instance),
            Substitute.For<IWishReconcileTrigger>(),
            new WishOptions { Enabled = true, Capture = { Enabled = true } },
            TimeProvider.System,
            NullLogger<WishCaptureService>.Instance);

    private static async Task<JsonDocument> MergeAuditAsync(AppDbContext db, MergePair pair)
    {
        var entry = await db.AuditLogEntries.AsNoTracking()
            .SingleAsync(e => e.Action == AuditActions.ChannelMerge && e.ChannelName == pair.LoserName);
        return JsonDocument.Parse(entry.DetailsJson!);
    }

    private static ChannelWishReward CaptureReward(
        string channelId, string tag, string label, DateTime? lastSeen = null, int days = 30) => new()
        {
            ChannelId = channelId,
            RewardId = tag,
            Label = label,
            DefaultDurationDays = days,
            CreatedAtUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            LastRedemptionSeenAtUtc = lastSeen
        };

    // An open captured wish without an emote: the placeholder in all three fields (plan 2.8a).
    private static Wish OpenWish(string channelId, long? rewardId, string ircMessageId, string requester) =>
        IrcWish(channelId, WishStatus.Pending, ircMessageId, rewardId, requester);

    // A captured wish in the given state; the emote fields are filled where CK_Wishes_EmotePlaceholder asks for them.
    private static Wish IrcWish(string channelId, string status, string ircMessageId, long? rewardId = null, string requester = "9001")
    {
        var created = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var placeholder = WishStatus.EmotePlaceholderAllowed.Contains(status);
        return new Wish
        {
            ChannelId = channelId,
            SevenTvEmoteId = placeholder ? string.Empty : Guid.NewGuid().ToString("N")[..24],
            EmoteName = placeholder ? string.Empty : "Emote",
            Alias = placeholder ? string.Empty : "Alias",
            Source = WishSource.Irc,
            Status = status,
            StatusReason = status == WishStatus.Pending ? WishStatusReason.Unparsed : null,
            StatusChangedAtUtc = created,
            CreatedAtUtc = created,
            ApprovedAtUtc = status == WishStatus.Approved ? created.AddHours(1) : null,
            EndedAtUtc = WishStatus.IsEnded(status) ? created.AddDays(1) : null,
            DecisionVersion = status == WishStatus.Pending ? 0 : 1,
            RequesterTwitchUserId = requester,
            RequesterLogin = "viewer",
            RewardId = rewardId,
            IrcMessageId = ircMessageId,
            RequestText = "KEKW",
            MatchKind = WishMatchKind.None
        };
    }

    private static WishCaptureGap Gap(string channelId, DateTime from, string reason) =>
        new() { ChannelId = channelId, FromUtc = from, ToUtc = from.AddMinutes(5), Reason = reason };

    private static WishCaptureReceipt Receipt(string channelId, string ircMessageId, DateTime seenAt) =>
        new() { ChannelId = channelId, IrcMessageId = ircMessageId, SeenAtUtc = seenAt };
}
