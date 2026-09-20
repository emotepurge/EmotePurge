using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Against real Postgres, not an in-memory provider: the whole point of this service is the partial
// unique index (IX_ChannelEmoteSetObservations_ChannelId, spec 4.3/AK 18) and the two-save
// transaction a set switch needs to respect it (AK 16) — neither is something an in-memory provider
// would ever reject or race the way the real database does.
[Collection("Postgres")]
public class ChannelEmoteSetObservationServiceTests(PostgresFixture fixture)
{
    private const string SetA = "64c9e0f0aa1234567890a000";
    private const string SetB = "64c9e0f0aa1234567890b111";

    [Fact]
    public async Task RecordObservedSetAsync_WithNoOpenInterval_OpensOneForTheReportedSet()
    {
        // AK 16, first half: a channel's first-ever sync (or one that resumes after a close) has
        // nothing to close — just a fresh open interval, tracked into the caller's own context.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "obsopen1");
        var service = new ChannelEmoteSetObservationService(db);
        var before = DateTime.UtcNow;

        await service.RecordObservedSetAsync(channel.Id, SetA, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = fixture.CreateDbContext();
        var row = await verify.ChannelEmoteSetObservations.AsNoTracking().SingleAsync(o => o.ChannelId == channel.Id);
        Assert.Equal(SetA, row.SevenTvEmoteSetId);
        Assert.Null(row.ObservedToUtc);
        Assert.Null(row.ClosedBy);
        Assert.InRange(row.ObservedFromUtc, before.AddMilliseconds(-1), DateTime.UtcNow.AddMilliseconds(1));
    }

    [Fact]
    public async Task RecordObservedSetAsync_WithOpenIntervalForTheSameSet_DoesNothing()
    {
        // The routine case on every periodic resync where 7TV reports the set we already have open —
        // by far the most common call, and it must not touch the row at all.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "obssameset1");
        var opened = await SeedOpenIntervalAsync(db, channel.Id, SetA);
        // Read back as persisted rather than trusting the in-memory value used to build the insert:
        // Postgres' timestamptz has microsecond precision, DateTime a finer tick, so the two can
        // differ by a sub-microsecond remainder even though nothing here ever changes the row.
        var openedFromUtc = await db.ChannelEmoteSetObservations.AsNoTracking()
            .Where(o => o.Id == opened.Id).Select(o => o.ObservedFromUtc).SingleAsync();
        var service = new ChannelEmoteSetObservationService(db);

        await service.RecordObservedSetAsync(channel.Id, SetA, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = fixture.CreateDbContext();
        var row = await verify.ChannelEmoteSetObservations.AsNoTracking().SingleAsync(o => o.ChannelId == channel.Id);
        Assert.Equal(opened.Id, row.Id);
        Assert.Equal(openedFromUtc, row.ObservedFromUtc);
        Assert.Null(row.ObservedToUtc);
    }

    [Fact]
    public async Task RecordObservedSetAsync_WithOpenIntervalForADifferentSet_ClosesTheOldOneAndOpensTheNewOne()
    {
        // AK 16, second half (happy path): a genuine switch closes the old interval with
        // ClosedBy = SetSwitch and opens a new one for the newly reported set.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "obsswitch1");
        var opened = await SeedOpenIntervalAsync(db, channel.Id, SetA);
        var service = new ChannelEmoteSetObservationService(db);
        var before = DateTime.UtcNow;

        await service.RecordObservedSetAsync(channel.Id, SetB, CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.ChannelEmoteSetObservations.AsNoTracking()
            .Where(o => o.ChannelId == channel.Id)
            .OrderBy(o => o.Id)
            .ToListAsync();
        Assert.Equal(2, rows.Count);

        var closed = rows[0];
        Assert.Equal(opened.Id, closed.Id);
        Assert.Equal(SetA, closed.SevenTvEmoteSetId);
        Assert.Equal(ChannelEmoteSetObservationClosedBy.SetSwitch, closed.ClosedBy);
        Assert.NotNull(closed.ObservedToUtc);

        var reopened = rows[1];
        Assert.Equal(SetB, reopened.SevenTvEmoteSetId);
        Assert.Null(reopened.ObservedToUtc);
        Assert.Null(reopened.ClosedBy);
        // The new interval's start is the same instant the old one's end was stamped with —
        // no gap and no overlap between the two.
        Assert.Equal(closed.ObservedToUtc, reopened.ObservedFromUtc);
        Assert.InRange(reopened.ObservedFromUtc, before.AddMilliseconds(-1), DateTime.UtcNow.AddMilliseconds(1));
    }

    [Fact]
    public async Task RecordObservedSetAsync_ExceptionBetweenTheCloseAndTheOpen_RollsBackBothChanges()
    {
        // AK 16's rollback proof: the switch is two explicit SaveChangesAsync calls in one
        // transaction (see the comment in ChannelEmoteSetObservationService), specifically so that
        // EF Core cannot reorder the insert ahead of the update and momentarily violate the partial
        // unique index. An interceptor that throws on the *second* SaveChangesAsync call lets the
        // close succeed inside the transaction before failing the open — proving the close alone is
        // not left committed.
        await using var seedDb = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(seedDb, "obsrollback1");
        var opened = await SeedOpenIntervalAsync(seedDb, channel.Id, SetA);

        var interceptor = new ThrowOnNthSaveChangesInterceptor(throwOnCallNumber: 2);
        await using var faultyDb = fixture.CreateDbContext([interceptor]);
        var service = new ChannelEmoteSetObservationService(faultyDb);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RecordObservedSetAsync(channel.Id, SetB, CancellationToken.None));
        Assert.Equal(2, interceptor.CallCount);

        await using var verify = fixture.CreateDbContext();
        var row = await verify.ChannelEmoteSetObservations.AsNoTracking().SingleAsync(o => o.ChannelId == channel.Id);
        Assert.Equal(opened.Id, row.Id);
        Assert.Equal(SetA, row.SevenTvEmoteSetId);
        Assert.Null(row.ObservedToUtc);
        Assert.Null(row.ClosedBy);
    }

    [Fact]
    public async Task RecordObservedSetAsync_Rejoin_AfterAClose_OpensABrandNewIntervalEvenForTheSameSetId()
    {
        // AK 17's rejoin case: the decision looks only at "is an interval open right now", never at
        // history, so a channel that was closed (e.g. by leave) and observed again under the exact
        // same set id gets a fresh row rather than reusing/reopening the old one.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "obsrejoin1");
        var closed = await SeedClosedIntervalAsync(db, channel.Id, SetA, ChannelEmoteSetObservationClosedBy.Leave);
        var service = new ChannelEmoteSetObservationService(db);

        await service.RecordObservedSetAsync(channel.Id, SetA, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.ChannelEmoteSetObservations.AsNoTracking()
            .Where(o => o.ChannelId == channel.Id)
            .OrderBy(o => o.Id)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(closed.Id, rows[0].Id);
        Assert.NotEqual(closed.Id, rows[1].Id);
        Assert.Equal(SetA, rows[1].SevenTvEmoteSetId);
        Assert.Null(rows[1].ObservedToUtc);
    }

    [Theory]
    [InlineData(ChannelEmoteSetObservationClosedBy.Leave)]
    [InlineData(ChannelEmoteSetObservationClosedBy.Rename)]
    [InlineData(ChannelEmoteSetObservationClosedBy.Merge)]
    public async Task CloseOpenIntervalAsync_ClosesTheOpenIntervalWithTheGivenReason(string closedBy)
    {
        // AK 17: the three ClosedBy values every non-switch call site (LeaveAsync, both rename call
        // sites, the surviving side of a merge) passes through this one method.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, $"obsclose_{closedBy}");
        var opened = await SeedOpenIntervalAsync(db, channel.Id, SetA);
        var service = new ChannelEmoteSetObservationService(db);
        var before = DateTime.UtcNow;

        await service.CloseOpenIntervalAsync(channel.Id, closedBy, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = fixture.CreateDbContext();
        var row = await verify.ChannelEmoteSetObservations.AsNoTracking().SingleAsync(o => o.Id == opened.Id);
        Assert.Equal(closedBy, row.ClosedBy);
        Assert.NotNull(row.ObservedToUtc);
        Assert.InRange(row.ObservedToUtc!.Value, before.AddMilliseconds(-1), DateTime.UtcNow.AddMilliseconds(1));
    }

    [Fact]
    public async Task CloseOpenIntervalAsync_WithNoOpenInterval_DoesNothing()
    {
        // Defensive: every real call site closes a channel expected to have an open interval, but a
        // channel with none (e.g. it was already closed) must not throw or fabricate a row.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "obsnoopen1");
        var service = new ChannelEmoteSetObservationService(db);

        await service.CloseOpenIntervalAsync(channel.Id, ChannelEmoteSetObservationClosedBy.Leave, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.ChannelEmoteSetObservations.AsNoTracking().AnyAsync(o => o.ChannelId == channel.Id));
    }

    [Fact]
    public async Task PartialUniqueIndex_RejectsASecondOpenIntervalForTheSameChannel()
    {
        // AK 18: the backstop the service's own single-open-row logic should never need in
        // practice, proven directly against the real index rather than assumed from the migration.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "obsindex1");
        await SeedOpenIntervalAsync(db, channel.Id, SetA);

        db.ChannelEmoteSetObservations.Add(new ChannelEmoteSetObservation
        {
            ChannelId = channel.Id,
            SevenTvEmoteSetId = SetB,
            ObservedFromUtc = DateTime.UtcNow,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal("23505", ((PostgresException)exception.InnerException!).SqlState);
    }

    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string name)
    {
        var channel = new Channel { ChannelName = ChannelName.Normalize(name), IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private static async Task<ChannelEmoteSetObservation> SeedOpenIntervalAsync(AppDbContext db, string channelId, string emoteSetId)
    {
        var interval = new ChannelEmoteSetObservation
        {
            ChannelId = channelId,
            SevenTvEmoteSetId = emoteSetId,
            ObservedFromUtc = DateTime.UtcNow.AddHours(-1),
        };
        db.ChannelEmoteSetObservations.Add(interval);
        await db.SaveChangesAsync();
        return interval;
    }

    private static async Task<ChannelEmoteSetObservation> SeedClosedIntervalAsync(
        AppDbContext db, string channelId, string emoteSetId, string closedBy)
    {
        var interval = new ChannelEmoteSetObservation
        {
            ChannelId = channelId,
            SevenTvEmoteSetId = emoteSetId,
            ObservedFromUtc = DateTime.UtcNow.AddHours(-2),
            ObservedToUtc = DateTime.UtcNow.AddHours(-1),
            ClosedBy = closedBy,
        };
        db.ChannelEmoteSetObservations.Add(interval);
        await db.SaveChangesAsync();
        return interval;
    }

    // Throws on its Nth invocation, letting every earlier SaveChangesAsync call on the same
    // AppDbContext complete normally first — the seam that lets
    // RecordObservedSetAsync_ExceptionBetweenTheCloseAndTheOpen_RollsBackBothChanges fail the second
    // of the two saves without touching production code.
    private sealed class ThrowOnNthSaveChangesInterceptor(int throwOnCallNumber) : SaveChangesInterceptor
    {
        public int CallCount { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == throwOnCallNumber)
            {
                throw new InvalidOperationException("Injected failure for the rollback test.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
