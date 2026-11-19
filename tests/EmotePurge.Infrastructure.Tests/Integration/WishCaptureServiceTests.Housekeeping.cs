using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The housekeeping pass (E26, AK60, AK66), the persistent set of gap channels, the gap writes (AK62) and the life
// stamp (plan 2.7). The service knows neither switch; the switch-independence is the worker's (T11), here every
// call runs with the switches of the Sut, which never matter.
public partial class WishCaptureServiceTests
{
    // ------------------------------------------------------------------------------------ learnings and receipts

    [Fact]
    public async Task Housekeeping_DeletesExpiredLearningsWithTheirCandidate_AndKeepsRunningOnes()
    {
        var expired = await SeedChannelAsync();
        var running = await SeedChannelAsync();
        await StartLearningAsync(expired);
        await Sut().ProcessAsync(Observation(expired, "tag-new", "msg-1", text: "some text"));
        _clock.Advance(TimeSpan.FromMinutes(6));
        await StartLearningAsync(running);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await Sut().RunHousekeepingAsync(includePendingExpiry: false);

        Assert.True(result.LearningsDeleted >= 1);
        Assert.Null(await LearningRowAsync(expired));
        Assert.NotNull(await LearningRowAsync(running));
    }

    [Fact]
    public async Task Housekeeping_DeletesReceiptsOlderThanSevenDays_AndTheLineIsDecidedAgainAfterwards()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");
        await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-old", userId: "1"));
        _clock.Advance(TimeSpan.FromDays(6));
        await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-young", userId: "2"));
        _clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));

        var result = await Sut().RunHousekeepingAsync(includePendingExpiry: false);

        Assert.True(result.ReceiptsDeleted >= 1);
        Assert.False(await ReceiptExistsAsync(channel, "msg-old"));
        Assert.True(await ReceiptExistsAsync(channel, "msg-young"));
    }

    // ------------------------------------------------------------------------------------ pending expiry (AK60)

    [Theory]
    [InlineData(14, 15 * 24, true)]
    [InlineData(14, 13 * 24, false)]
    [InlineData(1, 23, false)]
    [InlineData(1, 25, true)]
    public async Task Housekeeping_ExpiresOpenWishesPastTheChannelsPeriod(int days, int ageHours, bool expires)
    {
        var channel = await SeedChannelAsync();
        if (days != WishLimits.DefaultPendingExpiryDays)
        {
            await SeedSettingsAsync(channel, pendingExpiryDays: days);
        }

        var wishId = await SeedOpenWishAsync(channel, statusChangedAt: Now.AddHours(-ageHours), reason: WishStatusReason.Unparsed);

        var result = await Sut().RunHousekeepingAsync(includePendingExpiry: true);

        var wish = await WishAsync(wishId);
        if (expires)
        {
            Assert.Equal((WishStatus.Expired, (string?)null, Now, (DateTime?)Now), (wish.Status, wish.StatusReason, wish.StatusChangedAtUtc, wish.EndedAtUtc));
            Assert.True(result.WishesExpired >= 1);
            Assert.Equal(1, PublishedFor(channel));
        }
        else
        {
            Assert.Equal((WishStatus.Pending, WishStatusReason.Unparsed, (DateTime?)null), (wish.Status, wish.StatusReason, wish.EndedAtUtc));
            Assert.Equal(0, PublishedFor(channel));
        }

        await AssertNoAuditAsync(channel);
    }

    // AK66 on the service's side: it reads no switch, so a pass with both switches off cleans up exactly the same.
    [Fact]
    public async Task Housekeeping_WithBothSwitchesOff_StillDeletesTheCandidate_AndExpiresOpenWishes()
    {
        var channel = await SeedChannelAsync();
        await StartLearningAsync(channel);
        await Sut().ProcessAsync(Observation(channel, "tag-new", "msg-1"));
        var wishId = await SeedOpenWishAsync(channel, statusChangedAt: Now.AddDays(-15));
        _clock.Advance(WishLimits.LearningWindow);

        await Sut(options: new WishOptions()).RunHousekeepingAsync(includePendingExpiry: true);

        Assert.Null(await LearningRowAsync(channel));
        Assert.Equal(WishStatus.Expired, (await WishAsync(wishId)).Status);
    }

    [Fact]
    public async Task Housekeeping_WithoutThePendingExpiry_LeavesOldOpenWishes()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedOpenWishAsync(channel, statusChangedAt: Now.AddDays(-30));

        var result = await Sut().RunHousekeepingAsync(includePendingExpiry: false);

        Assert.Equal((0, 0), (result.WishesExpired, result.ChannelsExpired));
        Assert.Equal(WishStatus.Pending, (await WishAsync(wishId)).Status);
    }

    // The period counts from StatusChangedAtUtc: a wish created 20 days ago and unlinked back to pending 2 days ago
    // stays open (spec K6 "läuft nach dem Lösen also neu").
    [Fact]
    public async Task Housekeeping_CountsThePeriodFromTheLastStateChange_NotFromTheCreation()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedOpenWishAsync(channel, statusChangedAt: Now.AddDays(-2), createdAt: Now.AddDays(-20));

        await Sut().RunHousekeepingAsync(includePendingExpiry: true);

        Assert.Equal(WishStatus.Pending, (await WishAsync(wishId)).Status);
    }

    [Fact]
    public async Task Housekeeping_ExpiresOnlyOpenWishes_AndPublishesOncePerChannel()
    {
        var channel = await SeedChannelAsync();
        var first = await SeedOpenWishAsync(channel, statusChangedAt: Now.AddDays(-20));
        var second = await SeedOpenWishAsync(channel, statusChangedAt: Now.AddDays(-16), emoteId: Ulid);
        await using (var db = fixture.CreateDbContext())
        {
            db.Wishes.Add(new Wish
            {
                ChannelId = channel.Id,
                Status = WishStatus.Approved,
                Source = WishSource.Manual,
                SevenTvEmoteId = "01BX5ZZKBKACTAV9WEVGEMMVRZ",
                EmoteName = "Old",
                Alias = "Old",
                CreatedAtUtc = Now.AddDays(-40),
                StatusChangedAtUtc = Now.AddDays(-40),
                ApprovedAtUtc = Now.AddDays(-40)
            });
            await db.SaveChangesAsync();
        }

        await Sut().RunHousekeepingAsync(includePendingExpiry: true);

        Assert.Equal([WishStatus.Approved, WishStatus.Expired, WishStatus.Expired], (await WishesAsync(channel)).Select(w => w.Status).Order());
        Assert.Equal(Ulid, (await WishAsync(second)).SevenTvEmoteId);
        Assert.Equal(WishStatus.Expired, (await WishAsync(first)).Status);
        Assert.Equal(1, PublishedFor(channel));
    }

    // ------------------------------------------------------------------------------------ gap channels

    [Fact]
    public async Task ListGapChannels_HoldsBoundActiveChannelsWithAnActiveReward_Only()
    {
        var included = await SeedChannelAsync();
        await SeedRewardAsync(included, "tag-a");
        await SeedRewardAsync(included, "tag-b", isActive: false);
        var unbound = await SeedChannelAsync(bound: false);
        await SeedRewardAsync(unbound, "tag-a");
        var left = await SeedChannelAsync(isBotActive: false);
        await SeedRewardAsync(left, "tag-a");
        var deactivated = await SeedChannelAsync(deactivatedAt: Now);
        await SeedRewardAsync(deactivated, "tag-a");
        var withoutReward = await SeedChannelAsync();
        var onlyPaused = await SeedChannelAsync();
        await SeedRewardAsync(onlyPaused, "tag-a", isActive: false);

        var channels = await Sut().ListGapChannelsAsync();

        var hit = Assert.Single(channels, c => c.ChannelId == included.Id);
        Assert.Equal((included.TwitchId, included.Name), (hit.TwitchChannelId, hit.ChannelName));
        Assert.DoesNotContain(channels, c => new[] { unbound.Id, left.Id, deactivated.Id, withoutReward.Id, onlyPaused.Id }.Contains(c.ChannelId));
        Assert.Equal(channels.Select(c => c.ChannelName).Order(StringComparer.Ordinal), channels.Select(c => c.ChannelName));
    }

    // ------------------------------------------------------------------------------------ gaps (AK62)

    [Fact]
    public async Task RecordGaps_StoresFiveMinutes_SkipsThirtySeconds_AndChannelsWithoutAnActiveReward()
    {
        var withReward = await SeedChannelAsync();
        await SeedRewardAsync(withReward, "tag-a");
        var withoutReward = await SeedChannelAsync();
        var onlyPaused = await SeedChannelAsync();
        await SeedRewardAsync(onlyPaused, "tag-a", isActive: false);
        var from = Now.AddMinutes(-10);

        var written = await Sut().RecordGapsAsync(
        [
            new WishCaptureGapRecord(withReward.Id, from, from.AddMinutes(5), WishCaptureGapReason.Disconnect),
            new WishCaptureGapRecord(withReward.Id, from, from.AddSeconds(30), WishCaptureGapReason.NotJoined),
            new WishCaptureGapRecord(withoutReward.Id, from, from.AddMinutes(5), WishCaptureGapReason.Disconnect),
            new WishCaptureGapRecord(onlyPaused.Id, from, from.AddMinutes(5), WishCaptureGapReason.Disconnect),
            new WishCaptureGapRecord(Guid.NewGuid().ToString(), from, from.AddMinutes(5), WishCaptureGapReason.Disconnect)
        ]);

        Assert.Equal(1, written);
        var gap = Assert.Single(await GapsAsync(withReward));
        Assert.Equal((from, from.AddMinutes(5), WishCaptureGapReason.Disconnect), (gap.FromUtc, gap.ToUtc, gap.Reason));
        Assert.Empty(await GapsAsync(withoutReward));
        Assert.Empty(await GapsAsync(onlyPaused));
        AssertPublishedOnce(withReward);
    }

    [Fact]
    public async Task RecordGaps_ExactlySixtySeconds_IsStored()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");

        var written = await Sut().RecordGapsAsync(
            [new WishCaptureGapRecord(channel.Id, Now.AddMinutes(-1), Now, WishCaptureGapReason.WorkerRestart)]);

        Assert.Equal(1, written);
        Assert.Equal(WishCaptureGapReason.WorkerRestart, Assert.Single(await GapsAsync(channel)).Reason);
    }

    [Fact]
    public async Task RecordGaps_WithAnUnknownReason_Throws_AndWritesNothing()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");

        await Assert.ThrowsAsync<ArgumentException>(() => Sut().RecordGapsAsync(
        [
            new WishCaptureGapRecord(channel.Id, Now.AddMinutes(-5), Now, WishCaptureGapReason.Disconnect),
            new WishCaptureGapRecord(channel.Id, Now.AddMinutes(-5), Now, "network")
        ]));

        Assert.Empty(await GapsAsync(channel));
    }

    [Fact]
    public async Task RecordGaps_WithNothingLongEnough_WritesAndPublishesNothing()
    {
        Assert.Equal(0, await Sut().RecordGapsAsync([]));
        AssertNothingPublished();
    }

    // ------------------------------------------------------------------------------------ life stamp

    [Fact]
    public async Task LifeStamp_IsMissingUntilTheFirstWrite_ThenOverwritten()
    {
        await using (var db = fixture.CreateDbContext())
        {
            await db.WishCaptureState.ExecuteDeleteAsync();
        }

        Assert.Null(await Sut().ReadLastAliveAsync());

        await Sut().WriteLastAliveAsync(Now);
        Assert.Equal(Now, await Sut().ReadLastAliveAsync());

        await Sut().WriteLastAliveAsync(Now.AddMinutes(1));
        Assert.Equal(Now.AddMinutes(1), await Sut().ReadLastAliveAsync());

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.WishCaptureState.CountAsync());
    }

    // ------------------------------------------------------------------------------------ helpers

    // The expiry runs over every channel of the shared database, so an earlier suite's old open wish may publish too;
    // these tests count only their own channel's events.
    private int PublishedFor(Seeded channel) =>
        PublishCalls().Count(c => LiveEvent.TryParse((string?)c.GetArguments()[1]) == new LiveEvent(LiveEvents.WishChanged, channel.Name));

    private async Task<long> SeedOpenWishAsync(
        Seeded channel, DateTime statusChangedAt, DateTime? createdAt = null, string? reason = null, string emoteId = "")
    {
        await using var db = fixture.CreateDbContext();
        var wish = new Wish
        {
            ChannelId = channel.Id,
            Status = WishStatus.Pending,
            StatusReason = reason,
            Source = WishSource.Irc,
            SevenTvEmoteId = emoteId,
            MatchKind = emoteId.Length > 0 ? WishMatchKind.Link : WishMatchKind.None,
            CreatedAtUtc = createdAt ?? statusChangedAt,
            StatusChangedAtUtc = statusChangedAt
        };
        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }

    private async Task<Wish> WishAsync(long id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Wishes.AsNoTracking().SingleAsync(w => w.Id == id);
    }

    private async Task<List<WishCaptureGap>> GapsAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WishCaptureGaps.AsNoTracking().Where(g => g.ChannelId == channel.Id).ToListAsync();
    }
}
