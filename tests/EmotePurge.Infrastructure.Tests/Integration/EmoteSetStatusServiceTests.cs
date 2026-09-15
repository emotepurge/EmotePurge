using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Against real Postgres like the rest of the query services: the occupied-slot count is a COUNT
// with a filter over a real index, and the "no capacity reported" case has to survive a round trip
// through a nullable column rather than only through an object in memory.
[Collection("Postgres")]
public class EmoteSetStatusServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetAsync_CountsOnlyActiveEmotesAsOccupiedSlots()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest1", capacity: 1000);
        await SeedEmoteAsync(db, channel.Id, "One");
        await SeedEmoteAsync(db, channel.Id, "Two");
        await SeedEmoteAsync(db, channel.Id, "AlreadyDeleted", isArchived: true);

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Equal(2, status.OccupiedSlots);
        Assert.Equal(1000, status.Capacity);
    }

    [Fact]
    public async Task GetAsync_UnreportedCapacity_StaysNull()
    {
        // The consumer distinguishes "7TV reported no limit" from a number and renders no bar at
        // all — a 0 or an invented 1000 here would both be a lie about how full the set is.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest2", capacity: null);
        await SeedEmoteAsync(db, channel.Id, "One");

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Null(status.Capacity);
        Assert.Equal(1, status.OccupiedSlots);
    }

    [Fact]
    public async Task GetAsync_TrackedSince_PrefersTheRejoinOverTheCreation()
    {
        // A channel that was left and rejoined has a gap in its usage history. CreatedAt would
        // claim we counted through it.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest3", capacity: 1000);
        channel.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        channel.TrackingResumedAt = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Equal(new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc), status.TrackedSince);
    }

    [Fact]
    public async Task GetAsync_NeverRejoined_FallsBackToTheCreation()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest4", capacity: 1000);
        channel.CreatedAt = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Equal(new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc), status.TrackedSince);
    }

    [Fact]
    public async Task GetAsync_BeforeTheFirstSync_ReportsNoOccupiedSlots()
    {
        // The page polls this endpoint in a loop while waiting for the first sync; with no set id
        // there is nothing to count and the query is skipped entirely.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest5", capacity: null, activeEmoteSetId: "");

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Equal(string.Empty, status.ActiveEmoteSetId);
        Assert.Equal(0, status.OccupiedSlots);
    }

    [Fact]
    public async Task GetAsync_ReportsThePersistedSyncFailureReason()
    {
        // The whole point of issue #32: "empty set id" alone cannot tell a channel whose first sync
        // is still running apart from one that has no active emote set on 7TV at all. The reason
        // column is what separates them, so it has to survive the round trip through Postgres.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest7", capacity: null, activeEmoteSetId: "");
        channel.LastSyncFailureReason = SevenTvSyncFailureReasons.NoActiveEmoteSet;
        channel.LastSyncAttemptAtUtc = new DateTime(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Equal("no_active_emote_set", status.SyncFailureReason);
        Assert.Equal(new DateTime(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc), status.LastSyncAttemptAtUtc);
    }

    [Fact]
    public async Task GetAsync_NeverAttempted_ReportsNeitherReasonNorAttempt()
    {
        // The fourth state from the analysis: a freshly joined channel. Both fields null is what
        // lets the usage page keep polling instead of claiming a cause it does not have.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest8", capacity: null, activeEmoteSetId: "");

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Null(status.SyncFailureReason);
        Assert.Null(status.LastSyncAttemptAtUtc);
    }

    [Fact]
    public async Task GetAsync_NormalizesTheChannelName()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest6", capacity: 600);
        await SeedEmoteAsync(db, channel.Id, "One");

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync("  SlotsTest6 ");

        Assert.NotNull(status);
        Assert.Equal(1, status.OccupiedSlots);
    }

    [Fact]
    public async Task GetAsync_UntrackedChannel_ReturnsNull()
    {
        await using var db = fixture.CreateDbContext();

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync("slotstest_missing");

        Assert.Null(status);
    }

    [Fact]
    public async Task GetAsync_DuplicateNames_ReturnsCollidingNames_WithAllInvolvedEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest16", capacity: 1000);
        await SeedEmoteAsync(db, channel.Id, "Dup", sevenTvEmoteId: "7tv-b");
        await SeedEmoteAsync(db, channel.Id, "Dup", sevenTvEmoteId: "7tv-a");
        await SeedEmoteAsync(db, channel.Id, "Solo", sevenTvEmoteId: "7tv-c");

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        var group = Assert.Single(status.DuplicateNames);
        Assert.Equal("Dup", group.Name);
        // Ordered by SevenTvEmoteId, not by insertion order.
        Assert.Equal(["7tv-a", "7tv-b"], group.Emotes.Select(e => e.SevenTvEmoteId));
        Assert.All(group.Emotes, e => Assert.False(string.IsNullOrEmpty(e.EmoteId)));
        Assert.All(group.Emotes, e => Assert.False(string.IsNullOrEmpty(e.ImageUrl)));
    }

    [Fact]
    public async Task GetAsync_DuplicateNames_IgnoresArchivedEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest17", capacity: 1000);
        await SeedEmoteAsync(db, channel.Id, "Dup");
        await SeedEmoteAsync(db, channel.Id, "Dup", isArchived: true);

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Empty(status.DuplicateNames);
    }

    [Fact]
    public async Task GetAsync_DuplicateNames_TreatsCasingVariantsAsDistinctNames()
    {
        // Verified against real Postgres, not assumed: text equality (the operator GROUP BY relies
        // on) is a byte comparison regardless of collation, so this was already ordinal
        // case-sensitive before the in-memory grouping ever runs — but that is exactly the kind of
        // thing EF Core's SQL translation could get wrong silently.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest18", capacity: 1000);
        await SeedEmoteAsync(db, channel.Id, "Emote");
        await SeedEmoteAsync(db, channel.Id, "emote");

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Empty(status.DuplicateNames);
    }

    [Fact]
    public async Task GetAsync_DuplicateNames_NoCollisions_ReturnsEmptyList()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest19", capacity: 1000);
        await SeedEmoteAsync(db, channel.Id, "One");
        await SeedEmoteAsync(db, channel.Id, "Two");

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Empty(status.DuplicateNames);
    }

    [Fact]
    public async Task GetAsync_BeforeTheFirstSync_SkipsTheDuplicateNamesQueryToo()
    {
        // Same gate as occupiedSlots and the two usage dates: an empty ActiveEmoteSetId means the
        // collision scan is not even sent. Two colliding rows existing regardless (seeded directly,
        // bypassing the normal sync path that could never target an unsynced channel) prove the
        // skip happened — nothing else could produce an empty list here.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest20", capacity: null, activeEmoteSetId: "");
        await SeedEmoteAsync(db, channel.Id, "Dup");
        await SeedEmoteAsync(db, channel.Id, "Dup");

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Empty(status.DuplicateNames);
    }

    [Fact]
    public async Task GetAsync_BotsExcludedSince_IsTheEarliestBotRow_NotTheEarliestRowOverall()
    {
        // A human-only row from before the bot ever showed up must not win the MIN — the field
        // answers "since when is bot usage separated", not "since when is this emote used".
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest9", capacity: 1000);
        var emoteOne = await SeedEmoteAsync(db, channel.Id, "One");
        var emoteTwo = await SeedEmoteAsync(db, channel.Id, "Two");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emoteOne.Id, Date = new DateOnly(2026, 8, 1), UseCount = 10 },
            new UsageStat { EmoteId = emoteTwo.Id, Date = new DateOnly(2026, 8, 15), UseCount = 3, BotUseCount = 2 },
            new UsageStat { EmoteId = emoteOne.Id, Date = new DateOnly(2026, 8, 20), UseCount = 1, BotUseCount = 1 });
        await db.SaveChangesAsync();

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Equal(new DateOnly(2026, 8, 15), status.BotsExcludedSince);
    }

    [Fact]
    public async Task GetAsync_NoBotRowsAtAll_BotsExcludedSinceIsNull()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest10", capacity: 1000);
        var emote = await SeedEmoteAsync(db, channel.Id, "One");
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 8, 1), UseCount = 10, BotUseCount = 0 });
        await db.SaveChangesAsync();

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Null(status.BotsExcludedSince);
    }

    [Fact]
    public async Task GetAsync_BeforeTheFirstSync_SkipsTheBotsExcludedSinceQueryToo()
    {
        // Same gate as occupiedSlots: an empty ActiveEmoteSetId means the MIN query is not even
        // sent. A bot row existing regardless (seeded directly, bypassing the normal flush path
        // that could never target an unsynced channel) proves the skip happened — nothing else
        // could produce null here.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest11", capacity: null, activeEmoteSetId: "");
        var emote = await SeedEmoteAsync(db, channel.Id, "One");
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 8, 1), UseCount = 0, BotUseCount = 5 });
        await db.SaveChangesAsync();

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Null(status.BotsExcludedSince);
    }

    [Fact]
    public async Task GetAsync_BotRowOnAnArchivedEmote_StillCounts()
    {
        // An emote deleted from 7TV since the bot sighting still tells us when the separation
        // started for this channel — archived emotes are deliberately not excluded here.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest12", capacity: 1000);
        var archived = await SeedEmoteAsync(db, channel.Id, "GoneEmote", isArchived: true);
        db.UsageStats.Add(new UsageStat { EmoteId = archived.Id, Date = new DateOnly(2026, 8, 5), UseCount = 0, BotUseCount = 4 });
        await db.SaveChangesAsync();

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Equal(new DateOnly(2026, 8, 5), status.BotsExcludedSince);
    }

    [Fact]
    public async Task GetAsync_SharedChatSeparatedSince_IsTheEarliestSharedChatRow_NotTheEarliestRowOverall()
    {
        // Same shape as the bot field, and seeded next to a bot row on purpose: the two dates are
        // independent minima over two independent columns, and neither may leak into the other.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest13", capacity: 1000);
        var emoteOne = await SeedEmoteAsync(db, channel.Id, "One");
        var emoteTwo = await SeedEmoteAsync(db, channel.Id, "Two");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emoteOne.Id, Date = new DateOnly(2026, 8, 1), UseCount = 10, BotUseCount = 2 },
            new UsageStat { EmoteId = emoteTwo.Id, Date = new DateOnly(2026, 8, 15), UseCount = 0, SharedChatUseCount = 3 },
            new UsageStat { EmoteId = emoteOne.Id, Date = new DateOnly(2026, 8, 20), UseCount = 1, SharedChatUseCount = 1 });
        await db.SaveChangesAsync();

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Equal(new DateOnly(2026, 8, 15), status.SharedChatSeparatedSince);
        Assert.Equal(new DateOnly(2026, 8, 1), status.BotsExcludedSince);
    }

    [Fact]
    public async Task GetAsync_NoSharedChatRowsAtAll_SharedChatSeparatedSinceIsNull()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest14", capacity: 1000);
        var emote = await SeedEmoteAsync(db, channel.Id, "One");
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 8, 1), UseCount = 10, SharedChatUseCount = 0 });
        await db.SaveChangesAsync();

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Null(status.SharedChatSeparatedSince);
    }

    [Fact]
    public async Task GetAsync_BeforeTheFirstSync_SkipsTheSharedChatSeparatedSinceQueryToo()
    {
        // Same gate as occupiedSlots and botsExcludedSince: an empty ActiveEmoteSetId means the MIN
        // query is not even sent. A shared-chat row existing regardless (seeded directly, bypassing
        // the normal flush path that could never target an unsynced channel) proves the skip
        // happened — nothing else could produce null here.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotstest15", capacity: null, activeEmoteSetId: "");
        var emote = await SeedEmoteAsync(db, channel.Id, "One");
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 8, 1), UseCount = 0, SharedChatUseCount = 5 });
        await db.SaveChangesAsync();

        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db)).GetAsync(channel.ChannelName);

        Assert.NotNull(status);
        Assert.Null(status.SharedChatSeparatedSince);
    }

    private static async Task<Channel> SeedChannelAsync(
        AppDbContext db, string channelName, int? capacity, string activeEmoteSetId = "64c9e0f0aa1234567890abcd")
    {
        var channel = new Channel
        {
            ChannelName = channelName,
            IsBotActive = true,
            ActiveEmoteSetId = activeEmoteSetId,
            ActiveEmoteSetCapacity = capacity
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private static async Task<Emote> SeedEmoteAsync(
        AppDbContext db, string channelId, string name, bool isArchived = false, string? sevenTvEmoteId = null)
    {
        var emote = new Emote
        {
            ChannelId = channelId,
            Name = name,
            SevenTvEmoteId = sevenTvEmoteId ?? Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp",
            IsArchived = isArchived
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }
}
