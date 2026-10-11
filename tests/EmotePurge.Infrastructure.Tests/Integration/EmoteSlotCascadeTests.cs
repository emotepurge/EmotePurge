using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The cascade proof of the emote-slot schema (plan #371, section 5): the two new foreign keys
// (EmoteSlots -> Emotes and UsageStats -> EmoteSlots, both CASCADE) leave every existing delete path
// working. UsageStats is now reachable from an emote on two paths (directly and through its slot), so
// the diamond is exercised explicitly. Runs against the shared head-schema database.
[Collection("Postgres")]
public class EmoteSlotCascadeTests(PostgresFixture fixture)
{
    private const string SetId = "01CASCADETESTSET0000000000";

    [Fact]
    public async Task ChannelPurge_RemovesSlotsAndTheirUsageRows_AndTheRunWithItsSnapshotRow()
    {
        var seeded = await SeedAsync();

        await using (var db = fixture.CreateDbContext())
        {
            Assert.True(await CreateService(db).PurgeAsync(seeded.ChannelName, AuditActor.System));
        }

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Channels.AnyAsync(c => c.Id == seeded.ChannelId));
        Assert.False(await verify.Emotes.AnyAsync(e => e.Id == seeded.EmoteId));
        Assert.False(await verify.EmoteSlots.AnyAsync(s => s.EmoteId == seeded.EmoteId));
        Assert.False(await verify.UsageStats.AnyAsync(u => u.EmoteId == seeded.EmoteId));
        Assert.False(await verify.ChatLogBackfillRuns.AnyAsync(r => r.Id == seeded.RunId));
        Assert.False(await verify.ChatLogBackfillRunEmotes.AnyAsync(re => re.RunId == seeded.RunId));
    }

    [Fact]
    public async Task DeletingTheEmoteRow_RemovesItsSlotsAndUsageRows_DespiteTheTwoPathsToUsageStats()
    {
        var seeded = await SeedAsync();

        await using var db = fixture.CreateDbContext();
        var deleted = await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Emotes" WHERE "Id" = {0};""", seeded.EmoteId);

        Assert.Equal(1, deleted);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteSlots.AnyAsync(s => s.EmoteId == seeded.EmoteId));
        Assert.False(await verify.UsageStats.AnyAsync(u => u.EmoteId == seeded.EmoteId));
        // The channel and the run are not part of the emote's cascade; the snapshot row has no FK to it.
        Assert.True(await verify.Channels.AnyAsync(c => c.Id == seeded.ChannelId));
        Assert.True(await verify.ChatLogBackfillRuns.AnyAsync(r => r.Id == seeded.RunId));
    }

    [Fact]
    public async Task DeletingASlotRow_RemovesOnlyItsUsageRows_AndKeepsTheEmoteAndTheSnapshotsSlotIdText()
    {
        var seeded = await SeedAsync();

        await using var db = fixture.CreateDbContext();
        var deleted = await db.Database.ExecuteSqlRawAsync("""DELETE FROM "EmoteSlots" WHERE "Id" = {0};""", seeded.FirstSlotId);

        Assert.Equal(1, deleted);
        await using var verify = fixture.CreateDbContext();
        Assert.True(await verify.Emotes.AnyAsync(e => e.Id == seeded.EmoteId));
        Assert.False(await verify.EmoteSlots.AnyAsync(s => s.Id == seeded.FirstSlotId));
        Assert.True(await verify.EmoteSlots.AnyAsync(s => s.Id == seeded.SecondSlotId));
        Assert.False(await verify.UsageStats.AnyAsync(u => u.SlotId == seeded.FirstSlotId));
        Assert.True(await verify.UsageStats.AnyAsync(u => u.SlotId == seeded.SecondSlotId));
        // No FK on the snapshot's SlotId (by design): the row keeps the text of the vanished slot.
        var snapshot = await verify.ChatLogBackfillRunEmotes.SingleAsync(re => re.RunId == seeded.RunId);
        Assert.Equal(seeded.FirstSlotId, snapshot.SlotId);
    }

    private static ChannelService CreateService(AppDbContext db) =>
        new(
            db,
            Substitute.For<IRedisPublisher>(),
            Substitute.For<IChannelIdentityService>(),
            new ChannelEmoteSetObservationService(db),
            new BroadcasterChannelLockService(db),
            // Uncapped: the shared collection database accumulates active channels across tests.
            new ChannelCapacityOptions { MaxActiveChannels = int.MaxValue },
            Substitute.For<IExcludedChannelFilter>(),
            NullLogger<ChannelService>.Instance);

    // One channel with an emote that carries two slots, a usage row on each, and a finished backfill run
    // whose snapshot row names the first slot.
    private async Task<Seeded> SeedAsync()
    {
        var name = $"cascade{Guid.NewGuid():N}"[..20];
        await using var db = fixture.CreateDbContext();

        var channel = new Channel { ChannelName = name, IsBotActive = false };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var emote = new Emote
        {
            ChannelId = channel.Id,
            Name = "CascadeEmote",
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var first = new EmoteSlot { EmoteId = emote.Id, Name = "CascadeEmote", SeedUnsettled = true };
        var second = new EmoteSlot { EmoteId = emote.Id, Name = "CascadeAlias", SeedUnsettled = true };
        db.EmoteSlots.AddRange(first, second);
        await db.SaveChangesAsync();

        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, EmoteSetId = SetId, SlotId = first.Id, Date = new DateOnly(2026, 9, 1), UseCount = 3 },
            new UsageStat { EmoteId = emote.Id, EmoteSetId = SetId, SlotId = second.Id, Date = new DateOnly(2026, 9, 2), UseCount = 4 });
        var run = new ChatLogBackfillRun
        {
            ChannelId = channel.Id,
            Status = ChatLogBackfillRunStatus.Completed,
            RequestedMonths = 1,
            WindowFrom = new DateOnly(2026, 8, 1),
            WindowTo = new DateOnly(2026, 9, 1),
            WeeksTotal = 5,
            EmoteSetId = SetId,
            ArchiveBaseUrl = "https://logs.cyex.app/",
            RequestedByTwitchUserId = "42",
            RequestedByLogin = "cascade",
            RequestedAtUtc = DateTime.UtcNow
        };
        db.ChatLogBackfillRuns.Add(run);
        await db.SaveChangesAsync();

        db.ChatLogBackfillRunEmotes.Add(new ChatLogBackfillRunEmote
        {
            RunId = run.Id,
            EmoteId = emote.Id,
            SevenTvEmoteId = emote.SevenTvEmoteId,
            Name = emote.Name,
            SlotId = first.Id
        });
        await db.SaveChangesAsync();

        return new Seeded(channel.Id, name, emote.Id, first.Id, second.Id, run.Id);
    }

    private sealed record Seeded(string ChannelId, string ChannelName, string EmoteId, string FirstSlotId, string SecondSlotId, long RunId);
}
