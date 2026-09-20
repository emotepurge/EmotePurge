using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// FlushAsync writes through hand-written SQL (INSERT ... ON CONFLICT), so a real postgres:16-alpine
// container is the only thing that can verify it at all: the arbiter inference against the unique
// index — which carries an INCLUDE column — has no equivalent in any in-memory provider.
[Collection("Postgres")]
public class UsageStatFlushServiceTests(PostgresFixture fixture)
{
    // Every batch below uses a single, arbitrary emote set id — the composite (EmoteId, EmoteSetId)
    // key is exercised here only as a signature change (T1.1); the behaviour of two different sets
    // sharing a day is T1.4's addition once the flush writes the set id instead of summing across
    // it (see UsageStatFlushService.FlushAsync's provisional grouping).
    private const string SetId = "set-1";

    [Fact]
    public async Task FlushAsync_InsertsNewRow_ForFirstCountOfTheDay()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flushtest1");

        await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts> { [Key(emote)] = new(3, 0, 0) });

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(3, stat.UseCount);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), stat.Date);
    }

    [Fact]
    public async Task FlushAsync_InsertsNewRow_WithAllThreeValues()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flushtest-allthree");

        await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts> { [Key(emote)] = new(3, 1, 2) });

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(3, stat.UseCount);
        Assert.Equal(1, stat.BotUseCount);
        Assert.Equal(2, stat.SharedChatUseCount);
    }

    [Fact]
    public async Task FlushAsync_AddsToExistingRow_InsteadOfCreatingASecondOne()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flushtest2");

        // Two flushes in the same UTC day must accumulate on one row — this is the ON CONFLICT
        // DO UPDATE path, and the assertion that the arbiter actually matched the unique index
        // rather than raising a duplicate-key error.
        await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts> { [Key(emote)] = new(4, 0, 0) });
        await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts> { [Key(emote)] = new(6, 0, 0) });

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(10, stat.UseCount);
    }

    [Fact]
    public async Task FlushAsync_AddsToExistingRow_AccumulatesBothColumnsSeparately()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flushtest2b");

        // Same conflict path as above, but with both columns populated — the DO UPDATE must add
        // "UseCount" and "BotUseCount" independently, not cross-add or drop one of them.
        await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts> { [Key(emote)] = new(4, 1, 0) });
        await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts> { [Key(emote)] = new(6, 3, 0) });

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(10, stat.UseCount);
        Assert.Equal(4, stat.BotUseCount);
    }

    [Fact]
    public async Task FlushAsync_AddsToExistingRow_AccumulatesAllThreeColumnsSeparately()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flushtest2c");

        // Same conflict path as above, now with all three columns populated — the DO UPDATE must
        // add "UseCount", "BotUseCount" and "SharedChatUseCount" independently, not cross-add or
        // drop any of them.
        await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts> { [Key(emote)] = new(4, 1, 2) });
        await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts> { [Key(emote)] = new(6, 3, 5) });

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(10, stat.UseCount);
        Assert.Equal(4, stat.BotUseCount);
        Assert.Equal(7, stat.SharedChatUseCount);
    }

    [Fact]
    public async Task FlushAsync_SkipsOrphanedEmoteIds_AndStillWritesTheRest()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flushtest3");

        // A count buffered for an emote that no longer exists would violate the FK; it must not
        // take the rest of the batch down with it.
        await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts>
        {
            [Key(emote)] = new(2, 0, 0),
            [new UsageCounterKey(Guid.NewGuid().ToString(), SetId)] = new(99, 0, 0),
        });

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(2, stat.UseCount);
    }

    [Fact]
    public async Task FlushAsync_WritesEveryEmote_OfAMultiEmoteBatch()
    {
        await using var db = fixture.CreateDbContext();
        var first = await SeedEmoteAsync(db, "flushtest4");
        var second = await SeedEmoteAsync(db, "flushtest5");

        // Guards the UNNEST array pairing: a mismatch between the id array and the count array
        // would silently attribute counts to the wrong emote.
        await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts>
        {
            [Key(first)] = new(11, 0, 0),
            [Key(second)] = new(22, 0, 0),
        });

        Assert.Equal(11, Assert.Single(await ReadStatsAsync(fixture, first.Id)).UseCount);
        Assert.Equal(22, Assert.Single(await ReadStatsAsync(fixture, second.Id)).UseCount);
    }

    [Fact]
    public async Task FlushAsync_PairsAllThreeArraysCorrectly_AcrossMultipleEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var first = await SeedEmoteAsync(db, "flushtest4b");
        var second = await SeedEmoteAsync(db, "flushtest5b");

        // Now with a fourth UNNEST array (shared-chat counts): a swap between any two of the four
        // arrays would silently attribute counts to the wrong emote or the wrong column. All three
        // values are chosen distinct per emote — and pairwise distinct between the two emotes — so
        // any transposition shows up as a wrong assertion.
        await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts>
        {
            [Key(first)] = new(11, 5, 2),
            [Key(second)] = new(3, 22, 9),
        });

        var firstStat = Assert.Single(await ReadStatsAsync(fixture, first.Id));
        Assert.Equal(11, firstStat.UseCount);
        Assert.Equal(5, firstStat.BotUseCount);
        Assert.Equal(2, firstStat.SharedChatUseCount);

        var secondStat = Assert.Single(await ReadStatsAsync(fixture, second.Id));
        Assert.Equal(3, secondStat.UseCount);
        Assert.Equal(22, secondStat.BotUseCount);
        Assert.Equal(9, secondStat.SharedChatUseCount);
    }

    [Fact]
    public async Task FlushAsync_WritesBotOnlyRow_WithZeroHumanCount()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flushtest-botonly");

        // E1: bot usage is preserved, not dropped, even when an emote had no human usage at all in
        // the batch — no "only write rows with Human > 0" filter exists.
        await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts> { [Key(emote)] = new(0, 7, 0) });

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(0, stat.UseCount);
        Assert.Equal(7, stat.BotUseCount);
    }

    [Fact]
    public async Task FlushAsync_WritesSharedChatOnlyRow_WithZeroHumanAndBotCount()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flushtest-sharedonly");

        // B6 negative probe (#73 design): a batch where an emote came only from a foreign room
        // during a Shared Chat session still gets a row — no "only write rows with Human/Bot > 0"
        // filter exists.
        await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts> { [Key(emote)] = new(0, 0, 6) });

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(0, stat.UseCount);
        Assert.Equal(0, stat.BotUseCount);
        Assert.Equal(6, stat.SharedChatUseCount);
    }

    [Fact]
    public async Task FlushAsync_DoesNothing_ForEmptyBatch()
    {
        await using var db = fixture.CreateDbContext();

        Assert.Empty(await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts>()));
    }

    [Fact]
    public async Task FlushAsync_ReturnsEachAffectedChannelOnce()
    {
        await using var db = fixture.CreateDbContext();
        var (first, second) = await SeedTwoEmotesInOneChannelAsync(db, "flushchannels1");
        var elsewhere = await SeedEmoteAsync(db, "flushchannels2");

        // The caller announces one live event per channel — two emotes of the same channel must not
        // produce two announcements.
        var affected = await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts>
        {
            [Key(first)] = new(1, 0, 0),
            [Key(second)] = new(2, 0, 0),
            [Key(elsewhere)] = new(3, 0, 0),
        });

        Assert.Equal(["flushchannels1", "flushchannels2"], affected.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task FlushAsync_ExcludesChannelsOfSkippedEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flushchannels3");

        // An id with no row has no channel to announce, and must not smuggle a null into the result.
        var affected = await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts>
        {
            [Key(emote)] = new(5, 0, 0),
            [new UsageCounterKey(Guid.NewGuid().ToString(), SetId)] = new(9, 0, 0),
        });

        Assert.Equal("flushchannels3", Assert.Single(affected));
    }

    [Fact]
    public async Task FlushAsync_ReturnsEmpty_WhenEveryEmoteIsGone()
    {
        await using var db = fixture.CreateDbContext();

        var affected = await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts>
        {
            [new UsageCounterKey(Guid.NewGuid().ToString(), SetId)] = new(4, 0, 0),
        });

        Assert.Empty(affected);
    }

    private static UsageCounterKey Key(Emote emote) => new(emote.Id, SetId);

    private static UsageStatFlushService CreateService(AppDbContext db) =>
        new(db, NullLogger<UsageStatFlushService>.Instance);

    // Reads through a fresh context: the rows were written by raw SQL, so anything the original
    // context has tracked is irrelevant to what actually landed in the table.
    private static async Task<List<UsageStat>> ReadStatsAsync(PostgresFixture fixture, string emoteId)
    {
        await using var db = fixture.CreateDbContext();
        return db.UsageStats.Where(u => u.EmoteId == emoteId).ToList();
    }

    private static async Task<Emote> SeedEmoteAsync(AppDbContext db, string channelName)
    {
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Name = "PogChamp",
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp",
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    private static async Task<(Emote First, Emote Second)> SeedTwoEmotesInOneChannelAsync(AppDbContext db, string channelName)
    {
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        var first = NewEmote(channel.Id, "PogChamp");
        var second = NewEmote(channel.Id, "Kappa");
        db.Emotes.AddRange(first, second);
        await db.SaveChangesAsync();
        return (first, second);
    }

    private static Emote NewEmote(string channelId, string name) => new()
    {
        ChannelId = channelId,
        Name = name,
        SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
        ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp",
    };
}
