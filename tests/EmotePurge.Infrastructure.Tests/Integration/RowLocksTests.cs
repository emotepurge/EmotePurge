using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The slot work's row-lock helper (plan #372 E1/E3/E6, spec §6.5) against real Postgres. Every ordering
// claim is proven with a contender that would deadlock, or fail NOWAIT, if the helper took its locks in
// the caller's order or in a stronger mode.
[Collection("Postgres")]
public class RowLocksTests(PostgresFixture fixture)
{
    [Fact]
    public async Task LockEmotesByIds_TwoLockersOfTheSameEmote_Serialize()
    {
        var (_, emotes) = await SeedAsync("rowlocks_serialize", ("e1", false), ("e2", false));

        await using var first = fixture.CreateTaggedDbContext("rowlocks-serialize-first");
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        Assert.Equal(emotes.Order(StringComparer.Ordinal).ToList(), await first.LockEmotesByIdsAsync([emotes[1], emotes[0]], CancellationToken.None));

        await using var second = fixture.CreateTaggedDbContext("rowlocks-serialize-second");
        await using var secondTransaction = await second.Database.BeginTransactionAsync();
        var waiting = second.LockEmotesByIdsAsync([emotes[1]], CancellationToken.None);
        await fixture.WaitUntilBlockedOnLockAsync("rowlocks-serialize-second", waiting);

        await firstTransaction.CommitAsync();

        Assert.Equal([emotes[1]], await waiting);
    }

    [Fact]
    public async Task LockEmotesByIds_LocksInIdOrder_SoAReversedCallerNeverDeadlocks()
    {
        var (_, emotes) = await SeedAsync("rowlocks_nodeadlock", ("e1", false), ("e2", false));
        var (low, high) = (emotes.Min(StringComparer.Ordinal)!, emotes.Max(StringComparer.Ordinal)!);

        // A holds the lower id; B asks for both in the reverse order and must lock the lower one first,
        // i.e. wait while holding nothing. If it locked the higher id first, A's request for it below
        // would close a cycle (40P01).
        await using var a = fixture.CreateTaggedDbContext("rowlocks-nodeadlock-a");
        await using var aTransaction = await a.Database.BeginTransactionAsync();
        await a.LockEmotesByIdsAsync([low], CancellationToken.None);

        await using var b = fixture.CreateTaggedDbContext("rowlocks-nodeadlock-b");
        await using var bTransaction = await b.Database.BeginTransactionAsync();
        var waiting = b.LockEmotesByIdsAsync([high, low], CancellationToken.None);
        await fixture.WaitUntilBlockedOnLockAsync("rowlocks-nodeadlock-b", waiting);

        Assert.Equal([high], await a.LockEmotesByIdsAsync([high], CancellationToken.None));
        await aTransaction.CommitAsync();

        Assert.Equal([low, high], await waiting);
    }

