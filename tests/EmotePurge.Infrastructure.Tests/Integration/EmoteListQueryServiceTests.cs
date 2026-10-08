using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class EmoteListQueryServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ListActiveAsync_ReturnsOnlyActiveEmotes_WithIdAndName()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "emotelist1",
            ("7tv-a", "Alpha", false),
            ("7tv-b", "Bravo", false),
            ("7tv-c", "Charlie", false),
            ("7tv-d", "Deleted", true));

        var emotes = await new EmoteListQueryService(db).ListActiveAsync("emotelist1");

        Assert.NotNull(emotes);
        Assert.Equal(3, emotes.Count);
        Assert.DoesNotContain(emotes, e => e.SevenTvEmoteId == "7tv-d");
        Assert.All(emotes, e =>
        {
            Assert.False(string.IsNullOrEmpty(e.SevenTvEmoteId));
            Assert.False(string.IsNullOrEmpty(e.Name));
        });
    }

    [Fact]
    public async Task ListActiveAsync_ReturnsTheEmoteImageUrl()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "emotelist7",
            ("7tv-a", "Alpha", false));

        var emotes = await new EmoteListQueryService(db).ListActiveAsync("emotelist7");

        Assert.NotNull(emotes);
        var emote = Assert.Single(emotes);
        // Straight copy of the tracked Emote's own ImageUrl (T1) — never derived from
        // SevenTvEmoteId, so this pins the actual value SeedChannelAsync wrote, not just
        // "non-empty".
        Assert.Equal("https://cdn.7tv.app/emote/7tv-a/2x.webp", emote.ImageUrl);
    }

    [Fact]
    public async Task ListActiveAsync_SortsOrdinally_NotByLocaleAwareCollation()
    {
        await using var db = fixture.CreateDbContext();
        // "Zebra" (uppercase Z, code point 0x5A) sorts before "apple" (lowercase a, code point
        // 0x61) under ordinal comparison, but after it under a locale-aware collation, which
        // treats case as a secondary sort key and compares "z" against "a" first. This is exactly
        // the pair that would expose a query still relying on Postgres's default collation instead
        // of an in-memory StringComparer.Ordinal sort (see Plan-71 R4 for the trap).
        await SeedChannelAsync(db, "emotelist2",
            ("7tv-a", "apple", false),
            ("7tv-b", "Zebra", false));

        var emotes = await new EmoteListQueryService(db).ListActiveAsync("emotelist2");

        Assert.NotNull(emotes);
        Assert.Equal(["Zebra", "apple"], emotes.Select(e => e.Name));
    }

    [Fact]
    public async Task ListActiveAsync_UntrackedChannel_ReturnsNull()
    {
        await using var db = fixture.CreateDbContext();

        var emotes = await new EmoteListQueryService(db).ListActiveAsync("emotelist3");

        Assert.Null(emotes);
    }

    [Fact]
    public async Task ListActiveAsync_NormalizesTheChannelName()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "emotelist4",
            ("7tv-a", "Alpha", false));

        var emotes = await new EmoteListQueryService(db).ListActiveAsync("  EmoteList4 ");

        Assert.NotNull(emotes);
        Assert.Single(emotes);
    }

    [Fact]
    public async Task ListActiveAsync_ChannelWithOnlyArchivedEmotes_ReturnsEmptyList_NotNull()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "emotelist5",
            ("7tv-a", "Alpha", true));

        var emotes = await new EmoteListQueryService(db).ListActiveAsync("emotelist5");

        Assert.NotNull(emotes);
        Assert.Empty(emotes);
    }

    [Fact]
    public async Task ListActiveAsync_AllowsDuplicateNames()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "emotelist6",
            ("7tv-a", "Dup", false),
            ("7tv-b", "Dup", false));

        var emotes = await new EmoteListQueryService(db).ListActiveAsync("emotelist6");

        Assert.NotNull(emotes);
        Assert.Equal(2, emotes.Count);
        Assert.All(emotes, e => Assert.Equal("Dup", e.Name));
    }

    private static async Task<Channel> SeedChannelAsync(
        Persistence.AppDbContext db,
        string name,
        params (string SevenTvId, string Name, bool Archived)[] emotes)
    {
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}" };
        db.Channels.Add(channel);
        foreach (var (sevenTvId, emoteName, archived) in emotes)
        {
            db.Emotes.Add(new Emote
            {
                ChannelId = channel.Id,
                SevenTvEmoteId = sevenTvId,
                Name = emoteName,
                ImageUrl = $"https://cdn.7tv.app/emote/{sevenTvId}/2x.webp",
                IsArchived = archived
            });
        }

        await db.SaveChangesAsync();
        return channel;
    }
}
