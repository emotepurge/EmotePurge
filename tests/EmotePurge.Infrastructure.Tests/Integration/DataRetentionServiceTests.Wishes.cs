using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Wish emotes (plan T9, AK17): the two wish categories of the daily pass. Every rule of spec 6.5 and E14 gets
// a row just past its period and one just inside it; the requester fields are what the categories clear.
public partial class DataRetentionServiceTests
{
    private const string RequesterId = "wish-requester-1";
    private const string RequesterLogin = "wishrequester";

    [Fact]
    public async Task WishRequesters_AreClearedByEveryRule_PastThePeriodOnly_AndADryRunChangesNothing()
    {
        var channel = await SeedChannelAsync("wishretention", isBotActive: true, deactivatedAtUtc: null);
        var days = RetentionPolicy.WishRequester.TotalDays;
        var cap = RetentionPolicy.WishNeverActiveRequesterCap.TotalDays;

        var expectCleared = new List<long>
        {
            // Ended 31 days ago.
            await SeedRetentionWishAsync(channel.Id, WishStatus.Removed, endedDaysAgo: days + 1, firstActivatedDaysAgo: 60),
            // (a) Presented with an expiry 31 days ago.
            await SeedRetentionWishAsync(channel.Id, WishStatus.Presented, reason: WishStatusReason.Expired, expiresDaysAgo: days + 1, firstActivatedDaysAgo: 60),
            // (b) Presented (unconfirmed), failed and approved without an expiry, unchanged for 31 days.
            await SeedRetentionWishAsync(channel.Id, WishStatus.Presented, reason: WishStatusReason.Unconfirmed, statusChangedDaysAgo: days + 1),
            await SeedRetentionWishAsync(channel.Id, WishStatus.Failed, reason: WishStatusReason.Unknown, statusChangedDaysAgo: days + 1),
            await SeedRetentionWishAsync(channel.Id, WishStatus.Approved, statusChangedDaysAgo: days + 1),
            // (c) A failed wish retried every 20 days (its state changed a day ago), created 121 days ago.
            await SeedRetentionWishAsync(channel.Id, WishStatus.Failed, reason: WishStatusReason.Unknown, statusChangedDaysAgo: 1, createdDaysAgo: cap + 1),
        };
        var expectKept = new List<long>
        {
            await SeedRetentionWishAsync(channel.Id, WishStatus.Removed, endedDaysAgo: days - 1, firstActivatedDaysAgo: 60),
            await SeedRetentionWishAsync(channel.Id, WishStatus.Presented, reason: WishStatusReason.Expired, expiresDaysAgo: days - 1, firstActivatedDaysAgo: 60),
            await SeedRetentionWishAsync(channel.Id, WishStatus.Presented, reason: WishStatusReason.Unconfirmed, statusChangedDaysAgo: days - 1),
            await SeedRetentionWishAsync(channel.Id, WishStatus.Failed, reason: WishStatusReason.Unknown, statusChangedDaysAgo: days - 1),
            await SeedRetentionWishAsync(channel.Id, WishStatus.Approved, statusChangedDaysAgo: days - 1),
            await SeedRetentionWishAsync(channel.Id, WishStatus.Failed, reason: WishStatusReason.Unknown, statusChangedDaysAgo: 1, createdDaysAgo: cap - 1),
            // An active wish with a 365-day duration, 200 days in: no rule reaches it.
            await SeedRetentionWishAsync(
                channel.Id, WishStatus.Active, durationDays: 365, createdDaysAgo: 200, statusChangedDaysAgo: 200,
                expiresDaysAgo: -165, firstActivatedDaysAgo: 200),
            // An active wish long past its expiry (no expiry run in K1): (a) spares active wishes.
            await SeedRetentionWishAsync(channel.Id, WishStatus.Active, createdDaysAgo: 100, expiresDaysAgo: days + 10, firstActivatedDaysAgo: 100),
            // Externally removed an hour ago, inside the 6-hour return window.
            await SeedRetentionWishAsync(channel.Id, WishStatus.RemovedExternally, endedDaysAgo: 1.0 / 24, expiresDaysAgo: 2, firstActivatedDaysAgo: 40),
        };

        var before = await WishSnapshotAsync();
        var dry = await RunAsync(enforce: false);
        Assert.Equal(before, await WishSnapshotAsync());

        var enforced = await RunAsync(enforce: true);

        Assert.Equal(expectCleared.Count, dry.WishRequestersCleared);
        Assert.Equal(expectCleared.Count, enforced.WishRequestersCleared);
        Assert.Equal(0, enforced.EndedWishesDeleted);
        await using var db = CreateDbContext();
        var rows = await db.Wishes.AsNoTracking().ToDictionaryAsync(w => w.Id);
        Assert.All(expectCleared, id => Assert.Equal((null, null), (rows[id].RequesterTwitchUserId, rows[id].RequesterLogin)));
        Assert.All(expectKept, id => Assert.Equal((RequesterId, RequesterLogin), (rows[id].RequesterTwitchUserId, rows[id].RequesterLogin)));
        // Clearing is not a decision and leaves every other column alone.
        Assert.All(rows.Values, w => Assert.Equal(0, w.DecisionVersion));
        Assert.Equal(expectCleared.Count + expectKept.Count, rows.Count);
    }

