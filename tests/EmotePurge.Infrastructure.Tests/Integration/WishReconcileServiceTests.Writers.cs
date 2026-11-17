using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Integration.SlotSyncKit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishReconcileService part 2: the real slot writers (plan Sol-P2-1), the savepoint per wish (Sol-P2-3),
// the lock order against a command (Sol-P2-2) and a channel deleted during the run. There is no hook in the
// writers yet (T8 adds it), so each test calls the reconcile itself between the steps. The writers stamp
// archive and restore times with the wall clock, so these tests set the reconcile's clock from it.
public partial class WishReconcileServiceTests
{
    private const string OwnerSevenTvUserId = "owner-seven-tv-id";
    private const string OwnerTwitchLogin = "setowner";
    private const string OwnerTwitchUserId = "4499";

    // ------------------------------------------------------------------------------------ real writers

    // (i) reconcile after both steps, return inside the window; (ii) the reconcile after the archive failed
    // or never ran, back after 7 hours -> removed_externally, not active; (iii) as (ii), back after 1 hour ->
    // returned with the old deadline; (iv) hook missed, slot still archived -> EndedAtUtc = ArchivedAt.
    [Theory]
    [InlineData("i")]
    [InlineData("ii")]
    [InlineData("iii")]
    [InlineData("iv")]
    public async Task Report_DeleteAndRestore_ThroughEmoteService(string scenario)
    {
        var set = "01WISH" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        var x = "w" + Guid.NewGuid().ToString("N")[..23];
        Channel channel;
        EmoteSlot slot;
        await using (var db = fixture.CreateDbContext())
        {
            channel = await SlotSyncKit.SeedChannelAsync(db, $"r{Guid.NewGuid():N}"[..18], set);
            var emote = await SlotSyncKit.SeedEmoteAsync(db, channel, x, "kappa", firstSeenAt: T1);
            slot = await SlotSyncKit.SeedSlotAsync(db, emote, "kappa", T1);
        }

        var seeded = new Seeded(channel.Id, channel.ChannelName);
        var linkAt = WallClock().AddHours(scenario == "ii" ? -7 : -1).AddMinutes(-1);
        var (wishId, linked) = await LinkAtAsync(seeded, x, "kappa", linkAt);
        var entry = new SyncInSetEntryDto(x, "kappa", slot.Id, T1);

        await ReportAsync(set, [x], [entry], restore: false);
        DateTime archivedAt;
        await using (var db = fixture.CreateDbContext())
        {
            archivedAt = (await SlotAsync(db, slot.Id)).ArchivedAt!.Value;
        }

        if (scenario == "i")
        {
            _clock.SetUtcNow(WallClock());
            await ReconcileAsync(seeded);
            Assert.Equal((WishStatus.RemovedExternally, archivedAt), ((await WishAsync(wishId)).Status, (await WishAsync(wishId)).EndedAtUtc));
        }

        if (scenario != "iv")
        {
            await ReportAsync(set, [x], [entry], restore: true);
        }

        _clock.SetUtcNow(WallClock().AddMinutes(scenario == "iv" ? 120 : 1));
        await ReconcileAsync(seeded);

        var wish = await WishAsync(wishId);
        switch (scenario)
        {
            case "i":
            case "iii":
                Assert.Equal((WishStatus.Active, slot.Id, (DateTime?)null), (wish.Status, wish.SlotId, wish.EndedAtUtc));
                Assert.Equal((linked.StartsAtUtc, linked.ExpiresAtUtc), (wish.StartsAtUtc, wish.ExpiresAtUtc));
                // The restore's date is provisional, so the flag follows it (Sol-R2-2).
                Assert.False(wish.StartsAtAuthoritative);
                break;
            case "ii":
                Assert.Equal((WishStatus.RemovedExternally, linked.SlotConfirmedAtUtc), (wish.Status, wish.EndedAtUtc));
                break;
            default:
                Assert.Equal((WishStatus.RemovedExternally, archivedAt), (wish.Status, wish.EndedAtUtc));
                break;
        }
    }

