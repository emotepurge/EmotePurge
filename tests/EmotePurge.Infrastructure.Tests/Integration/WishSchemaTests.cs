using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The schema contract of the AddWishes migration (wish emotes, child 1, T2): the partial unique indexes,
// the CHECKs, the cascade and SET NULL behaviour, and that the migration leaves nothing pending. Runs
// against the shared head-schema database.
[Collection("Postgres")]
public partial class WishSchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_AreFullyApplied_AndTheModelHasNoDriftAgainstThem()
    {
        await using var db = fixture.CreateDbContext();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task SecondRunningWishForTheSameEmote_FailsOnTheIndex_AnEndedOneBesideItDoesNot()
    {
        var seeded = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        db.Wishes.Add(NewWish(seeded.ChannelId, "emote1", WishStatus.Approved));
        db.Wishes.Add(NewWish(seeded.ChannelId, "emote1", WishStatus.Removed));
        db.Wishes.Add(NewWish(seeded.ChannelId, "emote1", WishStatus.Kept));
        await db.SaveChangesAsync();

        foreach (var running in new[] { WishStatus.Approved, WishStatus.Unconfirmed, WishStatus.Active, WishStatus.Presented })
        {
            await using var attempt = fixture.CreateDbContext();
            attempt.Wishes.Add(NewWish(seeded.ChannelId, "emote1", running));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => attempt.SaveChangesAsync());
            Assert.Equal("23505", Assert.IsType<PostgresException>(ex.InnerException).SqlState);
        }

        // Another emote id in the same channel is unaffected.
        await using var other = fixture.CreateDbContext();
        other.Wishes.Add(NewWish(seeded.ChannelId, "emote2", WishStatus.Approved));
        await other.SaveChangesAsync();
    }

    [Fact]
    public async Task TwoLiveWishesOnOneSlot_FailOnTheIndex_AnEndedOneBesideALiveOneDoesNot()
    {
        var seeded = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var ended = NewWish(seeded.ChannelId, "emote1", WishStatus.Removed);
        ended.SlotId = seeded.SlotId;
        var live = NewWish(seeded.ChannelId, "emote1", WishStatus.Active);
        live.SlotId = seeded.SlotId;
        db.Wishes.AddRange(ended, live);
        await db.SaveChangesAsync();

        // A second live wish on the slot: a different emote id keeps the (channel, emote) index out of the way.
        await using var attempt = fixture.CreateDbContext();
        var clash = NewWish(seeded.ChannelId, "emote2", WishStatus.Presented);
        clash.SlotId = seeded.SlotId;
        attempt.Wishes.Add(clash);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => attempt.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    // Operator ruling 2026-11-18: a kept wish holds its slot like a live one; other ended wishes do not.
    [Fact]
    public async Task AKeptWishHoldsItsSlot_AgainstLiveAndKeptWishes_NotAgainstOtherEndedOnes()
    {
        var seeded = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var kept = NewWish(seeded.ChannelId, "emote1", WishStatus.Kept);
        kept.SlotId = seeded.SlotId;
        kept.EndedAtUtc = DateTime.UtcNow;
        var removed = NewWish(seeded.ChannelId, "emote2", WishStatus.RemovedExternally);
        removed.SlotId = seeded.SlotId;
        removed.EndedAtUtc = DateTime.UtcNow;
        db.Wishes.AddRange(kept, removed);
        await db.SaveChangesAsync();

        // Distinct emote ids keep the (channel, emote) index out of the way.
        var clashes = new[] { (WishStatus.Active, "emote3"), (WishStatus.Presented, "emote4"), (WishStatus.Kept, "emote5") };
        foreach (var (status, emoteId) in clashes)
        {
            await using var attempt = fixture.CreateDbContext();
            var clash = NewWish(seeded.ChannelId, emoteId, status);
            clash.SlotId = seeded.SlotId;
            clash.EndedAtUtc = WishStatus.IsEnded(status) ? DateTime.UtcNow : null;
            attempt.Wishes.Add(clash);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => attempt.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal(("23505", "IX_Wishes_SlotId_Live"), (postgres.SqlState, postgres.ConstraintName));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public async Task DurationDaysOutsideTheBounds_FailOnTheCheck(int days)
    {
        var seeded = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        var wish = NewWish(seeded.ChannelId, "emote1", WishStatus.Approved);
        wish.DurationDays = days;
        db.Wishes.Add(wish);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23514", Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [Theory]
    [InlineData(1, 1, null, true)]
    [InlineData(365, 30, 0.5, true)]
    [InlineData(0, 7, null, false)]
    [InlineData(366, 7, null, false)]
    [InlineData(30, 0, null, false)]
    [InlineData(30, 31, null, false)]
    [InlineData(30, 7, 0.0, false)]
    [InlineData(30, 7, -1.0, false)]
    public async Task ChannelSettingsChecks_AcceptTheBoundsAndRejectOutsideValues(int duration, int lead, double? threshold, bool accepted)
    {
        var seeded = await SeedAsync();
        await using var db = fixture.CreateDbContext();
        db.ChannelWishSettings.Add(new ChannelWishSettings
        {
            ChannelId = seeded.ChannelId,
            DefaultDurationDays = duration,
            ProbationLeadDays = lead,
            ProtectionThresholdPerDay = threshold is null ? null : (decimal)threshold.Value
        });

        if (accepted)
        {
            await db.SaveChangesAsync();
            return;
        }

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23514", Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [Fact]
    public async Task DeletingTheChannel_TakesItsWishesAndSettingsAlong()
    {
        var seeded = await SeedAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.Wishes.Add(NewWish(seeded.ChannelId, "emote1", WishStatus.Approved));
            db.ChannelWishSettings.Add(new ChannelWishSettings { ChannelId = seeded.ChannelId });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(1, await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Channels" WHERE "Id" = {0};""", seeded.ChannelId));
        }

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Wishes.AnyAsync(w => w.ChannelId == seeded.ChannelId));
        Assert.False(await verify.ChannelWishSettings.AnyAsync(s => s.ChannelId == seeded.ChannelId));
    }

    [Fact]
    public async Task DeletingTheSlot_NullsSlotId_AndKeepsTheWish()
    {
        var seeded = await SeedAsync();
        long wishId;
        await using (var db = fixture.CreateDbContext())
        {
            var wish = NewWish(seeded.ChannelId, "emote1", WishStatus.Active);
            wish.SlotId = seeded.SlotId;
            db.Wishes.Add(wish);
            await db.SaveChangesAsync();
            wishId = wish.Id;
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(1, await db.Database.ExecuteSqlRawAsync("""DELETE FROM "EmoteSlots" WHERE "Id" = {0};""", seeded.SlotId));
        }

        await using var verify = fixture.CreateDbContext();
        var kept = await verify.Wishes.SingleAsync(w => w.Id == wishId);
        Assert.Null(kept.SlotId);
        Assert.Equal(WishStatus.Active, kept.Status);
    }

    [Fact]
    public async Task DeletingTheMergeTarget_NullsTheReference_AndKeepsTheMergedWish()
    {
        var seeded = await SeedAsync();
        long mergedId;
        long targetId;
        await using (var db = fixture.CreateDbContext())
        {
            var target = NewWish(seeded.ChannelId, "emote1", WishStatus.Active);
            db.Wishes.Add(target);
            await db.SaveChangesAsync();
            var merged = NewWish(seeded.ChannelId, "emote1", WishStatus.Merged);
            merged.MergedIntoWishId = target.Id;
            db.Wishes.Add(merged);
            await db.SaveChangesAsync();
            mergedId = merged.Id;
            targetId = target.Id;
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(1, await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Wishes" WHERE "Id" = {0};""", targetId));
        }

        await using var verify = fixture.CreateDbContext();
        Assert.Null((await verify.Wishes.SingleAsync(w => w.Id == mergedId)).MergedIntoWishId);
    }

    [Theory]
    [InlineData("LinkOrigin")]
    [InlineData("SlotAddedAtUtc")]
    [InlineData("SlotConfirmedAtUtc")]
    [InlineData("FirstActivatedAtUtc")]
    public async Task ColumnsBeyondTheSpecTable_AreNullableWithoutDefault(string column)
    {
        await using var db = fixture.CreateDbContext();
        var rows = await db.Database.SqlQueryRaw<string>(
            """
            SELECT is_nullable || '|' || coalesce(column_default, '') AS "Value"
            FROM information_schema.columns
            WHERE table_name = 'Wishes' AND column_name = {0}
            """, column).ToListAsync();

        Assert.Equal(["YES|"], rows);
    }

    private static Wish NewWish(string channelId, string sevenTvEmoteId, string status) => new()
    {
        ChannelId = channelId,
        SevenTvEmoteId = sevenTvEmoteId,
        EmoteName = "Emote",
        Alias = "Alias",
        Source = WishSource.Manual,
        Status = status,
        StatusChangedAtUtc = DateTime.UtcNow,
        CreatedAtUtc = DateTime.UtcNow
    };

    private async Task<Seeded> SeedAsync()
    {
        var name = $"wish{Guid.NewGuid():N}"[..20];
        await using var db = fixture.CreateDbContext();

        var channel = new Channel { ChannelName = name, IsBotActive = false };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var emote = new Emote
        {
            ChannelId = channel.Id,
            Name = "WishEmote",
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var slot = new EmoteSlot { EmoteId = emote.Id, Name = "WishEmote", SeedUnsettled = true };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();

        return new Seeded(channel.Id, slot.Id);
    }

    private sealed record Seeded(string ChannelId, string SlotId);
}