    [Fact]
    public async Task EndedWishes_AreDeleted365DaysAfterTheirEnd_AndAreNotAlsoCountedAsCleared()
    {
        var channel = await SeedChannelAsync("wishdeletion", isBotActive: true, deactivatedAtUtc: null);
        var days = RetentionPolicy.EndedWish.TotalDays;

        var due = await SeedRetentionWishAsync(channel.Id, WishStatus.Kept, endedDaysAgo: days + 1, createdDaysAgo: 500, firstActivatedDaysAgo: 450);
        var dueWithoutRequester = await SeedRetentionWishAsync(channel.Id, WishStatus.Closed, endedDaysAgo: days + 1, createdDaysAgo: 500, withRequester: false);
        var inside = await SeedRetentionWishAsync(channel.Id, WishStatus.Removed, endedDaysAgo: days - 1, createdDaysAgo: 450, firstActivatedDaysAgo: 440);
        // Merged into the due one ten days ago: the reference goes with it (SET NULL), the row stays.
        var mergedIntoDue = await SeedRetentionWishAsync(channel.Id, WishStatus.Merged, endedDaysAgo: 10, firstActivatedDaysAgo: 20, mergedInto: due);
        // A running wish with a stray end stamp is never deleted: only end states count.
        var stray = await SeedRetentionWishAsync(channel.Id, WishStatus.Approved, endedDaysAgo: days + 50, statusChangedDaysAgo: 1, createdDaysAgo: 10, withRequester: false);

        var (dry, enforced) = await RunDryThenEnforcedAsync();

        Assert.Equal(2, dry.EndedWishesDeleted);
        Assert.Equal(2, enforced.EndedWishesDeleted);
        // Only the one inside the deletion period counts as cleared; the due one is deleted, not cleared.
        Assert.Equal(1, enforced.WishRequestersCleared);
        await using var db = CreateDbContext();
        var rows = await db.Wishes.AsNoTracking().ToDictionaryAsync(w => w.Id);
        Assert.Equal([inside, mergedIntoDue, stray], rows.Keys.Order());
        Assert.DoesNotContain(due, rows.Keys);
        Assert.DoesNotContain(dueWithoutRequester, rows.Keys);
        Assert.Null(rows[inside].RequesterTwitchUserId);
        Assert.Null(rows[mergedIntoDue].MergedIntoWishId);
        Assert.Equal(RequesterId, rows[mergedIntoDue].RequesterTwitchUserId);
    }

