using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The write methods only stage (the caller owns the transaction), so every test saves explicitly and
// reads back through a second context.
[Collection("Postgres")]
public class BroadcasterChannelLockServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task LockAsync_OnAnEmptyTable_ThenSave_MakesTheDateReadable()
    {
        var id = NewId();
        var lockedAt = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

        await using (var db = fixture.CreateDbContext())
        {
            await new BroadcasterChannelLockService(db).LockAsync(id, lockedAt);
            await db.SaveChangesAsync();
        }

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(lockedAt, await new BroadcasterChannelLockService(verify).GetLockedAtUtcAsync(id));
    }

    [Fact]
    public async Task LockAsync_Twice_KeepsOneRowWithTheLatestDate()
    {
        var id = NewId();
        var first = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var second = new DateTime(2026, 10, 8, 9, 30, 0, DateTimeKind.Utc);

        await using (var db = fixture.CreateDbContext())
        {
            var service = new BroadcasterChannelLockService(db);
            await service.LockAsync(id, first);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            await new BroadcasterChannelLockService(db).LockAsync(id, second);
            await db.SaveChangesAsync();
        }

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.BroadcasterChannelLocks.CountAsync(l => l.TwitchChannelId == id));
        Assert.Equal(second, await new BroadcasterChannelLockService(verify).GetLockedAtUtcAsync(id));
    }

    [Fact]
    public async Task UnlockAsync_RemovesTheRow_AndIsIdempotent()
    {
        var id = NewId();
        await using (var db = fixture.CreateDbContext())
        {
            await new BroadcasterChannelLockService(db).LockAsync(id, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var service = new BroadcasterChannelLockService(db);
            Assert.True(await service.UnlockAsync(id));
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var service = new BroadcasterChannelLockService(db);
            Assert.Null(await service.GetLockedAtUtcAsync(id));
            Assert.False(await service.UnlockAsync(id));
            await db.SaveChangesAsync();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GetLockedAtUtcAsync_WithANullOrEmptyId_ReturnsNull(string? id)
    {
        await using var db = fixture.CreateDbContext();

        Assert.Null(await new BroadcasterChannelLockService(db).GetLockedAtUtcAsync(id));
    }

    [Fact]
    public async Task LockAsync_WithoutSaveChanges_IsInvisibleToAnotherContext()
    {
        var id = NewId();
        await using var db = fixture.CreateDbContext();
        await new BroadcasterChannelLockService(db).LockAsync(id, DateTime.UtcNow);

        await using var other = fixture.CreateDbContext();
        Assert.Null(await new BroadcasterChannelLockService(other).GetLockedAtUtcAsync(id));
    }

    [Fact]
    public async Task TheMigratedSchema_HasTheLockTableKeyedByTheTwitchChannelId()
    {
        await using var db = fixture.CreateDbContext();

        var keyColumns = await db.Database.SqlQuery<string>($"""
            SELECT kcu.column_name AS "Value"
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema
            WHERE tc.table_name = 'BroadcasterChannelLocks' AND tc.constraint_type = 'PRIMARY KEY'
            """).ToListAsync();

        Assert.Equal(["TwitchChannelId"], keyColumns);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static string NewId() => $"lock-{Guid.NewGuid():N}";
}
