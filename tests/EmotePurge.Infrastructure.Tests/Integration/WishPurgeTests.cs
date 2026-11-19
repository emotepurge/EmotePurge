using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Wish emotes (plan T9, AK18): no purge path needs wish code. Wishes and the settings row go with the channel
// by cascade (ChannelId), the slot link is SET NULL on the way and the merge reference too, so each of the
// three existing purges leaves no row behind, whatever states and links the wishes have.
[Collection("Postgres")]
public class WishPurgeTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("admin")]
    [InlineData("retention")]
    [InlineData("broadcaster")]
    public async Task EveryPurgePath_TakesTheWishesAndTheWishSettingsOfTheChannel(string path)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var name = $"wishpurge{suffix}";
        var twitchId = $"wp{suffix}";
        var (channelId, bystanderId, wishId) = await SeedAsync(name, twitchId);

        await using (var db = fixture.CreateDbContext())
        {
            var service = CreateService(db);
            var actor = new AuditActor(twitchId, name);
            switch (path)
            {
                case "admin":
                    Assert.True(await service.PurgeAsync(name, new AuditActor("wish-admin", "wishadmin")));
                    break;
                case "retention":
                    Assert.Equal(
                        ChannelRetentionPurgeResult.Purged,
                        await service.PurgeIfInactiveSinceAsync(name, DateTime.UtcNow - RetentionPolicy.DeactivatedChannel, AuditActor.System));
                    break;
                default:
                    Assert.Equal(ChannelBroadcasterPurgeResult.Purged, await service.PurgeByBroadcasterAsync(name, actor));
                    break;
            }
        }

        await using var verify = fixture.CreateDbContext();
        try
        {
            Assert.False(await verify.Channels.AnyAsync(c => c.Id == channelId));
            Assert.False(await verify.Wishes.AnyAsync(w => w.ChannelId == channelId));
            Assert.False(await verify.ChannelWishSettings.AnyAsync(s => s.ChannelId == channelId));
            // K6: the six capture tables hang on the channel with CASCADE; the bystander's rows stay.
            Assert.False(await verify.ChannelWishRewards.AnyAsync(r => r.ChannelId == channelId));
            Assert.False(await verify.WishRewardLearnings.AnyAsync(l => l.ChannelId == channelId));
            Assert.False(await verify.WishCaptureGaps.AnyAsync(g => g.ChannelId == channelId));
            Assert.False(await verify.WishCaptureReceipts.AnyAsync(r => r.ChannelId == channelId));
            Assert.False(await verify.WishSlotExclusions.AnyAsync(e => e.WishId == wishId));
            Assert.True(await verify.ChannelWishRewards.AnyAsync(r => r.ChannelId == bystanderId));
            Assert.True(await verify.WishRewardLearnings.AnyAsync(l => l.ChannelId == bystanderId));
            Assert.True(await verify.WishCaptureGaps.AnyAsync(g => g.ChannelId == bystanderId));
            Assert.True(await verify.WishCaptureReceipts.AnyAsync(r => r.ChannelId == bystanderId));
            // Another channel's wishes and settings are untouched.
            Assert.Equal(1, await verify.Wishes.CountAsync(w => w.ChannelId == bystanderId));
            Assert.True(await verify.ChannelWishSettings.AnyAsync(s => s.ChannelId == bystanderId));
        }
        finally
        {
            await verify.BroadcasterChannelLocks.Where(l => l.TwitchChannelId == twitchId).ExecuteDeleteAsync();
        }
    }

    // A left channel, due for the retention purge, with an emote and slot, three wishes (active on the slot,
    // one merged into it, one removed) and a settings row; and a bystander channel with one of each.
    private async Task<(string ChannelId, string BystanderId, long WishId)> SeedAsync(string name, string twitchId)
    {
        await using var db = fixture.CreateDbContext();
        var deactivated = DateTime.UtcNow.AddDays(-200);
        var channel = new Channel { ChannelName = name, TwitchChannelId = twitchId, IsBotActive = false, DeactivatedAtUtc = deactivated };
        var bystander = new Channel { ChannelName = $"{name}by", IsBotActive = false, DeactivatedAtUtc = DateTime.UtcNow };
        db.Channels.AddRange(channel, bystander);
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Name = "PurgeWish",
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        var slot = new EmoteSlot { EmoteId = emote.Id, Name = "PurgeWish", AddedAt = DateTime.UtcNow.AddDays(-3), AddedAtFromRest = true };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();

        var active = NewWish(channel.Id, WishStatus.Active, emote.SevenTvEmoteId);
        active.SlotId = slot.Id;
        active.FirstActivatedAtUtc = DateTime.UtcNow.AddDays(-3);
        db.Wishes.AddRange(active, NewWish(channel.Id, WishStatus.Removed), NewWish(bystander.Id, WishStatus.Approved));
        await db.SaveChangesAsync();
        var merged = NewWish(channel.Id, WishStatus.Merged, emote.SevenTvEmoteId);
        merged.MergedIntoWishId = active.Id;
        db.Wishes.Add(merged);
        db.ChannelWishSettings.AddRange(
            new ChannelWishSettings { ChannelId = channel.Id, DefaultDurationDays = 12 },
            new ChannelWishSettings { ChannelId = bystander.Id });
        await db.SaveChangesAsync();

        // K6 rows on both channels: a reward, a learning row, a gap, a receipt, and a slot exclusion on a wish.
        foreach (var id in new[] { channel.Id, bystander.Id })
        {
            db.ChannelWishRewards.Add(new ChannelWishReward { ChannelId = id, RewardId = "reward-purge", Label = "Reward", DefaultDurationDays = 7, CreatedAtUtc = DateTime.UtcNow });
            db.WishRewardLearnings.Add(new WishRewardLearning
            {
                ChannelId = id,
                SessionId = Guid.NewGuid(),
                StartedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
            });
            db.WishCaptureGaps.Add(new WishCaptureGap
            {
                ChannelId = id,
                FromUtc = DateTime.UtcNow.AddMinutes(-5),
                ToUtc = DateTime.UtcNow,
                Reason = WishCaptureGapReason.Disconnect
            });
            db.WishCaptureReceipts.Add(new WishCaptureReceipt { ChannelId = id, IrcMessageId = "receipt", SeenAtUtc = DateTime.UtcNow });
        }

        db.WishSlotExclusions.Add(new WishSlotExclusion { WishId = merged.Id, SlotId = slot.Id.ToString(), CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (channel.Id, bystander.Id, merged.Id);
    }

    private static Wish NewWish(string channelId, string status, string? emoteId = null) => new()
    {
        ChannelId = channelId,
        SevenTvEmoteId = emoteId ?? Guid.NewGuid().ToString("N")[..24],
        EmoteName = "Emote",
        Alias = "PurgeWish",
        Source = WishSource.Manual,
        Status = status,
        StatusChangedAtUtc = DateTime.UtcNow,
        CreatedAtUtc = DateTime.UtcNow,
        EndedAtUtc = WishStatus.IsEnded(status) ? DateTime.UtcNow : null,
        RequesterTwitchUserId = "wish-purge-requester",
        RequesterLogin = "wishpurgerequester"
    };

    private static ChannelService CreateService(AppDbContext db) =>
        new(
            db,
            Substitute.For<IRedisPublisher>(),
            Substitute.For<IChannelIdentityService>(),
            new ChannelEmoteSetObservationService(db),
            new BroadcasterChannelLockService(db),
            new ChannelCapacityOptions { MaxActiveChannels = int.MaxValue },
            Substitute.For<IExcludedChannelFilter>(),
            new RecordingLogger<ChannelService>());
}