    // Sol-P2-4: a wish that was active once, then removed and re-added, is not "never active" whatever its
    // age. E14 (c) must not take its requester data at once; (b) runs from the re-add's state change.
    [Fact]
    public async Task AReAddedOnceActiveWish_OlderThanTheCap_KeepsItsRequester_UntilRuleBThirtyOneDaysLater()
    {
        var channel = await SeedChannelAsync("wishreadd", isBotActive: true, deactivatedAtUtc: null);
        var cap = RetentionPolicy.WishNeverActiveRequesterCap.TotalDays;
        var wishId = await SeedRetentionWishAsync(
            channel.Id, WishStatus.Removed, endedDaysAgo: 1, createdDaysAgo: cap + 1, statusChangedDaysAgo: 1,
            expiresDaysAgo: 2, firstActivatedDaysAgo: cap - 10);

        await using (var wishDb = CreateDbContext())
        {
            var service = new WishService(
                wishDb,
                new WishChangedPublisher(Substitute.For<IRedisPublisher>(), NullLogger<WishChangedPublisher>.Instance),
                new HandWoundTimeProvider(new DateTimeOffset(_now)),
                NullLogger<WishService>.Instance, BotChannelStates.Unavailable());
            var readd = await service.ReAddAsync(
                channel.ChannelName, wishId, new ReAddWishRequest(WishReAddMode.Timed, null), new AuditActor("wish-mod", "wishmod"));
            Assert.Equal(WishOutcome.Success, readd.Outcome);
        }

        var sameDay = await RunAsync(enforce: true);
        Assert.Equal(0, sameDay.WishRequestersCleared);
        Assert.Equal(RequesterId, (await LoadWishAsync(wishId)).RequesterTwitchUserId);

        var stillInside = await RunAsync(enforce: true, at: _now.AddDays(RetentionPolicy.WishRequester.TotalDays - 1));
        Assert.Equal(0, stillInside.WishRequestersCleared);

        var past = await RunAsync(enforce: true, at: _now.AddDays(RetentionPolicy.WishRequester.TotalDays + 1));
        Assert.Equal(1, past.WishRequestersCleared);
        var wish = await LoadWishAsync(wishId);
        Assert.Equal((WishStatus.Approved, null, null), (wish.Status, wish.RequesterTwitchUserId, wish.RequesterLogin));
        Assert.Null(wish.ExpiresAtUtc);
        Assert.NotNull(wish.FirstActivatedAtUtc);
    }

    // Plan #485 T0b: a removal report after the reconcile's removed_externally keeps EndedAtUtc (the moment the
    // entry left the set), so the reported wish falls due for both periods at the same instant as one nobody
    // reported, not up to six hours later as when the report stamped its own time.
    [Fact]
    public async Task AReportedExternalRemoval_FallsDueAtTheSameInstantAsAnUnreportedOne()
    {
        const string set = "01RETENTIONSET0000000000AA";
        var channel = await SeedChannelAsync("wishreported", isBotActive: true, deactivatedAtUtc: null);
        var endedAt = _now.AddHours(-5);
        string slotId;
        await using (var db = CreateDbContext())
        {
            await db.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(c => c.SetProperty(x => x.ActiveEmoteSetId, set));
            var emote = new Emote { ChannelId = channel.Id, Name = "Kappa", SevenTvEmoteId = "01RETENTIONEMOTE00000000AA" };
            db.Emotes.Add(emote);
            await db.SaveChangesAsync();
            var slot = new EmoteSlot
            {
                EmoteId = emote.Id,
                Name = "Kappa",
                AddedAt = endedAt.AddDays(-40),
                AddedAtFromRest = true,
                IsArchived = true,
                ArchivedAt = endedAt
            };
            db.EmoteSlots.Add(slot);
            await db.SaveChangesAsync();
            slotId = slot.Id;
        }

        var reported = await SeedRetentionWishAsync(channel.Id, WishStatus.RemovedExternally, endedDaysAgo: 1, firstActivatedDaysAgo: 40);
        var unreported = await SeedRetentionWishAsync(channel.Id, WishStatus.RemovedExternally, endedDaysAgo: 1, firstActivatedDaysAgo: 40);
        await using (var db = CreateDbContext())
        {
            await db.Wishes.Where(w => w.Id == reported || w.Id == unreported).ExecuteUpdateAsync(w => w
                .SetProperty(x => x.EndedAtUtc, endedAt)
                .SetProperty(x => x.SlotAddedAtUtc, endedAt.AddDays(-40)));
            await db.Wishes.Where(w => w.Id == reported).ExecuteUpdateAsync(w => w.SetProperty(x => x.SlotId, slotId));
        }

        await using (var wishDb = CreateDbContext())
        {
            var service = new WishService(
                wishDb,
                new WishChangedPublisher(Substitute.For<IRedisPublisher>(), NullLogger<WishChangedPublisher>.Instance),
                new HandWoundTimeProvider(new DateTimeOffset(_now)),
                NullLogger<WishService>.Instance, BotChannelStates.Unavailable());
            var report = await service.CompleteRemovalAsync(
                channel.ChannelName, reported, new CompleteWishRemovalRequest(slotId, endedAt.AddDays(-40), set), new AuditActor("wish-mod", "wishmod"));
            Assert.Equal(WishOutcome.Success, report.Outcome);
        }

        var booked = await LoadWishAsync(reported);
        Assert.Equal((WishStatus.Removed, (DateTime?)endedAt), (booked.Status, booked.EndedAtUtc));

        var requesterDue = endedAt + RetentionPolicy.WishRequester;
        Assert.Equal(0, (await RunAsync(enforce: true, at: requesterDue.AddMinutes(-1))).WishRequestersCleared);
        Assert.Equal(2, (await RunAsync(enforce: true, at: requesterDue.AddMinutes(1))).WishRequestersCleared);

        var deletionDue = endedAt + RetentionPolicy.EndedWish;
        Assert.Equal(0, (await RunAsync(enforce: true, at: deletionDue.AddMinutes(-1))).EndedWishesDeleted);
        Assert.Equal(2, (await RunAsync(enforce: true, at: deletionDue.AddMinutes(1))).EndedWishesDeleted);
        await using var verify = CreateDbContext();
        Assert.False(await verify.Wishes.AnyAsync(w => w.Id == reported || w.Id == unreported));
    }

