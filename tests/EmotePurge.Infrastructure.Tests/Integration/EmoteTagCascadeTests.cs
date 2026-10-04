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

// The emote-tag tables hang off the channel with FK cascades (#201): both channel purges must take
// them down, and deleting a tag must take its entries. Against real Postgres, because the cascade is
// the database's, not EF's.
[Collection("Postgres")]
public class EmoteTagCascadeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task PurgeAsync_RemovesTheChannelsTagsAndEntries()
    {
        var seeded = await SeedChannelWithTagAsync("emotetagpurge");

        await using var db = fixture.CreateDbContext();
        var purged = await CreateService(db).PurgeAsync("emotetagpurge", new AuditActor("tag-mod", "tagmod"));

        Assert.True(purged);
        await AssertTagTablesEmptyAsync(seeded);
    }

    [Fact]
    public async Task PurgeIfInactiveSince_RemovesTheChannelsTagsAndEntries()
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

    private async Task AssertTagTablesEmptyAsync(Seeded seeded)
    {
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.EmoteTags.AnyAsync(t => t.ChannelId == seeded.ChannelId));
        Assert.False(await verify.EmoteTagEntries.AnyAsync(e => e.TagId == seeded.TagId));
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

        db.EmoteTagEntries.Add(Entry(tag.Id));
        await db.SaveChangesAsync();
        return new Seeded(channel.Id, tag.Id);
    }

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

    private sealed record Seeded(string ChannelId, long TagId);
}