    // The same four cases through the full sync: the emote leaves the set, then D7 resumes the archived row
    // (ResumeArchivedSlots: same slot id, new authoritative AddedAt).
    [Theory]
    [InlineData("i")]
    [InlineData("ii")]
    [InlineData("iii")]
    [InlineData("iv")]
    public async Task FullSync_RemoveAndResume_ThroughTheSameSlot(string scenario)
    {
        Channel channel;
        string slotId;
        await using (var db = fixture.CreateDbContext())
        {
            channel = await SlotSyncKit.SeedChannelAsync(db, $"r{Guid.NewGuid():N}"[..18]);
            Assert.NotNull(await SyncOnAsync(db, channel, Live("x", "kappa", T1), Live("y", "other", T1)));
            slotId = Assert.Single(await SlotsAsync(db, (await EmoteAsync(db, channel, "x")).Id)).Id;
        }

        var seeded = new Seeded(channel.Id, channel.ChannelName);
        var linkAt = WallClock().AddHours(-8);
        var (wishId, linked) = await LinkAtAsync(seeded, "x", "kappa", linkAt);

        await SyncSetAsync(channel, Live("y", "other", T1));
        DateTime archivedAt;
        await using (var db = fixture.CreateDbContext())
        {
            archivedAt = (await SlotAsync(db, slotId)).ArchivedAt!.Value;
        }

        DateTime? resumedAt = scenario switch
        {
            "i" => archivedAt.AddHours(1),
            "ii" => linkAt.AddHours(7),
            "iii" => linkAt.AddHours(1),
            _ => null
        };

        if (scenario == "i")
        {
            _clock.SetUtcNow(WallClock());
            await ReconcileAsync(seeded);
            Assert.Equal(WishStatus.RemovedExternally, (await WishAsync(wishId)).Status);
        }

        if (resumedAt is { } addedAt)
        {
            await SyncSetAsync(channel, Live("x", "kappa", addedAt), Live("y", "other", T1));
            await using var db = fixture.CreateDbContext();
            var resumed = await SlotAsync(db, slotId);
            Assert.Equal((false, addedAt), (resumed.IsArchived, resumed.AddedAt));
        }

        _clock.SetUtcNow(WallClock().AddHours(2));
        await ReconcileAsync(seeded);

        var wish = await WishAsync(wishId);
        switch (scenario)
        {
            case "i":
            case "iii":
                Assert.Equal((WishStatus.Active, slotId, (DateTime?)null), (wish.Status, wish.SlotId, wish.EndedAtUtc));
                Assert.Equal((linked.StartsAtUtc, linked.ExpiresAtUtc), (wish.StartsAtUtc, wish.ExpiresAtUtc));
                Assert.Equal((true, resumedAt), (wish.StartsAtAuthoritative, wish.SlotAddedAtUtc));
                break;
            case "ii":
                Assert.Equal((WishStatus.RemovedExternally, linkAt), (wish.Status, wish.EndedAtUtc));
                break;
            default:
                Assert.Equal((WishStatus.RemovedExternally, archivedAt), (wish.Status, wish.EndedAtUtc));
                break;
        }
    }

    // ------------------------------------------------------------------------------------ savepoint (Sol-P2-3)