    [Fact]
    public async Task WishCategories_WalkMoreThanOneBatch()
    {
        var channel = await SeedChannelAsync("wishbatches", isBotActive: true, deactivatedAtUtc: null);
        await using (var seed = CreateDbContext())
        {
            // 1,001 ended wishes past the deletion period and 1,001 past the requester period only.
            foreach (var endedDaysAgo in new[] { 400, 40 })
            {
                var ended = _now.AddDays(-endedDaysAgo);
                await seed.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO "Wishes"
                        ("ChannelId", "SevenTvEmoteId", "EmoteName", "Alias", "Source", "Status", "StatusChangedAtUtc",
                         "RequesterTwitchUserId", "RequesterLogin", "DurationDays", "CreatedAtUtc", "StartsAtAuthoritative",
                         "EndedAtUtc", "ExtensionCount", "Permanent", "BotOwned", "BotAttemptCount", "DecisionVersion",
                         "FirstActivatedAtUtc")
                    SELECT {channel.Id}, 'e' || g, 'E', 'E', 'manual', 'closed', {ended},
                           {RequesterId}, {RequesterLogin}, 30, {ended}, false,
                           {ended}, 0, false, false, 0, 0,
                           {ended}
                    FROM generate_series(1, 1001) AS g
                    """);
            }
        }

        var (dry, enforced) = await RunDryThenEnforcedAsync();

        Assert.Equal(1001, dry.EndedWishesDeleted);
        Assert.Equal(1001, enforced.EndedWishesDeleted);
        Assert.Equal(1001, enforced.WishRequestersCleared);
        await using var db = CreateDbContext();
        Assert.Equal(1001, await db.Wishes.CountAsync());
        Assert.False(await db.Wishes.AnyAsync(w => w.RequesterTwitchUserId != null || w.RequesterLogin != null));
    }

    // K6 (plan T8, AK62, AK64): the chat capture's request data goes with the requester under the same rules, in
    // the same update; a row held only by the new columns counts as holding data; the content-free receipt stays.
    [Fact]
    public async Task CapturedRequestData_IsClearedWithTheRequester_PastThePeriodOnly_AndADryRunChangesNothing()
    {
        var channel = await SeedChannelAsync("wishcapturedata", isBotActive: true, deactivatedAtUtc: null);
        var days = RetentionPolicy.WishRequester.TotalDays;
        var cap = RetentionPolicy.WishNeverActiveRequesterCap.TotalDays;
        var due = new List<long>
        {
            await SeedRetentionWishAsync(channel.Id, WishStatus.Removed, endedDaysAgo: days + 1, firstActivatedDaysAgo: 60, captured: true),
            // E14 (c): an open wish that never became active, created past the cap.
            await SeedRetentionWishAsync(channel.Id, WishStatus.Pending, createdDaysAgo: cap + 1, captured: true),
            // Held only by the capture columns (the requester was cleared by an account deletion earlier).
            await SeedRetentionWishAsync(channel.Id, WishStatus.Rejected, endedDaysAgo: days + 1, withRequester: false, captured: true)
        };
        var inside = new List<long>
        {
            await SeedRetentionWishAsync(channel.Id, WishStatus.Removed, endedDaysAgo: days - 1, firstActivatedDaysAgo: 60, captured: true),
            await SeedRetentionWishAsync(channel.Id, WishStatus.Pending, createdDaysAgo: cap - 1, captured: true),
            await SeedRetentionWishAsync(channel.Id, WishStatus.Rejected, endedDaysAgo: days - 1, withRequester: false, captured: true)
        };
        await using (var seed = CreateDbContext())
        {
            seed.WishCaptureReceipts.Add(new WishCaptureReceipt { ChannelId = channel.Id, IrcMessageId = "receipt-1", SeenAtUtc = _now.AddDays(-1) });
            await seed.SaveChangesAsync();
        }

        var before = await WishSnapshotAsync();
        var dry = await RunAsync(enforce: false);
        Assert.Equal(before, await WishSnapshotAsync());
        var enforced = await RunAsync(enforce: true);

        Assert.Equal(due.Count, dry.WishRequestersCleared);
        Assert.Equal(due.Count, enforced.WishRequestersCleared);
        await using var db = CreateDbContext();
        var rows = await db.Wishes.AsNoTracking().ToDictionaryAsync(w => w.Id);
        Assert.All(due, id => Assert.Equal(
            ((string?)null, (string?)null, (string?)null, (string?)null, (string?)null),
            (rows[id].RequesterTwitchUserId, rows[id].RequesterLogin, rows[id].RequestText, rows[id].RequestedName, rows[id].IrcMessageId)));
        Assert.All(inside, id => Assert.NotNull(rows[id].RequestText));
        Assert.All(inside, id => Assert.NotNull(rows[id].RequestedName));
        Assert.All(inside, id => Assert.NotNull(rows[id].IrcMessageId));
        // What carries no viewer content stays.
        Assert.All(due, id => Assert.Equal(WishMatchKind.Name, rows[id].MatchKind));
        Assert.All(rows.Values, w => Assert.Equal(0, w.DecisionVersion));
        Assert.True(await db.WishCaptureReceipts.AnyAsync(r => r.ChannelId == channel.Id && r.IrcMessageId == "receipt-1"));
    }

    [Fact]
    public async Task CaptureGaps_AreDeleted30DaysAfterTheirEnd_AndADryRunOnlyCounts()
    {
        var channel = await SeedChannelAsync("wishgaps", isBotActive: true, deactivatedAtUtc: null);
        var days = RetentionPolicy.WishCaptureGap.TotalDays;
        await using (var seed = CreateDbContext())
        {
            seed.WishCaptureGaps.AddRange(
                Gap(channel.Id, endedDaysAgo: days + 1),
                Gap(channel.Id, endedDaysAgo: days + 20),
                Gap(channel.Id, endedDaysAgo: days - 1),
                Gap(channel.Id, endedDaysAgo: 0.01));
            await seed.SaveChangesAsync();
        }

        var dry = await RunAsync(enforce: false);
        await using (var check = CreateDbContext())
        {
            Assert.Equal(4, await check.WishCaptureGaps.CountAsync(g => g.ChannelId == channel.Id));
        }

        var enforced = await RunAsync(enforce: true);

        Assert.Equal(2, dry.WishCaptureGapsDeleted);
        Assert.Equal(2, enforced.WishCaptureGapsDeleted);
        await using var db = CreateDbContext();
        Assert.Equal(2, await db.WishCaptureGaps.CountAsync(g => g.ChannelId == channel.Id));
        Assert.All(await db.WishCaptureGaps.AsNoTracking().ToListAsync(), g => Assert.True(g.ToUtc > _now - RetentionPolicy.WishCaptureGap));
    }

    [Fact]
    public async Task CaptureGaps_WalkMoreThanOneBatch()
    {
        var channel = await SeedChannelAsync("wishgapbatches", isBotActive: true, deactivatedAtUtc: null);
        await using (var seed = CreateDbContext())
        {
            var ended = _now.AddDays(-40);
            var started = ended.AddMinutes(-5);
            await seed.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "WishCaptureGaps" ("ChannelId", "FromUtc", "ToUtc", "Reason")
                SELECT {channel.Id}, {started}, {ended}, 'disconnect' FROM generate_series(1, 1001)
                """);
        }

        var (dry, enforced) = await RunDryThenEnforcedAsync();

        Assert.Equal(1001, dry.WishCaptureGapsDeleted);
        Assert.Equal(1001, enforced.WishCaptureGapsDeleted);
        await using var db = CreateDbContext();
        Assert.False(await db.WishCaptureGaps.AnyAsync(g => g.ChannelId == channel.Id));
    }

