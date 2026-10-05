using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The emote-tag tables hang off the channel with FK cascades (#201): both channel purges must take
// them down, and deleting a tag must take its entries. Against real Postgres, because the cascade is
// the database's, not EF's. T-C adds placements, activations, operations and leave observations: the
// purge must leave all six tag-related tables empty, and the placement-to-entry FK is the database's
// guarantee that no placement outlives its entry.
[Collection("Postgres")]
public class EmoteTagCascadeTests(PostgresFixture fixture)
{
    // 26 characters, like real 7TV set ULIDs.
    private const string SetA = "01HZSETAAAAAAAAAAAAAAAAAAA";
    private const string SetB = "01HZSETBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public async Task PurgeAsync_RemovesAllSixTagTables()
    {
        var seeded = await SeedChannelWithTagAsync("emotetagpurge");

        await using var db = fixture.CreateDbContext();
        var purged = await CreateService(db).PurgeAsync("emotetagpurge", new AuditActor("tag-mod", "tagmod"));

        Assert.True(purged);
        await AssertTagTablesEmptyAsync(seeded);
    }

    [Fact]
    public async Task PurgeIfInactiveSince_RemovesAllSixTagTables()
    {
        var cutoff = DateTime.UtcNow.AddDays(-180);
        cutoff = cutoff.AddTicks(-(cutoff.Ticks % TimeSpan.TicksPerSecond));
        var seeded = await SeedChannelWithTagAsync("emotetagretention", deactivatedAtUtc: cutoff.AddMinutes(-1));

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db).PurgeIfInactiveSinceAsync("emotetagretention", cutoff, AuditActor.System);

        Assert.Equal(ChannelRetentionPurgeResult.Purged, result);
        await AssertTagTablesEmptyAsync(seeded);
    }

    [Fact]
    public async Task DeletingATag_RemovesItsEntries_AndLeavesTheChannelsOtherTags()
    {
        var seeded = await SeedChannelWithTagAsync("emotetagdelete");
        long otherTagId;
        await using (var db = fixture.CreateDbContext())
        {
            var other = new EmoteTag { ChannelId = seeded.ChannelId, Name = "Other", NormalizedName = "other", CreatedAtUtc = DateTime.UtcNow };
            db.EmoteTags.Add(other);
            await db.SaveChangesAsync();
            db.EmoteTagEntries.Add(Entry(other.Id));
            await db.SaveChangesAsync();
            otherTagId = other.Id;
        }

        await using (var db = fixture.CreateDbContext())
        {
            await db.EmoteTags.Where(t => t.Id == seeded.TagId).ExecuteDeleteAsync();
        }

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagEntries.AnyAsync(e => e.TagId == seeded.TagId));
        Assert.True(await verify.EmoteTagEntries.AnyAsync(e => e.TagId == otherTagId));
    }

    [Fact]
    public async Task RemovingAnEntry_RemovesItsPlacementsInEverySet_AndLeavesOtherEmotesAndTags()
    {
        var seeded = await SeedChannelWithTagAsync("emotetagentrycascade");
        long otherTagId;
        await using (var db = fixture.CreateDbContext())
        {
            var other = new EmoteTag { ChannelId = seeded.ChannelId, Name = "Other", NormalizedName = "other", CreatedAtUtc = DateTime.UtcNow };
            db.EmoteTags.Add(other);
            await db.SaveChangesAsync();
            otherTagId = other.Id;
            db.EmoteTagEntries.Add(new EmoteTagEntry { TagId = otherTagId, SevenTvEmoteId = seeded.EmoteId, Alias = "KEKW", ImageUrl = "x", AddedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            db.EmoteTagPlacements.AddRange(
                Placement(otherTagId, seeded.EmoteId, SetA),
                Placement(seeded.TagId, seeded.EmoteId, SetB),
                Placement(seeded.TagId, seeded.OtherEmoteId, SetA));
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            await db.EmoteTagEntries.Where(e => e.TagId == seeded.TagId && e.SevenTvEmoteId == seeded.EmoteId).ExecuteDeleteAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var left = await verify.EmoteTagPlacements
            .Where(p => p.TagId == seeded.TagId || p.TagId == otherTagId)
            .Select(p => new { p.TagId, p.SevenTvEmoteId, p.SevenTvEmoteSetId })
            .ToListAsync();
        // (Tag, X, SetA) and (Tag, X, SetB) are gone; the same emote under the other tag and the
        // tag's other emote stay.
        Assert.Equal(2, left.Count);
        Assert.Contains(left, p => p.TagId == otherTagId && p.SevenTvEmoteId == seeded.EmoteId);
        Assert.Contains(left, p => p.TagId == seeded.TagId && p.SevenTvEmoteId == seeded.OtherEmoteId);
    }

    [Fact]
    public async Task DeletingATag_RemovesEntriesAndPlacements_ThroughBothCascadePaths()
    {
        var seeded = await SeedChannelWithTagAsync("emotetagbothpaths");

        await using (var db = fixture.CreateDbContext())
        {
            await db.EmoteTags.Where(t => t.Id == seeded.TagId).ExecuteDeleteAsync();
        }

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTagEntries.AnyAsync(e => e.TagId == seeded.TagId));
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.TagId == seeded.TagId));
        Assert.False(await verify.EmoteTagActivations.AnyAsync(a => a.TagId == seeded.TagId));
        Assert.False(await verify.EmoteTagOperations.AnyAsync(o => o.TagId == seeded.TagId));
        // The observation is the channel's, not the tag's: it survives the tag.
        Assert.True(await verify.EmoteSetLeaveObservations.AnyAsync(o => o.ChannelId == seeded.ChannelId));
    }

    [Fact]
    public async Task InsertingAPlacementWithoutAnEntry_ViolatesTheForeignKey()
    {
        var seeded = await SeedChannelWithTagAsync("emotetagnoentry");

        await using var db = fixture.CreateDbContext();
        db.EmoteTagPlacements.Add(Placement(seeded.TagId, Guid.NewGuid().ToString("N")[..26], SetA));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, pg.SqlState);
    }

    [Fact]
    public async Task TheNewTables_AcceptThirtyTwoCharacterIds_AndRejectThirtyThree()
    {
        var seeded = await SeedChannelWithTagAsync("emotetagwidth");
        var emote32 = new string('E', 32);
        var set32 = new string('S', 32);

        await using (var db = fixture.CreateDbContext())
        {
            var entry = Entry(seeded.TagId);
            entry.SevenTvEmoteId = emote32;
            db.EmoteTagEntries.Add(entry);
            await db.SaveChangesAsync();
            db.EmoteTagPlacements.Add(Placement(seeded.TagId, emote32, set32));
            db.EmoteSetLeaveObservations.Add(new EmoteSetLeaveObservation
            {
                ChannelId = seeded.ChannelId,
                SevenTvEmoteId = emote32,
                SevenTvEmoteSetId = set32,
                LastObservedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            db.EmoteTagPlacements.Add(Placement(seeded.TagId, seeded.EmoteId, new string('S', 33)));
            await AssertTooLongAsync(db);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var entry = Entry(seeded.TagId);
            entry.SevenTvEmoteId = new string('E', 33);
            db.EmoteTagEntries.Add(entry);
            await AssertTooLongAsync(db);
        }
    }

    private async Task AssertTagTablesEmptyAsync(Seeded seeded)
    {
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTags.AnyAsync(t => t.ChannelId == seeded.ChannelId));
        Assert.False(await verify.EmoteTagEntries.AnyAsync(e => e.TagId == seeded.TagId));
        Assert.False(await verify.EmoteTagPlacements.AnyAsync(p => p.TagId == seeded.TagId));
        Assert.False(await verify.EmoteTagActivations.AnyAsync(a => a.TagId == seeded.TagId));
        Assert.False(await verify.EmoteTagOperations.AnyAsync(o => o.TagId == seeded.TagId));
        Assert.False(await verify.EmoteSetLeaveObservations.AnyAsync(o => o.ChannelId == seeded.ChannelId));
    }

    private static async Task AssertTooLongAsync(AppDbContext db)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, pg.SqlState);
    }

    private async Task<Seeded> SeedChannelWithTagAsync(string name, DateTime? deactivatedAtUtc = null)
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = name, IsBotActive = deactivatedAtUtc is null, DeactivatedAtUtc = deactivatedAtUtc };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var tag = new EmoteTag { ChannelId = channel.Id, Name = "Funny", NormalizedName = "funny", CreatedAtUtc = DateTime.UtcNow };
        db.EmoteTags.Add(tag);
        await db.SaveChangesAsync();

        var entry = Entry(tag.Id);
        var otherEntry = Entry(tag.Id);
        db.EmoteTagEntries.AddRange(entry, otherEntry);
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        db.EmoteTagOperations.Add(new EmoteTagOperation
        {
            OperationId = operationId,
            TagId = tag.Id,
            Kind = EmoteTagOperationKind.PlayIn,
            SevenTvEmoteSetId = SetA,
            RegisteredAtUtc = DateTime.UtcNow
        });
        db.EmoteTagActivations.Add(new EmoteTagActivation
        {
            TagId = tag.Id,
            SevenTvEmoteSetId = SetA,
            ActivatedAtUtc = DateTime.UtcNow,
            OperationId = operationId
        });
        db.EmoteSetLeaveObservations.Add(new EmoteSetLeaveObservation
        {
            ChannelId = channel.Id,
            SevenTvEmoteId = entry.SevenTvEmoteId,
            SevenTvEmoteSetId = SetA,
            LastObservedAtUtc = DateTime.UtcNow
        });
        var placement = Placement(tag.Id, entry.SevenTvEmoteId, SetA);
        placement.OperationId = operationId;
        db.EmoteTagPlacements.Add(placement);
        await db.SaveChangesAsync();
        return new Seeded(channel.Id, tag.Id, entry.SevenTvEmoteId, otherEntry.SevenTvEmoteId);
    }

    private static EmoteTagPlacement Placement(long tagId, string emoteId, string setId) => new()
    {
        TagId = tagId,
        SevenTvEmoteId = emoteId,
        SevenTvEmoteSetId = setId,
        PlacedAtUtc = DateTime.UtcNow,
        OperationId = Guid.NewGuid()
    };

    // 26 characters, like a real 7TV ULID.
    private static EmoteTagEntry Entry(long tagId) => new()
    {
        TagId = tagId,
        SevenTvEmoteId = Guid.NewGuid().ToString("N")[..26],
        Alias = "KEKW",
        ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp",
        AddedAtUtc = DateTime.UtcNow
    };

    private static ChannelService CreateService(AppDbContext db) =>
        new(
            db,
            Substitute.For<IRedisPublisher>(),
            Substitute.For<IChannelIdentityService>(),
            new ChannelEmoteSetObservationService(db),
            new ChannelCapacityOptions { MaxActiveChannels = int.MaxValue },
            Substitute.For<IExcludedChannelFilter>(),
            NullLogger<ChannelService>.Instance);

    private sealed record Seeded(string ChannelId, long TagId, string EmoteId, string OtherEmoteId);
}