    // A return that fails at the running index because a re-add committed between this run's reads and its
    // write (the pre-filter bypassed through a barrier), next to an independent valid link: the link commits,
    // the colliding wish is unchanged, nothing is thrown, and the skip is logged without the alias.
    [Fact]
    public async Task SavepointPerWish_AnIndexHitLeavesThatWishAlone_AndTheRestOfThePassCommits()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var other = await SeedEmoteAsync(channel);
        var returner = await SeedWishAsync(channel, emote, WishStatus.RemovedExternally, w =>
        {
            w.Alias = "Kappa";
            w.EndedAtUtc = Now.AddHours(-1);
            w.CreatedAtUtc = Now.AddDays(-3);
        });
        var removed = await SeedWishAsync(channel, emote, WishStatus.Removed);
        var independent = await SeedWishAsync(channel, other, WishStatus.Approved, w =>
        {
            w.ApprovedAtUtc = Now.AddHours(-1);
            w.CreatedAtUtc = Now.AddDays(-1);
        });
        await SeedSlotAsync(emote, "Kappa", Now.AddMinutes(-30));
        var otherSlot = await SeedSlotAsync(other, "Other", Now.AddMinutes(-30));
        var returnerBefore = await SnapshotAsync(returner);

        // The barrier: the slot read is this run's last read before the writes.
        var hook = new CommandHook(
            c => c.Contains("FROM \"EmoteSlots\"", StringComparison.Ordinal) && c.Contains("\"AddedAtFromRest\"", StringComparison.Ordinal),
            async _ =>
            {
                var reAdd = await WishSut().ReAddAsync(channel.Name, removed, new ReAddWishRequest(WishReAddMode.Timed, null), Actor);
                Assert.True(reAdd.IsSuccess);
            });

        await Sut(() => fixture.CreateTaggedDbContext("wish-reconcile-savepoint", [hook])).ReconcileChannelAsync(channel.Id);

        Assert.Equal(returnerBefore, await SnapshotAsync(returner));
        Assert.Equal(WishStatus.Approved, (await WishAsync(removed)).Status);
        var linked = await WishAsync(independent);
        Assert.Equal((WishStatus.Active, otherSlot), (linked.Status, linked.SlotId));
        var line = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("IX_Wishes_ChannelId_SevenTvEmoteId_Running", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Kappa", line.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------ locks (Sol-P2-2)

    // extend against the reconcile's date correction, in both commit orders: whoever waits sees what the other
    // committed, so the expiry ends as "corrected expiry plus the extension", never as the uncorrected value.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExtendAgainstDateCorrection_EitherCommitOrder_EndsAtTheCorrectedExpiryPlusTheExtension(bool reconcileFirst)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Approved, w => w.ApprovedAtUtc = Now.AddHours(-2));
        var provisional = Now.AddHours(-1);
        var slot = await SeedSlotAsync(emote, "Alias", provisional, fromRest: false);
        await ReconcileAsync(channel);
        var linked = await WishAsync(wishId);
        var correction = TimeSpan.FromMinutes(-10);
        await UpdateSlotAsync(slot, s => (s.AddedAt, s.AddedAtFromRest) = (provisional + correction, true));
        _clock.Advance(TimeSpan.FromMinutes(5));

        static bool IsWishLock(string c) => c.Contains("FROM \"Wishes\"", StringComparison.Ordinal) && c.Contains("FOR UPDATE", StringComparison.Ordinal);

        if (reconcileFirst)
        {
            Task<WishCommandResult>? extend = null;
            var hook = new CommandHook(IsWishLock, async _ =>
            {
                extend = WishSut(fixture.CreateTaggedDbContext("wish-extend-waits")).ExtendAsync(channel.Name, wishId, Actor);
                await fixture.WaitUntilBlockedOnLockAsync("wish-extend-waits", extend);
            });

            await Sut(() => fixture.CreateTaggedDbContext("wish-reconcile-first", [hook])).ReconcileChannelAsync(channel.Id);
            Assert.True((await extend!).IsSuccess);
        }
        else
        {
            Task? reconcile = null;
            var hook = new CommandHook(IsWishLock, async _ =>
            {
                reconcile = Sut(() => fixture.CreateTaggedDbContext("wish-reconcile-waits")).ReconcileChannelAsync(channel.Id);
                await fixture.WaitUntilBlockedOnLockAsync("wish-reconcile-waits", reconcile);
            });

            var extended = await WishSut(fixture.CreateTaggedDbContext("wish-extend-first", [hook])).ExtendAsync(channel.Name, wishId, Actor);
            Assert.True(extended.IsSuccess);
            await reconcile!;
        }

        var wish = await WishAsync(wishId);
        var correctedExpiry = linked.ExpiresAtUtc!.Value + correction;
        Assert.Equal(correctedExpiry.AddDays(WishLimits.DefaultDurationDays), wish.ExpiresAtUtc);
        Assert.Equal((provisional + correction, true), (wish.StartsAtUtc, wish.StartsAtAuthoritative));
        Assert.Equal((1, 1), (wish.ExtensionCount, wish.DecisionVersion));
    }