    private WishCaptureGap Gap(string channelId, double endedDaysAgo) => new()
    {
        ChannelId = channelId,
        FromUtc = _now.AddDays(-endedDaysAgo).AddMinutes(-5),
        ToUtc = _now.AddDays(-endedDaysAgo),
        Reason = WishCaptureGapReason.Disconnect
    };

    private async Task<long> SeedRetentionWishAsync(
        string channelId,
        string status,
        string? reason = null,
        double? endedDaysAgo = null,
        double? expiresDaysAgo = null,
        double statusChangedDaysAgo = 0,
        double createdDaysAgo = 50,
        double? firstActivatedDaysAgo = null,
        int durationDays = 30,
        bool withRequester = true,
        long? mergedInto = null,
        bool captured = false)
    {
        await using var db = CreateDbContext();
        var wish = new Wish
        {
            ChannelId = channelId,
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            EmoteName = "Emote",
            Alias = "Alias",
            Source = WishSource.Manual,
            Status = status,
            StatusReason = reason,
            StatusChangedAtUtc = _now.AddDays(-statusChangedDaysAgo),
            RequesterTwitchUserId = withRequester ? RequesterId : null,
            RequesterLogin = withRequester ? RequesterLogin : null,
            DurationDays = durationDays,
            CreatedAtUtc = _now.AddDays(-createdDaysAgo),
            ApprovedAtUtc = _now.AddDays(-createdDaysAgo),
            ExpiresAtUtc = expiresDaysAgo is { } expires ? _now.AddDays(-expires) : null,
            EndedAtUtc = endedDaysAgo is { } ended ? _now.AddDays(-ended) : null,
            FirstActivatedAtUtc = firstActivatedDaysAgo is { } activated ? _now.AddDays(-activated) : null,
            MergedIntoWishId = mergedInto
        };
        if (captured)
        {
            // A chat-captured wish: the viewer's text, the name read from it and the IRC message id.
            wish.Source = WishSource.Irc;
            wish.RequestText = "please add Emote";
            wish.RequestedName = "Emote";
            wish.IrcMessageId = Guid.NewGuid().ToString("N");
            wish.MatchKind = WishMatchKind.Name;
            if (status is WishStatus.Pending or WishStatus.Rejected)
            {
                wish.SevenTvEmoteId = string.Empty;
                wish.EmoteName = string.Empty;
                wish.Alias = string.Empty;
            }
        }

        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }

    private async Task<Wish> LoadWishAsync(long id)
    {
        await using var db = CreateDbContext();
        return await db.Wishes.AsNoTracking().SingleAsync(w => w.Id == id);
    }

    private async Task<string> WishSnapshotAsync()
    {
        await using var db = CreateDbContext();
        return JsonSerializer.Serialize(await db.Wishes.AsNoTracking().OrderBy(w => w.Id).ToListAsync());
    }
}
