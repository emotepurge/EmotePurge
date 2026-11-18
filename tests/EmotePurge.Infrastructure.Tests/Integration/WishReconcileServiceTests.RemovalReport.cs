using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Integration.SlotSyncKit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishReconcileService part 5 (plan #485 T0b, Sol-R2-P1-2, Sol-R3-P1): the removal report against the real slot
// writers. The sync commits the inventory before the wish reconcile runs in a transaction of its own; these tests
// stop in that gap (the sync is built without a reconcile trigger, so the reconcile is "paused" until the test
// runs it) and show that a report with the old triple books nothing there, and nothing after the reconcile either.
// The other half, the report in both orders with the client's sync-deleted, uses the browser report writer.
public partial class WishReconcileServiceTests
{
    // A replacement under the wish's alias revives the wish's own slot row (removed, then added again: D7 resumes
    // the archived row with a new date) or arrives as a new row (one sync sees a new date under the alias and keeps
    // the old row as archived). The report of the old stay is a conflict before and after the reconcile, and the
    // reconcile then binds the wish to the replacement.
    [Theory]
    [InlineData("revived-row")]
    [InlineData("new-row")]
    public async Task Report_BetweenTheSlotCommitAndThePausedReconcile_IsAConflict_ThenTheReconcileBindsTheReplacement(string replacement)
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
        var (wishId, _) = await LinkAtAsync(seeded, "x", "kappa", linkAt);
        var report = new CompleteWishRemovalRequest(slotId, T1, SetA);

        DateTime replacedAt;
        string replacementSlot;
        if (replacement == "revived-row")
        {
            replacedAt = linkAt.AddHours(1);
            await SyncSetAsync(channel, Live("y", "other", T1));
            await SyncSetAsync(channel, Live("x", "kappa", replacedAt), Live("y", "other", T1));
            replacementSlot = slotId;
        }
        else
        {
            replacedAt = WallClock().AddMinutes(-10);
            await SyncSetAsync(channel, Live("x", "kappa", replacedAt), Live("y", "other", T1));
            await using var db = fixture.CreateDbContext();
            var slots = await SlotsAsync(db, (await EmoteAsync(db, channel, "x")).Id);
            Assert.Equal(2, slots.Count);
            Assert.True(slots.Single(s => s.Id == slotId).IsArchived);
            replacementSlot = slots.Single(s => s.Id != slotId).Id;
        }

        await using (var db = fixture.CreateDbContext())
        {
            var live = await SlotAsync(db, replacementSlot);
            Assert.Equal((false, (DateTime?)replacedAt), (live.IsArchived, live.AddedAt));
        }

        // The gap: the inventory shows the replacement, the wish still carries the old generation.
        var paused = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, slotId, (DateTime?)T1), (paused.Status, paused.SlotId, paused.SlotAddedAtUtc));
        var before = SnapshotOf(paused);
        _clock.SetUtcNow(WallClock());

        var inTheGap = await WishSut().CompleteRemovalAsync(channel.ChannelName, wishId, report, Actor);

        Assert.Equal(WishOutcome.StateConflict, inTheGap.Outcome);
        Assert.Equal(before, await SnapshotAsync(wishId));

        _clock.SetUtcNow(WallClock().AddMinutes(1));
        await ReconcileAsync(seeded);

        var rebound = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, replacementSlot, (DateTime?)replacedAt), (rebound.Status, rebound.SlotId, rebound.SlotAddedAtUtc));
        var afterReconcile = SnapshotOf(rebound);

        var again = await WishSut().CompleteRemovalAsync(channel.ChannelName, wishId, report, Actor);

        Assert.Equal(WishOutcome.StateConflict, again.Outcome);
        Assert.Equal(afterReconcile, await SnapshotAsync(wishId));
        Assert.Equal(0, await CountRemovalAuditsAsync(seeded));
    }

    // Sol-R3-P1, the return candidate that is older than the wish's entry: the emote is in the set under two
    // aliases; in 7TV the wish's entry is removed and the older entry is renamed to the wish's alias. The full
    // sync commits that (the wish's slot archived, the older slot keeps id and date under the new name), the
    // reconcile is paused, and the report of the old stay is a conflict. The reconcile then returns the wish to
    // the older entry, which lies inside its window.
    [Fact]
    public async Task Report_AfterAnOlderEntryWasRenamedToTheAlias_IsAConflict_ThenTheWishReturnsThere()
    {
        var olderAt = WallClock().AddMinutes(-20);
        var wishAt = WallClock().AddMinutes(-10);
        Channel channel;
        string wishSlot;
        string olderSlot;
        await using (var db = fixture.CreateDbContext())
        {
            channel = await SlotSyncKit.SeedChannelAsync(db, $"r{Guid.NewGuid():N}"[..18]);
            Assert.NotNull(await SyncOnAsync(db, channel, Live("x", "kappa", wishAt), Live("x", "kappatoo", olderAt), Live("y", "other", T1)));
            var slots = await SlotsAsync(db, (await EmoteAsync(db, channel, "x")).Id);
            wishSlot = slots.Single(s => s.Name == "kappa").Id;
            olderSlot = slots.Single(s => s.Name == "kappatoo").Id;
        }

        var seeded = new Seeded(channel.Id, channel.ChannelName);
        _clock.SetUtcNow(WallClock().AddMinutes(-5));
        var wishId = await SeedWishAsync(seeded, "x", WishStatus.Approved, w =>
        {
            w.Alias = "kappa";
            w.ApprovedAtUtc = olderAt.AddHours(-1);
            w.CreatedAtUtc = olderAt.AddHours(-1);
            w.DurationDays = 365;
        });
        await ReconcileAsync(seeded);
        var linked = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, wishSlot, (DateTime?)wishAt), (linked.Status, linked.SlotId, linked.SlotAddedAtUtc));
        var report = new CompleteWishRemovalRequest(wishSlot, wishAt, SetA);

        await SyncSetAsync(channel, Live("x", "kappa", olderAt), Live("y", "other", T1));

        await using (var db = fixture.CreateDbContext())
        {
            Assert.True((await SlotAsync(db, wishSlot)).IsArchived);
            var renamed = await SlotAsync(db, olderSlot);
            Assert.Equal(("kappa", (DateTime?)olderAt, false), (renamed.Name, renamed.AddedAt, renamed.IsArchived));
        }

        var before = await SnapshotAsync(wishId);
        _clock.SetUtcNow(WallClock());

        var inTheGap = await WishSut().CompleteRemovalAsync(channel.ChannelName, wishId, report, Actor);

        Assert.Equal(WishOutcome.StateConflict, inTheGap.Outcome);
        Assert.Equal(before, await SnapshotAsync(wishId));

        _clock.SetUtcNow(WallClock().AddMinutes(1));
        await ReconcileAsync(seeded);

        var returned = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, olderSlot, (DateTime?)olderAt), (returned.Status, returned.SlotId, returned.SlotAddedAtUtc));
        Assert.Equal(WishOutcome.StateConflict, (await WishSut().CompleteRemovalAsync(channel.ChannelName, wishId, report, Actor)).Outcome);
        Assert.Equal(0, await CountRemovalAuditsAsync(seeded));
    }

    // Both orders of the client's two reports after its own removal: complete-removal before sync-deleted books
    // removed now and the archive changes nothing; sync-deleted first lets the reconcile book removed_externally,
    // and the report then turns it into removed with the end time of the archive. One wish.remove either way.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Report_InBothOrdersWithSyncDeleted_EndsRemoved_WithOneEntry(bool reportFirst)
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
        var (wishId, _) = await LinkAtAsync(seeded, x, "kappa", WallClock().AddHours(-1).AddMinutes(-1));
        var report = new CompleteWishRemovalRequest(slot.Id, T1, set, WishRemovalEvidence.Acknowledged);
        var entry = new SyncInSetEntryDto(x, "kappa", slot.Id, T1);

        DateTime expectedEnd;
        if (reportFirst)
        {
            _clock.SetUtcNow(WallClock());
            expectedEnd = Now;
            Assert.Equal(WishOutcome.Success, (await WishSut().CompleteRemovalAsync(channel.ChannelName, wishId, report, Actor)).Outcome);
            await ReportAsync(set, [x], [entry], restore: false);
            _clock.Advance(TimeSpan.FromMinutes(1));
            await ReconcileAsync(seeded);
        }
        else
        {
            await ReportAsync(set, [x], [entry], restore: false);
            await using (var db = fixture.CreateDbContext())
            {
                expectedEnd = (await SlotAsync(db, slot.Id)).ArchivedAt!.Value;
            }

            _clock.SetUtcNow(WallClock());
            await ReconcileAsync(seeded);
            Assert.Equal((WishStatus.RemovedExternally, (DateTime?)expectedEnd), ((await WishAsync(wishId)).Status, (await WishAsync(wishId)).EndedAtUtc));
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(WishOutcome.Success, (await WishSut().CompleteRemovalAsync(channel.ChannelName, wishId, report, Actor)).Outcome);
        }

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Removed, (DateTime?)expectedEnd), (wish.Status, wish.EndedAtUtc));
        Assert.Equal(1, await CountRemovalAuditsAsync(seeded));
    }

    private async Task<int> CountRemovalAuditsAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        return await db.AuditLogEntries.CountAsync(a => a.ChannelName == channel.Name && a.Action == AuditActions.WishRemove);
    }
}