    [Fact]
    public async Task LockEmotesByIds_ReturnsOnlyIdsThatExist_AndTracksNothing()
    {
        var (_, emotes) = await SeedAsync("rowlocks_missing", ("e1", false));

        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();

        Assert.Equal([emotes[0]], await db.LockEmotesByIdsAsync([emotes[0], "no-such-emote", emotes[0]], CancellationToken.None));
        Assert.Empty(await db.LockEmotesByIdsAsync([], CancellationToken.None));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task EveryEntry_RequiresAnOpenTransaction()
    {
        await using var db = fixture.CreateDbContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.LockEmotesByIdsAsync(["x"], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.LockEmotesForReconcileAsync("c", ["x"], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.LockChannelsByIdsAsync(["x"], CancellationToken.None));
        // Even with nothing to lock: a caller outside a transaction is a bug whether or not it finds rows.
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.LockEmotesByIdsAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task Lock_DoesNotBlockTheKeyShareOfAForeignKeyInsert()
    {
        // E1: a usage-stat insert (the flush, the backfill block replace, ballot rows) takes FOR KEY SHARE
        // on its Emote parent. NO KEY UPDATE does not conflict with that; FOR UPDATE would, and every
        // flush would then wait for a reconcile and risk a deadlock with it.
        var (_, emotes) = await SeedAsync("rowlocks_keyshare", ("e1", false));

        await using var locker = fixture.CreateDbContext();
        await using var lockTransaction = await locker.Database.BeginTransactionAsync();
        await locker.LockEmotesByIdsAsync([emotes[0]], CancellationToken.None);

        // The slot comes from its own context: the probes below must hold only the lock under test.
        string slotId;
        await using (var slotDb = fixture.CreateDbContext())
        {
            slotId = await slotDb.SeedUsageSlotAsync(emotes[0]);
        }

        await using var writer = fixture.CreateTaggedDbContext("rowlocks-keyshare-writer");
        writer.UsageStats.Add(new UsageStat { EmoteId = emotes[0], SlotId = slotId, EmoteSetId = "SETKS", Date = new DateOnly(2026, 10, 1), UseCount = 3 });
        await writer.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await lockTransaction.RollbackAsync();

        // The contrast that makes the assertion above mean something: FOR UPDATE does block the insert.
        await using var strong = fixture.CreateDbContext();
        await using var strongTransaction = await strong.Database.BeginTransactionAsync();
#pragma warning disable EF1002 // constant text, no user input
        await strong.Database.ExecuteSqlRawAsync($"""SELECT 1 FROM "Emotes" WHERE "Id" = '{emotes[0]}' FOR UPDATE""");
#pragma warning restore EF1002
        await using var blocked = fixture.CreateTaggedDbContext("rowlocks-keyshare-blocked");
        blocked.UsageStats.Add(new UsageStat { EmoteId = emotes[0], SlotId = slotId, EmoteSetId = "SETKS", Date = new DateOnly(2026, 10, 2), UseCount = 1 });
        var insert = blocked.SaveChangesAsync();
        await fixture.WaitUntilBlockedOnLockAsync("rowlocks-keyshare-blocked", insert);
        await strongTransaction.RollbackAsync();
        await insert;
    }

    [Fact]
    public async Task LockEmotesForReconcile_LocksExactlyTheRowsTheReconcileCanWrite_AndMatchesTheInMemoryPredicate()
    {
        var channelId = await SeedChannelAsync("rowlocks_reconcile");
        var otherChannelId = await SeedChannelAsync("rowlocks_reconcile_other");

        await using (var seed = fixture.CreateDbContext())
        {
            Emote Row(string channel, string key, bool archived) =>
                new() { ChannelId = channel, SevenTvEmoteId = key, Name = key, ImageUrl = "i", IsArchived = archived };
            EmoteSlot Slot(Emote e, string name, bool archived, bool seedUnsettled = false) =>
                new() { EmoteId = e.Id, Name = name, IsArchived = archived, SeedUnsettled = seedUnsettled };

            var liveArchived = Row(channelId, "live-archived", true);
            var activeNoSlot = Row(channelId, "active-no-slot", false);
            var archivedActiveSlot = Row(channelId, "archived-active-slot", true);
            var archivedSeedOnly = Row(channelId, "archived-seed-only", true);
            var archivedArchivedSlot = Row(channelId, "archived-archived-slot", true);
            var archivedPlain = Row(channelId, "archived-plain", true);
            var foreignLive = Row(otherChannelId, "live-archived", true);
            seed.Emotes.AddRange(liveArchived, activeNoSlot, archivedActiveSlot, archivedSeedOnly, archivedArchivedSlot, archivedPlain, foreignLive);
            seed.EmoteSlots.AddRange(
                Slot(archivedActiveSlot, "A", archived: false),
                Slot(archivedSeedOnly, "S", archived: false, seedUnsettled: true),
                Slot(archivedArchivedSlot, "G", archived: true));
            await seed.SaveChangesAsync();
        }

        string[] live = ["live-archived"];
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var locked = await db.LockEmotesForReconcileAsync(channelId, live, CancellationToken.None);

        var all = await fixture.CreateDbContext().Emotes.AsNoTracking().Include(e => e.Slots)
            .Where(e => e.ChannelId == channelId).ToListAsync();
        var liveSet = live.ToHashSet(StringComparer.Ordinal);
        var expected = all.Where(e => RowLocks.IsReconcileTarget(e, liveSet)).Select(e => e.SevenTvEmoteId).Order(StringComparer.Ordinal).ToList();
        var lockedKeys = all.Where(e => locked.Contains(e.Id)).Select(e => e.SevenTvEmoteId).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["active-no-slot", "archived-active-slot", "live-archived"], lockedKeys);
        Assert.Equal(expected, lockedKeys);
        Assert.Equal(locked.Order(StringComparer.Ordinal).ToList(), locked);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task LockChannelsByIds_ReportsAMissingChannelByAbsence_AndLocksInIdOrder()
    {
        var first = await SeedChannelAsync("rowlocks_channels_one");
        var second = await SeedChannelAsync("rowlocks_channels_two");
        var (low, high) = (new[] { first, second }.Min(StringComparer.Ordinal)!, new[] { first, second }.Max(StringComparer.Ordinal)!);

        await using var a = fixture.CreateTaggedDbContext("rowlocks-channels-a");
        await using var aTransaction = await a.Database.BeginTransactionAsync();
        Assert.Equal([low, high], await a.LockChannelsByIdsAsync([high, "no-such-channel", low], CancellationToken.None));

        // B asks in the reverse order while A holds both: it waits, and after A commits gets both.
        await using var b = fixture.CreateTaggedDbContext("rowlocks-channels-b");
        await using var bTransaction = await b.Database.BeginTransactionAsync();
        var waiting = b.LockChannelsByIdsAsync([high, low], CancellationToken.None);
        await fixture.WaitUntilBlockedOnLockAsync("rowlocks-channels-b", waiting);
        await aTransaction.CommitAsync();
        Assert.Equal([low, high], await waiting);
        await bTransaction.CommitAsync();

        // The deadlock-freedom proof: C holds the lower id, D asks for [high, low] and must wait on the
        // lower one while holding nothing, so C can still take the higher one (40P01 otherwise).
        await using var c = fixture.CreateTaggedDbContext("rowlocks-channels-c");
        await using var cTransaction = await c.Database.BeginTransactionAsync();
        await c.LockChannelsByIdsAsync([low], CancellationToken.None);
        await using var d = fixture.CreateTaggedDbContext("rowlocks-channels-d");
        await using var dTransaction = await d.Database.BeginTransactionAsync();
        var dWaiting = d.LockChannelsByIdsAsync([high, low], CancellationToken.None);
        await fixture.WaitUntilBlockedOnLockAsync("rowlocks-channels-d", dWaiting);
        Assert.Equal([high], await c.LockChannelsByIdsAsync([high], CancellationToken.None));
        await cTransaction.CommitAsync();
        Assert.Equal([low, high], await dWaiting);
    }

    [Fact]
    public async Task LockChannelsByIds_LeavesATrackedChangedChannelExactlyAsItIs()
    {
        // The counterpart of ChannelQueries.LockAsync, which reloads a tracked instance (overwriting the
        // old login the cache cleanup still needs) and refuses a changed one.
        var id = await SeedChannelAsync("rowlocks_tracked_old");

        await using var db = fixture.CreateDbContext();
        var channel = await db.Channels.SingleAsync(c => c.Id == id);
        channel.ChannelName = "rowlocks_tracked_new";
        channel.IsBotActive = false;
        await using var transaction = await db.Database.BeginTransactionAsync();

        Assert.Equal([id], await db.LockChannelsByIdsAsync([id], CancellationToken.None));

        Assert.Equal("rowlocks_tracked_new", channel.ChannelName);
        Assert.False(channel.IsBotActive);
        Assert.Equal(EntityState.Modified, db.Entry(channel).State);
        Assert.Single(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task IsSlotKeyConflict_RecognisesOnlyTheAuthoritativeSlotIndex()
    {
        var (_, emotes) = await SeedAsync("rowlocks_slotkey", ("e1", false));
        var addedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        EmoteSlot Authoritative() => new() { EmoteId = emotes[0], Name = "Dup", AddedAt = addedAt, AddedAtFromRest = true };

        await using (var seed = fixture.CreateDbContext())
        {
            seed.EmoteSlots.Add(Authoritative());
            await seed.SaveChangesAsync();
        }

        await using var slotDb = fixture.CreateDbContext();
        slotDb.EmoteSlots.Add(Authoritative());
        var slotConflict = await Assert.ThrowsAsync<DbUpdateException>(() => slotDb.SaveChangesAsync());
        Assert.True(EmoteSlotKeys.IsSlotKeyConflict(slotConflict));
        Assert.Equal(EmoteSlotKeys.AuthoritativeIndexName, ((PostgresException)slotConflict.InnerException!).ConstraintName);

        // Another unique violation (the emote key) and a non-unique failure (foreign key) are not it.
        await using var emoteDb = fixture.CreateDbContext();
        var existing = await emoteDb.Emotes.AsNoTracking().SingleAsync(e => e.Id == emotes[0]);
        emoteDb.Emotes.Add(new Emote { ChannelId = existing.ChannelId, SevenTvEmoteId = existing.SevenTvEmoteId, Name = "x", ImageUrl = "x" });
        var emoteConflict = await Assert.ThrowsAsync<DbUpdateException>(() => emoteDb.SaveChangesAsync());
        Assert.False(EmoteSlotKeys.IsSlotKeyConflict(emoteConflict));

        await using var fkDb = fixture.CreateDbContext();
        fkDb.EmoteSlots.Add(new EmoteSlot { EmoteId = "no-such-emote", Name = "x" });
        var fkFailure = await Assert.ThrowsAsync<DbUpdateException>(() => fkDb.SaveChangesAsync());
        Assert.False(EmoteSlotKeys.IsSlotKeyConflict(fkFailure));
    }

    private async Task<string> SeedChannelAsync(string name)
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = name, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel.Id;
    }

    private async Task<(string ChannelId, string[] EmoteIds)> SeedAsync(string channelName, params (string Key, bool Archived)[] emotes)
    {
        var channelId = await SeedChannelAsync(channelName);
        await using var db = fixture.CreateDbContext();
        var rows = emotes
            .Select(e => new Emote { ChannelId = channelId, SevenTvEmoteId = e.Key, Name = e.Key, ImageUrl = "i", IsArchived = e.Archived })
            .ToList();
        db.Emotes.AddRange(rows);
        await db.SaveChangesAsync();
        return (channelId, rows.Select(r => r.Id).ToArray());
    }
}