    // The channel is purged between the pre-check and the lock: the run ends without effect and without error.
    [Fact]
    public async Task ChannelDeletedDuringTheRun_IsNoError_AndAnnouncesNothing()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        await SeedWishAsync(channel, emote, WishStatus.Approved, w => w.ApprovedAtUtc = Now.AddHours(-1));
        await SeedSlotAsync(emote, "Alias", Now);
        var hook = new CommandHook(
            c => c.Contains("FROM \"Wishes\"", StringComparison.Ordinal),
            async occurrence =>
            {
                if (occurrence == 1)
                {
                    await using var db = fixture.CreateDbContext();
                    await db.Channels.Where(c => c.Id == channel.Id).ExecuteDeleteAsync();
                }
            });

        await Sut(() => fixture.CreateTaggedDbContext("wish-reconcile-purged", [hook])).ReconcileChannelAsync(channel.Id);

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Wishes.AnyAsync(w => w.ChannelId == channel.Id));
        AssertNothingPublished();
    }

    // ------------------------------------------------------------------------------------ helpers (writers)

    // The wall clock in whole seconds: the writers stamp with it, and whole seconds survive every round trip.
    private static DateTime WallClock()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    // An approved wish (approved before T1) for the slot of the 7TV emote, linked by a reconcile at linkAt on a
    // 365-day run, so the deadline is plainly in the future.
    private async Task<(long WishId, Wish Linked)> LinkAtAsync(Seeded channel, string sevenTvEmoteId, string alias, DateTime linkAt)
    {
        _clock.SetUtcNow(linkAt);
        var wishId = await SeedWishAsync(channel, sevenTvEmoteId, WishStatus.Approved, w =>
        {
            w.Alias = alias;
            w.ApprovedAtUtc = T1.AddDays(-1);
            w.CreatedAtUtc = T1.AddDays(-1);
            w.DurationDays = 365;
        });
        await ReconcileAsync(channel);
        var linked = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, T1, linkAt), (linked.Status, linked.SlotAddedAtUtc, linked.SlotConfirmedAtUtc));
        return (wishId, linked);
    }

    private async Task ReportAsync(string set, IReadOnlyList<string> ids, IReadOnlyList<SyncInSetEntryDto> entries, bool restore)
    {
        await using var db = fixture.CreateDbContext();
        var service = new EmoteService(
            db, NullLogger<EmoteService>.Instance, Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db));
        var actor = new AuditActor("100", "synctester");
        if (restore)
        {
            var result = await service.MarkRestoredInSetAsync(set, OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ids, null, actor, entries);
            Assert.Equal(1, Assert.Single(result.Channels).NewlyChangedCount);
        }
        else
        {
            var result = await service.MarkDeletedInSetAsync(set, OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ids, null, actor, entries);
            Assert.Equal(1, Assert.Single(result.Channels).NewlyChangedCount);
        }
    }

    private async Task SyncSetAsync(Channel channel, params EmotePurge.Core.SevenTv.SevenTvEmote[] live)
    {
        await using var db = fixture.CreateDbContext();
        Assert.NotNull(await SyncOnAsync(db, channel, live));
    }
}
