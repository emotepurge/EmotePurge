using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The race-safe insert of authoritative, never-in-the-set slots (plan #372 E20, spec §18) against real
// Postgres.
[Collection("Postgres")]
public class ArchivedSlotUpsertTests(PostgresFixture fixture)
{
    private static readonly DateTime Now = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 3, 1, 10, 0, 0, 123, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task EnsureSlotsAsync_CreatesAuthoritativeNeverInSetSlots_AndReportsOnlyWhatItCreated()
    {
        var emoteId = await SeedEmoteAsync("slotupsert_create");
        await using var db = fixture.CreateDbContext();

        var result = await ArchivedSlotUpsert.EnsureSlotsAsync(
            db,
            [new(emoteId, "Alpha", T1), new(emoteId, "Beta", T1), new(emoteId, "Alpha", T2), new(emoteId, "Alpha", T1)],
            Now,
            CancellationToken.None);

        Assert.Equal(3, result.Created.Count);
        Assert.Equal(3, result.SlotIds.Count);
        var rows = await db.EmoteSlots.AsNoTracking().Where(s => s.EmoteId == emoteId).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, s =>
        {
            Assert.True(s.AddedAtFromRest);
            Assert.True(s.IsArchived);
            Assert.Null(s.ArchivedAt);
            Assert.False(s.SeedUnsettled);
            Assert.Equal(Now, s.LastSyncedAt);
            Assert.Equal(result.SlotIds[new ArchivedSlotKey(emoteId, s.Name, s.AddedAt!.Value)], s.Id);
        });
        Assert.Contains(rows, s => s.Name == "Alpha" && s.AddedAt == T1);
    }

    [Fact]
    public async Task EnsureSlotsAsync_SecondCallIsANoOp_AndAnExistingActiveSlotStaysExactlyAsItIs()
    {
        var emoteId = await SeedEmoteAsync("slotupsert_existing");
        var lastSynced = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        string activeId;
        await using (var seed = fixture.CreateDbContext())
        {
            var active = new EmoteSlot
            {
                EmoteId = emoteId,
                Name = "Live",
                AddedAt = T1,
                AddedAtFromRest = true,
                IsArchived = false,
                LastSyncedAt = lastSynced,
            };
            seed.EmoteSlots.Add(active);
            await seed.SaveChangesAsync();
            activeId = active.Id;
        }

        await using var db = fixture.CreateDbContext();
        ArchivedSlotKey[] keys = [new(emoteId, "Live", T1), new(emoteId, "Gone", T2)];

        var first = await ArchivedSlotUpsert.EnsureSlotsAsync(db, keys, Now, CancellationToken.None);
        var second = await ArchivedSlotUpsert.EnsureSlotsAsync(db, keys, Now.AddHours(1), CancellationToken.None);

        Assert.Equal([new ArchivedSlotKey(emoteId, "Gone", T2)], first.Created);
        Assert.Empty(second.Created);
        Assert.Equal(activeId, first.SlotIds[keys[0]]);
        Assert.Equal(first.SlotIds, second.SlotIds);
        var rows = await db.EmoteSlots.AsNoTracking().Where(s => s.EmoteId == emoteId).ToListAsync();
        Assert.Equal(2, rows.Count);
        var live = rows.Single(s => s.Id == activeId);
        Assert.Equal((false, null, lastSynced), (live.IsArchived, live.ArchivedAt, live.LastSyncedAt));
        Assert.Equal(Now, rows.Single(s => s.Name == "Gone").LastSyncedAt);
    }

    [Fact]
    public async Task EnsureSlotsAsync_IgnoresANonAuthoritativeSlotOfTheSameKey_AndStoresEveryDateKindAsUtc()
    {
        var emoteId = await SeedEmoteAsync("slotupsert_kinds");
        await using (var seed = fixture.CreateDbContext())
        {
            // Provisional: not part of the partial unique index, so it must not stand in for the key.
            seed.EmoteSlots.Add(new EmoteSlot { EmoteId = emoteId, Name = "Prov", AddedAt = T1, AddedAtFromRest = false });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var local = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Local);
        var unspecified = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Unspecified);

        var result = await ArchivedSlotUpsert.EnsureSlotsAsync(
            db,
            [new(emoteId, "Prov", T1), new(emoteId, "Local", local), new(emoteId, "Unspec", unspecified)],
            Now,
            CancellationToken.None);

        Assert.Equal(3, result.Created.Count);
        var rows = await db.EmoteSlots.AsNoTracking().Where(s => s.EmoteId == emoteId).ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.Equal(local.ToUniversalTime(), rows.Single(s => s.Name == "Local").AddedAt);
        Assert.Equal(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), rows.Single(s => s.Name == "Unspec").AddedAt);
        Assert.Equal(2, rows.Count(s => s.Name == "Prov"));
    }

    [Fact]
    public async Task EnsureSlotsAsync_WithoutKeys_AsksNothing_AndAMissingEmoteSurfacesAsAForeignKeyFailure()
    {
        await using var db = fixture.CreateDbContext();

        var empty = await ArchivedSlotUpsert.EnsureSlotsAsync(db, [], Now, CancellationToken.None);
        Assert.Empty(empty.SlotIds);
        Assert.Empty(empty.Created);

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => ArchivedSlotUpsert.EnsureSlotsAsync(db, [new("no-such-emote", "A", T1)], Now, CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, failure.SqlState);
    }

    [Fact]
    public async Task EnsureSlotsAsync_ASlotInsertedByAConcurrentWriterFirst_IsWaitedFor_AndNotReportedAsCreated()
    {
        var emoteId = await SeedEmoteAsync("slotupsert_race");

        await using var racer = fixture.CreateDbContext();
        await using var racerTransaction = await racer.Database.BeginTransactionAsync();
        racer.EmoteSlots.Add(new EmoteSlot { EmoteId = emoteId, Name = "Race", AddedAt = T1, AddedAtFromRest = true });
        await racer.SaveChangesAsync();

        await using var db = fixture.CreateTaggedDbContext("slotupsert-race");
        var pending = ArchivedSlotUpsert.EnsureSlotsAsync(db, [new(emoteId, "Race", T1)], Now, CancellationToken.None);
        await fixture.WaitUntilBlockedOnLockAsync("slotupsert-race", pending);
        await racerTransaction.CommitAsync();

        var result = await pending;
        Assert.Empty(result.Created);
        Assert.Single(result.SlotIds);
        await using var verify = fixture.CreateDbContext();
        Assert.Single(await verify.EmoteSlots.Where(s => s.EmoteId == emoteId).ToListAsync());
    }

    private async Task<string> SeedEmoteAsync(string channelName)
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        var emote = new Emote { ChannelId = channel.Id, SevenTvEmoteId = "7tv-" + channelName, Name = "E", ImageUrl = "i" };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote.Id;
    }
}
