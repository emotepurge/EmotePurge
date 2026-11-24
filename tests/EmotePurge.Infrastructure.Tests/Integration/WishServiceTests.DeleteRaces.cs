using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishService part 7 (K7a, plan #500 T2): deleting an ended wish against other connections in the shared database. Two
// deletes of the same wish (AK111), the merged children locked without waiting (E32, SQLSTATE 55P03 gives way with a state
// conflict and leaves the scope usable), and an identity change behind the channel lock (E41, AK114). The races with the
// real retention pass run in the retention suite's own database (DataRetentionServiceTests.WishDelete, AK112), because
// the pass works on every row.
public partial class WishServiceTests
{
    // ------------------------------------------------------------------------------------ two deletes (AK111)

    [Fact]
    public async Task Delete_TwiceAtOnce_OneSucceeds_TheOtherIsWishNotFound_AndOneAuditEntry()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Removed);
        var ended = (await LoadWishAsync(wishId)).EndedAtUtc!.Value;
        var access = await AccessAsync(channel);

        // Both wait behind a third connection's channel lock, so they really start together; the first to get the
        // channel row deletes, the second finds no wish row behind it.
        await using var holder = fixture.CreateDbContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Channels" WHERE "Id" = {channel.Id} FOR NO KEY UPDATE""");
        var first = Sut(fixture.CreateTaggedDbContext("wish-delete-twice-a")).DeleteAsync(channel.Name, wishId, ended, Actor, access);
        var second = Sut(fixture.CreateTaggedDbContext("wish-delete-twice-b")).DeleteAsync(channel.Name, wishId, ended, Actor, access);
        await fixture.WaitUntilBlockedOnLockAsync("wish-delete-twice-a", first);
        await fixture.WaitUntilBlockedOnLockAsync("wish-delete-twice-b", second);
        await held.CommitAsync();

        var outcomes = (await Task.WhenAll(first, second)).Select(r => r.Outcome).Order().ToArray();

        Assert.Equal(new[] { WishOutcome.Success, WishOutcome.WishNotFound }.Order(), outcomes);
        await AssertGoneAsync(wishId);
        Assert.Equal(1, await CountAuditsAsync(channel, AuditActions.WishDelete));
        AssertPublishedOnce(channel);
    }

    [Fact]
    public async Task ChannelScopedWishLock_WithNoIds_LocksNothingAndReturnsNothing()
    {
        var channel = await SeedChannelAsync();
        await using var db = fixture.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();

        Assert.Empty(await db.LockChannelWishesByIdsAsync(channel.Id, [], CancellationToken.None));
        Assert.Empty(await db.LockChannelWishesByIdsNoWaitAsync(channel.Id, [], CancellationToken.None));
    }

    // ------------------------------------------------------------------------------------ merged children without waiting (E32)

    [Fact]
    public async Task Delete_WhileAnotherTransactionHoldsAMergedChild_IsAStateConflict_AtOnce_AndTheSameScopeDeletesAfterwards()
    {
        var channel = await SeedChannelAsync();
        var target = await SeedRowAsync(channel, WishStatus.Kept);
        var child = await SeedRowAsync(channel, WishStatus.Merged, w => w.MergedIntoWishId = target);
        var ended = (await LoadWishAsync(target)).EndedAtUtc!.Value;
        var targetBefore = await SnapshotAsync(target);
        var childBefore = await SnapshotAsync(child);
        await using var scope = fixture.CreateDbContext();
        var sut = Sut(scope);
        var access = await AccessAsync(channel);

        await using (var holder = fixture.CreateDbContext())
        {
            await using var held = await holder.Database.BeginTransactionAsync();
            await holder.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Wishes" WHERE "Id" = {child} FOR UPDATE""");

            // Without NOWAIT the command would wait here until the holder ends; it gives way instead. The bound cancels the
            // statement rather than abandoning a task that still uses the context.
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var refused = await sut.DeleteAsync(channel.Name, target, ended, Actor, access, bound.Token);

            Assert.Equal(WishOutcome.StateConflict, refused.Outcome);
            await AssertUntouchedAsync(channel, target, targetBefore);
            Assert.Equal(childBefore, await SnapshotAsync(child));
            // The refused command released everything it had locked: the channel and the target are free again.
            Assert.False(await IsWishRowLockedAsync(target));
            Assert.Null(scope.Database.CurrentTransaction);
            Assert.Empty(scope.ChangeTracker.Entries());
            await held.RollbackAsync();
        }

        // The aborted transaction left nothing behind: the same context deletes on the next attempt.
        var retried = await sut.DeleteAsync(channel.Name, target, ended, Actor, access);

        Assert.Equal(WishOutcome.Success, retried.Outcome);
        await AssertGoneAsync(target);
        Assert.Null((await LoadWishAsync(child)).MergedIntoWishId);
        Assert.Equal(1, await CountAuditsAsync(channel, AuditActions.WishDelete));
        AssertPublishedOnce(channel);
    }

    // Plan #500 T2, edge cases: no children → no lock statement; several children → one statement, ascending.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 1)]
    public async Task Delete_LocksTheMergedChildrenInOneStatement_OnlyWhenThereAreAny(int children, int statements)
    {
        var channel = await SeedChannelAsync();
        var target = await SeedRowAsync(channel, WishStatus.Closed);
        var childIds = new List<long>();
        for (var i = 0; i < children; i++)
        {
            childIds.Add(await SeedRowAsync(channel, WishStatus.Merged, w => w.MergedIntoWishId = target));
        }

        var ended = (await LoadWishAsync(target)).EndedAtUtc!.Value;
        var hook = new CommandHook(_ => false, _ => Task.CompletedTask);

        var result = await Sut(fixture.CreateTaggedDbContext("wish-delete-children", [hook]))
            .DeleteAsync(channel.Name, target, ended, Actor, await AccessAsync(channel));

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal(statements, hook.Count(IsChildLock));
        await using var db = fixture.CreateDbContext();
        var after = await db.Wishes.AsNoTracking().Where(w => childIds.Contains(w.Id)).ToListAsync();
        Assert.Equal(children, after.Count);
        Assert.All(after, w => Assert.Equal((WishStatus.Merged, (long?)null), (w.Status, w.MergedIntoWishId)));
    }

    // A child that goes (the retention deleted it) between reading the ids and locking them is simply not returned.
    [Fact]
    public async Task Delete_AChildDeletedBetweenReadingTheIdsAndTheLock_IsNoError()
    {
        var channel = await SeedChannelAsync();
        var target = await SeedRowAsync(channel, WishStatus.Removed);
        var vanishing = await SeedRowAsync(channel, WishStatus.Merged, w => w.MergedIntoWishId = target);
        var staying = await SeedRowAsync(channel, WishStatus.Merged, w => w.MergedIntoWishId = target);
        var ended = (await LoadWishAsync(target)).EndedAtUtc!.Value;
        var hook = new CommandHook(IsChildIdRead, async _ =>
        {
            await using var other = fixture.CreateDbContext();
            await other.Wishes.Where(w => w.Id == vanishing).ExecuteDeleteAsync();
        });

        var result = await Sut(fixture.CreateTaggedDbContext("wish-delete-child-gone", [hook]))
            .DeleteAsync(channel.Name, target, ended, Actor, await AccessAsync(channel));

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal(1, hook.Count(IsChildIdRead));
        Assert.Equal(1, hook.Count(IsChildLock));
        await AssertGoneAsync(target);
        await AssertGoneAsync(vanishing);
        Assert.Null((await LoadWishAsync(staying)).MergedIntoWishId);
    }

    // ------------------------------------------------------------------------------------ identity change under the lock (AK114)

    [Fact]
    public async Task Delete_WhileAnotherConnectionChangesTheChannelsTwitchIdUnderItsLock_WaitsThenIsAccessDenied_AndWritesNothing()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Kept);
        var ended = (await LoadWishAsync(wishId)).EndedAtUtc!.Value;
        var before = await SnapshotAsync(wishId);
        // The filter decided on the old id.
        var access = await AccessAsync(channel);
        const string tag = "wish-delete-identity-change";
        await using var identity = fixture.CreateDbContext();
        await using var change = await identity.Database.BeginTransactionAsync();
        await identity.Database.ExecuteSqlAsync(
            $"""UPDATE "Channels" SET "TwitchChannelId" = {Guid.NewGuid().ToString("N")[..12]} WHERE "Id" = {channel.Id}""");

        var delete = Sut(fixture.CreateTaggedDbContext(tag)).DeleteAsync(channel.Name, wishId, ended, Actor, access);
        await fixture.WaitUntilBlockedOnLockAsync(tag, delete);
        Assert.Contains("\"Channels\"", await fixture.WaitingQueryAsync(tag), StringComparison.Ordinal);
        await change.CommitAsync();

        Assert.Equal(WishOutcome.AccessDenied, (await delete).Outcome);
        await AssertUntouchedAsync(channel, wishId, before);
    }

    // ------------------------------------------------------------------------------------ helpers (delete races)

    private static bool IsChildLock(string command) => command.Contains("NOWAIT", StringComparison.Ordinal);

    private static bool IsChildIdRead(string command) =>
        command.Contains("\"MergedIntoWishId\" =", StringComparison.Ordinal) && !IsChildLock(command);
}
