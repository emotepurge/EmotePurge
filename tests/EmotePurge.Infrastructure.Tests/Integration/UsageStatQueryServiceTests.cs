using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Runs against a real postgres:16-alpine container, not EF Core InMemory. The conditional-sum
// GroupBy in GetUsageContextAsync only translates cleanly because the query is pre-scoped to a
// plain emote-ID list (see the comment in UsageStatQueryService.cs) — InMemory would happily
// evaluate the naive, untranslatable version client-side and never catch a regression back to it.
//
// SharedOnlyRow_ReadsAsUnused_LikeBotOnly is the inverted marker of the D5 transition (#73). The
// bridge has fallen: every query here sums and filters over UseCount alone, so a shared-only row
// reads as unused, exactly like a bot-only row. It is the same seed as the test that used to
// assert the opposite, kept rather than deleted — a deleted test would prove nothing about which
// side of the cutover this code is on.
[Collection("Postgres")]
public class UsageStatQueryServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetUsageContextAsync_SumsUseCountAcrossDays_WithinRange()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "totalstest1");
        var emote = await SeedEmoteAsync(db, channel.Id, "PogChamp");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 1), UseCount = 5 },
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 2), UseCount = 7 },
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 10), UseCount = 100 }); // outside range
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 3));

        var result = Assert.Single(totals);
        Assert.Equal(12, result.TotalUseCount);
    }

    [Fact]
    public async Task GetUsageContextAsync_ZeroFills_ActiveEmotesWithoutUsageStatRows()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "totalstest2");
        await SeedEmoteAsync(db, channel.Id, "NeverUsed");

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var result = Assert.Single(totals);
        Assert.Equal("NeverUsed", result.EmoteName);
        Assert.Equal(0, result.TotalUseCount);
        Assert.Null(result.LastUsedDate);
        Assert.Equal(0, result.PreviousWindowUseCount);
    }

    [Fact]
    public async Task GetUsageContextAsync_Excludes_ArchivedEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "totalstest3");
        var archived = await SeedEmoteAsync(db, channel.Id, "GoneEmote", isArchived: true);
        db.UsageStats.Add(new UsageStat { EmoteId = archived.Id, Date = new DateOnly(2026, 7, 1), UseCount = 42 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        Assert.Empty(totals);
    }

    [Fact]
    public async Task GetUsageContextAsync_ReturnsEmpty_ForChannelWithNoEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "totalstest4");

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        Assert.Empty(totals);
    }

    [Fact]
    public async Task GetUsageContextAsync_LastUsedDate_IsNotBoundedByTheRange()
    {
        // The whole point of the field: "0 uses in the last 7 days" must still be able to say the
        // emote was heavily used a month ago. Clipping the maximum to the range would turn this
        // into a restatement of the total and report almost every emote as never used.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "contexttest_lastused");
        var emote = await SeedEmoteAsync(db, channel.Id, "Faded");
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 5, 4), UseCount = 900 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        var result = Assert.Single(totals);
        Assert.Equal(0, result.TotalUseCount);
        Assert.Equal(new DateOnly(2026, 5, 4), result.LastUsedDate);
    }

    [Fact]
    public async Task GetUsageContextAsync_PreviousWindow_CoversTheEquallyLongPrecedingRange()
    {
        // Range 07-08..07-14 is 7 days inclusive, so the preceding window is 07-01..07-07:
        // previousFrom inclusive, from exclusive. Both boundary days are asserted, because an
        // off-by-one here silently shifts every trend label by a day.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "contexttest_window");
        var emote = await SeedEmoteAsync(db, channel.Id, "Trending");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 6, 30), UseCount = 1000 }, // before the previous window
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 1), UseCount = 3 },     // first previous-window day
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 7), UseCount = 4 },     // last previous-window day
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 8), UseCount = 20 },    // first range day
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 14), UseCount = 5 });   // last range day
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 14));

        var result = Assert.Single(totals);
        Assert.Equal(25, result.TotalUseCount);
        Assert.Equal(7, result.PreviousWindowUseCount);
        Assert.Equal(new DateOnly(2026, 7, 14), result.LastUsedDate);
    }

    [Fact]
    public async Task GetUsageContextAsync_LastUsedDate_IgnoresBotOnlyRows()
    {
        // A row can carry UseCount = 0 while BotUseCount > 0 (a day an emote was posted only by
        // bots) — such a row must not read as "used" for LastUsedDate, or a bot-only day would
        // outrank a genuinely more recent human day.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "contexttest_botonly1");
        var emote = await SeedEmoteAsync(db, channel.Id, "BotSpam");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 1), UseCount = 5 },
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 15), UseCount = 0, BotUseCount = 3 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        var result = Assert.Single(totals);
        Assert.Equal(new DateOnly(2026, 7, 1), result.LastUsedDate);
        Assert.Equal(5, result.TotalUseCount);
    }

    [Fact]
    public async Task GetUsageContextAsync_LastUsedDate_IsNull_WhenOnlyBotRowsExist()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "contexttest_botonly2");
        var emote = await SeedEmoteAsync(db, channel.Id, "OnlyBot");
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 10), UseCount = 0, BotUseCount = 7 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var result = Assert.Single(totals);
        Assert.Null(result.LastUsedDate);
        Assert.Equal(0, result.TotalUseCount);
    }

    [Fact]
    public async Task SharedOnlyRow_ReadsAsUnused_LikeBotOnly()
    {
        // The inverted D5 marker (#73, Zug 2): the read path sums and filters over UseCount alone
        // again, so a shared-only row reads as unused everywhere — exactly the way a bot-only row
        // always did. Same seed as the test that asserted the opposite during the transition, so
        // the diff between the two is the whole behaviour change. If this ever goes green with the
        // old numbers again, the bridge is back.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "sharedchat_transition");
        var from = new DateOnly(2026, 7, 10);
        var to = new DateOnly(2026, 7, 20);

        // Older human day outside the requested range, younger shared-only day inside it — proves
        // LastUsedDate (unbounded in time) falls back to the older HUMAN day rather than naming the
        // younger foreign one, and that the range-bounded sums see nothing at all here.
        var sharedThenHuman = await SeedEmoteAsync(db, channel.Id, "SharedThenHuman");
        var olderHumanDay = new DateOnly(2026, 7, 1);
        var youngerSharedDay = new DateOnly(2026, 7, 15);
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = sharedThenHuman.Id, Date = olderHumanDay, UseCount = 5 },
            new UsageStat { EmoteId = sharedThenHuman.Id, Date = youngerSharedDay, UseCount = 0, BotUseCount = 0, SharedChatUseCount = 3 });

        // A mixed row: only the own half of the day counts.
        var mixed = await SeedEmoteAsync(db, channel.Id, "MixedHumanAndShared");
        var mixedDay = new DateOnly(2026, 7, 12);
        db.UsageStats.Add(new UsageStat { EmoteId = mixed.Id, Date = mixedDay, UseCount = 2, SharedChatUseCount = 3 });

        // An emote with exclusively shared-only rows (no own usage, ever) counts as never used and
        // drops out of the channel series entirely, the same way a bot-only emote does.
        var sharedOnly = await SeedEmoteAsync(db, channel.Id, "ExclusivelySharedChat");
        db.UsageStats.Add(new UsageStat { EmoteId = sharedOnly.Id, Date = new DateOnly(2026, 7, 14), UseCount = 0, SharedChatUseCount = 4 });

        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);

        var context = await service.GetUsageContextAsync(channel.ChannelName, from, to);
        var sharedThenHumanContext = context.Single(c => c.EmoteId == sharedThenHuman.Id);
        Assert.Equal(0, sharedThenHumanContext.TotalUseCount);
        Assert.Equal(olderHumanDay, sharedThenHumanContext.LastUsedDate);
        Assert.Equal(2, context.Single(c => c.EmoteId == mixed.Id).TotalUseCount);
        var sharedOnlyContext = context.Single(c => c.EmoteId == sharedOnly.Id);
        Assert.Equal(0, sharedOnlyContext.TotalUseCount);
        Assert.Null(sharedOnlyContext.LastUsedDate);

        var dailySeries = await service.GetDailySeriesAsync(channel.ChannelName, sharedThenHuman.Id, from, to);
        Assert.NotNull(dailySeries);
        Assert.Empty(dailySeries.Days);
        Assert.Equal(0, dailySeries.TotalUseCount);
        Assert.Equal(olderHumanDay, dailySeries.FirstUsedDate);
        Assert.Equal(olderHumanDay, dailySeries.LastUsedDate);

        var channelSeries = await service.GetChannelSeriesAsync(channel.ChannelName, from, to);
        Assert.DoesNotContain(channelSeries.Emotes, e => e.EmoteId == sharedThenHuman.Id);
        Assert.DoesNotContain(channelSeries.Emotes, e => e.EmoteId == sharedOnly.Id);
        var mixedEntry = channelSeries.Emotes.Single(e => e.EmoteId == mixed.Id);
        Assert.Equal([[mixedDay.DayNumber - from.DayNumber, 2]], mixedEntry.Days);

        // No UseCount > 0 filter here, only the date range — so the shared-only day still forms a
        // group, it just sums to nothing.
        var totals = await service.GetTotalsByEmoteIdsAsync([sharedThenHuman.Id, mixed.Id], from, to);
        Assert.Equal(0, totals[sharedThenHuman.Id]);
        Assert.Equal(2, totals[mixed.Id]);
    }

    [Fact]
    public async Task GetUsageContextAsync_CarriesFirstSeenAt()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "contexttest_firstseen");
        var firstSeen = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        await SeedEmoteAsync(db, channel.Id, "Fresh", firstSeenAt: firstSeen);
        await SeedEmoteAsync(db, channel.Id, "Unknown");

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        Assert.Equal(firstSeen, totals.Single(t => t.EmoteName == "Fresh").FirstSeenAt);
        // Null stays null — a row that predates the column must read as "unknown", never as "new".
        Assert.Null(totals.Single(t => t.EmoteName == "Unknown").FirstSeenAt);
    }

    [Fact]
    public async Task GetUsageContextAsync_TranslatesServerSide_ForAFullSizedEmoteSet()
    {
        // 1200 emotes is above HandOfBlood's ~900. A regression to client evaluation would either
        // throw here or drag the whole set through memory; both must fail this test rather than
        // only show up in production.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "contexttest_large");
        for (var i = 0; i < 1200; i++)
        {
            db.Emotes.Add(new Emote
            {
                ChannelId = channel.Id,
                Name = $"Bulk{i}",
                SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
                ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
            });
        }

        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetUsageContextAsync(channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        Assert.Equal(1200, totals.Count);
    }

    [Fact]
    public async Task GetUsageContextAsync_Throws_WhenFromIsAfterTo()
    {
        // The guard runs before any DB access, so no seed is needed here.
        await using var db = fixture.CreateDbContext();

        var service = new UsageStatQueryService(db);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetUsageContextAsync("irrelevant-channel", new DateOnly(2026, 7, 7), new DateOnly(2026, 7, 1)));

        Assert.Equal("from", exception.ParamName);
    }

    [Fact]
    public async Task GetTotalsByEmoteIdsAsync_ReturnsOnlyTheRequestedIds()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballottest1");
        var onBallot = await SeedEmoteAsync(db, channel.Id, "OnBallot");
        var offBallot = await SeedEmoteAsync(db, channel.Id, "OffBallot");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = onBallot.Id, Date = new DateOnly(2026, 7, 2), UseCount = 9 },
            new UsageStat { EmoteId = offBallot.Id, Date = new DateOnly(2026, 7, 2), UseCount = 99 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetTotalsByEmoteIdsAsync([onBallot.Id], new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        Assert.Equal(9, Assert.Single(totals).Value);
    }

    [Fact]
    public async Task GetTotalsByEmoteIdsAsync_OmitsEmotesWithoutUsage()
    {
        // No zero-fill here, unlike the context query: the caller already holds the emote rows and
        // reads a missing key as zero, so filling them in would only make the payload bigger.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "ballottest2");
        var unused = await SeedEmoteAsync(db, channel.Id, "Unused");

        var service = new UsageStatQueryService(db);
        var totals = await service.GetTotalsByEmoteIdsAsync([unused.Id], new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        Assert.Empty(totals);
    }

    [Fact]
    public async Task GetTotalsByEmoteIdsAsync_ForNoIds_ReturnsEmptyWithoutQuerying()
    {
        await using var db = fixture.CreateDbContext();

        var service = new UsageStatQueryService(db);
        var totals = await service.GetTotalsByEmoteIdsAsync([], new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        Assert.Empty(totals);
    }

    [Fact]
    public async Task GetTotalsByEmoteIdsAsync_Throws_WhenFromIsAfterTo()
    {
        // The guard runs before any DB access, so no seed is needed here.
        await using var db = fixture.CreateDbContext();

        var service = new UsageStatQueryService(db);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetTotalsByEmoteIdsAsync(["irrelevant-id"], new DateOnly(2026, 7, 7), new DateOnly(2026, 7, 1)));

        Assert.Equal("from", exception.ParamName);
    }

    [Fact]
    public async Task GetDailySeriesAsync_ReturnsOnlyDaysWithUsage_Ascending_InclusiveBounds()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dailytest1");
        var emote = await SeedEmoteAsync(db, channel.Id, "Daily");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 6, 30), UseCount = 50 }, // before range
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 1), UseCount = 3 },   // first range day
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 5), UseCount = 8 },
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 7), UseCount = 2 },   // last range day
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 8), UseCount = 60 }); // after range
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var series = await service.GetDailySeriesAsync(
            channel.ChannelName, emote.Id, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.NotNull(series);
        // Sparse (no zero rows for 07-02..07-04, 07-06) and strictly ascending; both boundary days
        // included, both neighbours excluded.
        Assert.Equal(
            [new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 7)],
            series.Days.Select(d => d.Date).ToArray());
        Assert.Equal(13, series.TotalUseCount);
    }

    [Fact]
    public async Task GetDailySeriesAsync_FirstAndLastUsedDate_AreNotBoundedByTheRange()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dailytest2");
        var emote = await SeedEmoteAsync(db, channel.Id, "OldTimer");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 5, 1), UseCount = 1 },
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 8, 1), UseCount = 1 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var series = await service.GetDailySeriesAsync(
            channel.ChannelName, emote.Id, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.NotNull(series);
        Assert.Empty(series.Days);
        Assert.Equal(0, series.TotalUseCount);
        Assert.Equal(new DateOnly(2026, 5, 1), series.FirstUsedDate);
        Assert.Equal(new DateOnly(2026, 8, 1), series.LastUsedDate);
    }

    [Fact]
    public async Task GetDailySeriesAsync_ExcludesBotOnlyDays_FromDaysAndBounds()
    {
        // Same UseCount = 0 / BotUseCount > 0 row shape as the context query — must not show up
        // in the sparse Days list nor shift First/LastUsedDate.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dailytest_botonly1");
        var emote = await SeedEmoteAsync(db, channel.Id, "BotDay");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 1), UseCount = 3 },
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 5), UseCount = 0, BotUseCount = 9 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var series = await service.GetDailySeriesAsync(
            channel.ChannelName, emote.Id, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.NotNull(series);
        Assert.Equal([new DateOnly(2026, 7, 1)], series.Days.Select(d => d.Date).ToArray());
        Assert.Equal(new DateOnly(2026, 7, 1), series.FirstUsedDate);
        Assert.Equal(new DateOnly(2026, 7, 1), series.LastUsedDate);
    }

    [Fact]
    public async Task GetDailySeriesAsync_FirstAndLastUsedDate_AreNull_WhenOnlyBotRowsExist()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dailytest_botonly2");
        var emote = await SeedEmoteAsync(db, channel.Id, "OnlyBotDaily");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 2), UseCount = 0, BotUseCount = 4 },
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 6), UseCount = 0, BotUseCount = 1 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var series = await service.GetDailySeriesAsync(
            channel.ChannelName, emote.Id, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.NotNull(series);
        Assert.Empty(series.Days);
        Assert.Equal(0, series.TotalUseCount);
        Assert.Null(series.FirstUsedDate);
        Assert.Null(series.LastUsedDate);
    }

    [Fact]
    public async Task GetDailySeriesAsync_ReturnsNull_ForAnEmoteOfAnotherChannel()
    {
        // The security-relevant case: emoteId is a client-supplied value, and without the channel
        // join a caller with access to channel A could read channel B's series.
        await using var db = fixture.CreateDbContext();
        var channelA = await SeedChannelAsync(db, "dailytest3a");
        var channelB = await SeedChannelAsync(db, "dailytest3b");
        var foreignEmote = await SeedEmoteAsync(db, channelB.Id, "Foreign");

        var service = new UsageStatQueryService(db);
        var series = await service.GetDailySeriesAsync(
            channelA.ChannelName, foreignEmote.Id, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.Null(series);
    }

    [Fact]
    public async Task GetDailySeriesAsync_ReturnsNull_ForAnUnknownEmoteId()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dailytest4");

        var service = new UsageStatQueryService(db);
        var series = await service.GetDailySeriesAsync(
            channel.ChannelName, "no-such-id", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.Null(series);
    }

    [Fact]
    public async Task GetDailySeriesAsync_ForAnEmoteWithoutAnyUsage_ReturnsEmptySeries()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dailytest5");
        var emote = await SeedEmoteAsync(db, channel.Id, "NeverUsed");

        var service = new UsageStatQueryService(db);
        var series = await service.GetDailySeriesAsync(
            channel.ChannelName, emote.Id, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.NotNull(series);
        Assert.Empty(series.Days);
        Assert.Equal(0, series.TotalUseCount);
        Assert.Null(series.FirstUsedDate);
        Assert.Null(series.LastUsedDate);
    }

    [Fact]
    public async Task GetDailySeriesAsync_KeepsTheHistoryOfAnArchivedEmote()
    {
        // Unlike GetUsageContextAsync: an archived emote is unreachable from the usage grid, but a
        // subset vote session still lists it as a ballot member, and its history is real.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dailytest6");
        var archived = await SeedEmoteAsync(db, channel.Id, "GoneButReal", isArchived: true);
        db.UsageStats.Add(new UsageStat { EmoteId = archived.Id, Date = new DateOnly(2026, 7, 2), UseCount = 4 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var series = await service.GetDailySeriesAsync(
            channel.ChannelName, archived.Id, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.NotNull(series);
        Assert.Equal(4, Assert.Single(series.Days).UseCount);
    }

    [Fact]
    public async Task GetDailySeriesAsync_ReturnsLiveDaysWithinTheRange_Ascending()
    {
        // Unlike FirstUsedDate/LastUsedDate, the live days are range-bounded: the consumer lays
        // them under exactly the rendered window (A10). Both boundary days in, both neighbours out.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dailytest_live1");
        var emote = await SeedEmoteAsync(db, channel.Id, "LiveAware");
        db.ChannelLiveDays.AddRange(
            new ChannelLiveDay { ChannelId = channel.Id, Date = new DateOnly(2026, 6, 30), LiveMinutes = 120 }, // before range
            new ChannelLiveDay { ChannelId = channel.Id, Date = new DateOnly(2026, 7, 1), LiveMinutes = 5 },    // first range day
            new ChannelLiveDay { ChannelId = channel.Id, Date = new DateOnly(2026, 7, 7), LiveMinutes = 300 },  // last range day
            new ChannelLiveDay { ChannelId = channel.Id, Date = new DateOnly(2026, 7, 8), LiveMinutes = 60 });  // after range
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var series = await service.GetDailySeriesAsync(
            channel.ChannelName, emote.Id, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.NotNull(series);
        Assert.Equal([new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7)], series.LiveDays);
    }

    [Fact]
    public async Task GetDailySeriesAsync_DoesNotLeakAnotherChannelsLiveDays()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dailytest_live2");
        var otherChannel = await SeedChannelAsync(db, "dailytest_live2_other");
        var emote = await SeedEmoteAsync(db, channel.Id, "HomeAlone");
        db.ChannelLiveDays.Add(new ChannelLiveDay { ChannelId = otherChannel.Id, Date = new DateOnly(2026, 7, 3), LiveMinutes = 60 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var series = await service.GetDailySeriesAsync(
            channel.ChannelName, emote.Id, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.NotNull(series);
        Assert.Empty(series.LiveDays);
    }

    [Fact]
    public async Task GetDailySeriesAsync_Throws_WhenFromIsAfterTo()
    {
        // The guard runs before any DB access, so no seed is needed here.
        await using var db = fixture.CreateDbContext();

        var service = new UsageStatQueryService(db);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetDailySeriesAsync("irrelevant-channel", "irrelevant-id", new DateOnly(2026, 7, 7), new DateOnly(2026, 7, 1)));

        Assert.Equal("from", exception.ParamName);
    }

    [Fact]
    public async Task GetChannelSeriesAsync_ReturnsOffsetsFromRangeStart_PerEmote_Ascending()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "seriestest1");
        var first = await SeedEmoteAsync(db, channel.Id, "First");
        var second = await SeedEmoteAsync(db, channel.Id, "Second");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = first.Id, Date = new DateOnly(2026, 6, 30), UseCount = 99 }, // before range
            new UsageStat { EmoteId = first.Id, Date = new DateOnly(2026, 7, 1), UseCount = 3 },   // offset 0
            new UsageStat { EmoteId = first.Id, Date = new DateOnly(2026, 7, 5), UseCount = 8 },   // offset 4
            new UsageStat { EmoteId = first.Id, Date = new DateOnly(2026, 7, 8), UseCount = 99 },  // after range
            new UsageStat { EmoteId = second.Id, Date = new DateOnly(2026, 7, 7), UseCount = 2 }); // offset 6
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var series = await service.GetChannelSeriesAsync(
            channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        // Both boundary days in, both neighbours out — and the offset is counted from `from`, which
        // is the whole reason the client can zero-fill without parsing a date.
        var firstEntry = Assert.Single(series.Emotes, e => e.EmoteId == first.Id);
        Assert.Equal([[0, 3], [4, 8]], firstEntry.Days);
        var secondEntry = Assert.Single(series.Emotes, e => e.EmoteId == second.Id);
        Assert.Equal([[6, 2]], secondEntry.Days);
        Assert.Equal(new DateOnly(2026, 7, 1), series.From);
        Assert.Equal(new DateOnly(2026, 7, 7), series.To);
    }

    [Fact]
    public async Task GetChannelSeriesAsync_OmitsEmotesWithoutUsageInTheRange()
    {
        // The batch's one deliberate asymmetry against GetUsageContextAsync, which zero-fills: on a
        // 900-emote set the never-used band is most of the response, and "absent" already means
        // "no usage" one level down, inside an entry's sparse days.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "seriestest2");
        var used = await SeedEmoteAsync(db, channel.Id, "Used");
        await SeedEmoteAsync(db, channel.Id, "NeverUsed");
        var outOfRange = await SeedEmoteAsync(db, channel.Id, "UsedElsewhen");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = used.Id, Date = new DateOnly(2026, 7, 3), UseCount = 1 },
            new UsageStat { EmoteId = outOfRange.Id, Date = new DateOnly(2026, 8, 3), UseCount = 500 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var series = await service.GetChannelSeriesAsync(
            channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.Equal(used.Id, Assert.Single(series.Emotes).EmoteId);
    }

    [Fact]
    public async Task GetChannelSeriesAsync_OmitsEmoteWithOnlyBotUsage_InRange()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "seriestest_botonly1");
        var human = await SeedEmoteAsync(db, channel.Id, "HumanOnly");
        var botOnly = await SeedEmoteAsync(db, channel.Id, "BotOnly");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = human.Id, Date = new DateOnly(2026, 7, 3), UseCount = 2 },
            new UsageStat { EmoteId = botOnly.Id, Date = new DateOnly(2026, 7, 4), UseCount = 0, BotUseCount = 5 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var series = await service.GetChannelSeriesAsync(
            channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.Equal(human.Id, Assert.Single(series.Emotes).EmoteId);
    }

    [Fact]
    public async Task GetChannelSeriesAsync_ForMixedEmote_OnlyIncludesHumanDays()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "seriestest_botonly2");
        var emote = await SeedEmoteAsync(db, channel.Id, "Mixed");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 2), UseCount = 4 },                    // offset 1
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 5), UseCount = 0, BotUseCount = 8 });  // offset 4, bot-only
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var series = await service.GetChannelSeriesAsync(
            channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        var entry = Assert.Single(series.Emotes);
        Assert.Equal([[1, 4]], entry.Days);
    }

    [Fact]
    public async Task GetChannelSeriesAsync_ExcludesArchivedEmotes_AndOtherChannels()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "seriestest3");
        var other = await SeedChannelAsync(db, "seriestest3_other");
        var archived = await SeedEmoteAsync(db, channel.Id, "Gone", isArchived: true);
        var foreign = await SeedEmoteAsync(db, other.Id, "Foreign");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = archived.Id, Date = new DateOnly(2026, 7, 2), UseCount = 4 },
            new UsageStat { EmoteId = foreign.Id, Date = new DateOnly(2026, 7, 2), UseCount = 7 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var series = await service.GetChannelSeriesAsync(
            channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.Empty(series.Emotes);
    }

    [Fact]
    public async Task GetChannelSeriesAsync_ReturnsLiveDaysAsOffsets_BoundedByTheRange()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "seriestest_live");
        var other = await SeedChannelAsync(db, "seriestest_live_other");
        db.ChannelLiveDays.AddRange(
            new ChannelLiveDay { ChannelId = channel.Id, Date = new DateOnly(2026, 6, 30), LiveMinutes = 120 }, // before
            new ChannelLiveDay { ChannelId = channel.Id, Date = new DateOnly(2026, 7, 1), LiveMinutes = 5 },    // offset 0
            new ChannelLiveDay { ChannelId = channel.Id, Date = new DateOnly(2026, 7, 7), LiveMinutes = 300 },  // offset 6
            new ChannelLiveDay { ChannelId = channel.Id, Date = new DateOnly(2026, 7, 8), LiveMinutes = 60 },   // after
            new ChannelLiveDay { ChannelId = other.Id, Date = new DateOnly(2026, 7, 3), LiveMinutes = 60 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var series = await service.GetChannelSeriesAsync(
            channel.ChannelName, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.Equal([0, 6], series.LiveDays);
    }

    [Fact]
    public async Task GetChannelSeriesAsync_ForAnUnknownChannel_ReturnsEmptyListsRatherThanThrowing()
    {
        await using var db = fixture.CreateDbContext();

        var service = new UsageStatQueryService(db);
        var series = await service.GetChannelSeriesAsync(
            "no-such-channel", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.Empty(series.Emotes);
        Assert.Empty(series.LiveDays);
    }

    [Fact]
    public async Task GetChannelSeriesAsync_Throws_WhenFromIsAfterTo()
    {
        // The guard runs before any DB access, so no seed is needed here.
        await using var db = fixture.CreateDbContext();

        var service = new UsageStatQueryService(db);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetChannelSeriesAsync("irrelevant-channel", new DateOnly(2026, 7, 7), new DateOnly(2026, 7, 1)));

        Assert.Equal("from", exception.ParamName);
    }

    [Fact]
    public async Task GetEarliestBotUsageDateAsync_IsTheEarliestBotDay_NotTheEarliestRowOverall()
    {
        // A human-only row from before the bot ever showed up must not win — the answer is "since
        // when is bot usage separated", not "since when is this emote used at all".
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "bottest1");
        var emoteOne = await SeedEmoteAsync(db, channel.Id, "One");
        var emoteTwo = await SeedEmoteAsync(db, channel.Id, "Two");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emoteOne.Id, Date = new DateOnly(2026, 8, 1), UseCount = 10 },
            new UsageStat { EmoteId = emoteTwo.Id, Date = new DateOnly(2026, 8, 15), UseCount = 3, BotUseCount = 2 },
            new UsageStat { EmoteId = emoteOne.Id, Date = new DateOnly(2026, 8, 20), UseCount = 1, BotUseCount = 1 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var earliestBotDate = await service.GetEarliestBotUsageDateAsync(channel.Id);

        Assert.Equal(new DateOnly(2026, 8, 15), earliestBotDate);
    }

    [Fact]
    public async Task GetEarliestBotUsageDateAsync_NoBotRowsAtAll_ReturnsNull()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "bottest2");
        var emote = await SeedEmoteAsync(db, channel.Id, "One");
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 8, 1), UseCount = 10, BotUseCount = 0 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var earliestBotDate = await service.GetEarliestBotUsageDateAsync(channel.Id);

        Assert.Null(earliestBotDate);
    }

    [Fact]
    public async Task GetEarliestBotUsageDateAsync_BotRowOnAnArchivedEmote_StillCounts()
    {
        // An emote deleted from 7TV since the bot sighting still tells us when the separation
        // started for this channel — archived emotes are deliberately not excluded here.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "bottest3");
        var archived = await SeedEmoteAsync(db, channel.Id, "GoneEmote", isArchived: true);
        db.UsageStats.Add(new UsageStat { EmoteId = archived.Id, Date = new DateOnly(2026, 8, 5), UseCount = 0, BotUseCount = 4 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var earliestBotDate = await service.GetEarliestBotUsageDateAsync(channel.Id);

        Assert.Equal(new DateOnly(2026, 8, 5), earliestBotDate);
    }

    [Fact]
    public async Task GetEarliestBotUsageDateAsync_AnotherChannelsBotRow_DoesNotCount()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "bottest4");
        var otherChannel = await SeedChannelAsync(db, "bottest4_other");
        var otherEmote = await SeedEmoteAsync(db, otherChannel.Id, "Foreign");
        db.UsageStats.Add(new UsageStat { EmoteId = otherEmote.Id, Date = new DateOnly(2026, 8, 1), UseCount = 0, BotUseCount = 9 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var earliestBotDate = await service.GetEarliestBotUsageDateAsync(channel.Id);

        Assert.Null(earliestBotDate);
    }

    [Fact]
    public async Task GetEarliestSharedChatUsageDateAsync_IsTheEarliestSharedChatDay_NotTheEarliestRowOverall()
    {
        // A human-only row from before the first mirrored message must not win — the answer is
        // "since when is shared-chat usage separated", not "since when is this emote used at all".
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "sharedtest1");
        var emoteOne = await SeedEmoteAsync(db, channel.Id, "One");
        var emoteTwo = await SeedEmoteAsync(db, channel.Id, "Two");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emoteOne.Id, Date = new DateOnly(2026, 8, 1), UseCount = 10 },
            // The normal case this query has to find: no own usage at all on that day, only mirrored.
            new UsageStat { EmoteId = emoteTwo.Id, Date = new DateOnly(2026, 8, 15), UseCount = 0, BotUseCount = 0, SharedChatUseCount = 2 },
            new UsageStat { EmoteId = emoteOne.Id, Date = new DateOnly(2026, 8, 20), UseCount = 1, SharedChatUseCount = 1 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var earliestSharedChatDate = await service.GetEarliestSharedChatUsageDateAsync(channel.Id);

        Assert.Equal(new DateOnly(2026, 8, 15), earliestSharedChatDate);
    }

    [Fact]
    public async Task GetEarliestSharedChatUsageDateAsync_NoSharedChatRowsAtAll_ReturnsNull()
    {
        // Includes a bot row on purpose: the two columns are separate, and a bot sighting must not
        // be mistaken for a shared-chat one.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "sharedtest2");
        var emote = await SeedEmoteAsync(db, channel.Id, "One");
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 8, 1), UseCount = 10, BotUseCount = 4, SharedChatUseCount = 0 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var earliestSharedChatDate = await service.GetEarliestSharedChatUsageDateAsync(channel.Id);

        Assert.Null(earliestSharedChatDate);
    }

    [Fact]
    public async Task GetEarliestSharedChatUsageDateAsync_SharedChatRowOnAnArchivedEmote_StillCounts()
    {
        // An emote deleted from 7TV since the sighting still tells us when the separation started
        // for this channel — archived emotes are deliberately not excluded here.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "sharedtest3");
        var archived = await SeedEmoteAsync(db, channel.Id, "GoneEmote", isArchived: true);
        db.UsageStats.Add(new UsageStat { EmoteId = archived.Id, Date = new DateOnly(2026, 8, 5), UseCount = 0, SharedChatUseCount = 4 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var earliestSharedChatDate = await service.GetEarliestSharedChatUsageDateAsync(channel.Id);

        Assert.Equal(new DateOnly(2026, 8, 5), earliestSharedChatDate);
    }

    [Fact]
    public async Task GetEarliestSharedChatUsageDateAsync_AnotherChannelsSharedChatRow_DoesNotCount()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "sharedtest4");
        var otherChannel = await SeedChannelAsync(db, "sharedtest4_other");
        var otherEmote = await SeedEmoteAsync(db, otherChannel.Id, "Foreign");
        db.UsageStats.Add(new UsageStat { EmoteId = otherEmote.Id, Date = new DateOnly(2026, 8, 1), UseCount = 0, SharedChatUseCount = 9 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var earliestSharedChatDate = await service.GetEarliestSharedChatUsageDateAsync(channel.Id);

        Assert.Null(earliestSharedChatDate);
    }

    [Fact]
    public async Task GetEmoteLifetimesAsync_IncludesActiveAndArchivedEmotes_WithFieldsPassedThrough()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "lifetimetest1");
        var firstSeen = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var archivedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastSynced = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        var active = await SeedEmoteAsync(db, channel.Id, "Active", firstSeenAt: firstSeen, lastSyncedAt: lastSynced);
        var archived = await SeedEmoteAsync(
            db, channel.Id, "Archived", isArchived: true, archivedAt: archivedAt, lastSyncedAt: lastSynced);

        var service = new UsageStatQueryService(db);
        var lifetimes = await service.GetEmoteLifetimesAsync(channel.Id);

        Assert.Equal(2, lifetimes.Count);
        var activeDto = lifetimes.Single(l => l.Id == active.Id);
        Assert.Equal("Active", activeDto.Name);
        Assert.False(activeDto.IsArchived);
        Assert.Equal(firstSeen, activeDto.FirstSeenAt);
        Assert.Null(activeDto.ArchivedAt);
        Assert.Equal(lastSynced, activeDto.LastSyncedAt);

        var archivedDto = lifetimes.Single(l => l.Id == archived.Id);
        Assert.Equal("Archived", archivedDto.Name);
        Assert.True(archivedDto.IsArchived);
        Assert.Equal(archivedAt, archivedDto.ArchivedAt);
        Assert.Null(archivedDto.FirstSeenAt);
        Assert.Equal(lastSynced, archivedDto.LastSyncedAt);
    }

    [Fact]
    public async Task GetEmoteLifetimesAsync_FirstSeenAtNull_StaysNull()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "lifetimetest2");
        await SeedEmoteAsync(db, channel.Id, "Unknown");

        var service = new UsageStatQueryService(db);
        var lifetimes = await service.GetEmoteLifetimesAsync(channel.Id);

        Assert.Null(Assert.Single(lifetimes).FirstSeenAt);
    }

    [Fact]
    public async Task GetEmoteLifetimesAsync_ExcludesOtherChannels()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "lifetimetest3");
        var otherChannel = await SeedChannelAsync(db, "lifetimetest3_other");
        await SeedEmoteAsync(db, otherChannel.Id, "Foreign");

        var service = new UsageStatQueryService(db);
        var lifetimes = await service.GetEmoteLifetimesAsync(channel.Id);

        Assert.Empty(lifetimes);
    }

    [Fact]
    public async Task GetEmoteLifetimesAsync_OrdersById_Ordinal()
    {
        // The chat-log backfill harness (issue #69) hashes this list to detect a changed input set
        // on resume — the order has to be deterministic and independent of insertion order.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "lifetimetest4");
        var one = await SeedEmoteAsync(db, channel.Id, "One");
        var two = await SeedEmoteAsync(db, channel.Id, "Two");
        var three = await SeedEmoteAsync(db, channel.Id, "Three");

        var service = new UsageStatQueryService(db);
        var lifetimes = await service.GetEmoteLifetimesAsync(channel.Id);

        var expectedOrder = new[] { one.Id, two.Id, three.Id }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedOrder, lifetimes.Select(l => l.Id).ToArray());
    }

    [Fact]
    public async Task GetRowsAsync_ReturnsRowsInInclusiveRange_ExcludingOutsideDaysAndOtherEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "rowstest1");
        var emote = await SeedEmoteAsync(db, channel.Id, "InScope");
        var notRequested = await SeedEmoteAsync(db, channel.Id, "NotRequested");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 6, 30), UseCount = 1 },     // before range
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 1), UseCount = 3 },      // first range day
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 7), UseCount = 5 },      // last range day
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 8), UseCount = 9 },      // after range
            new UsageStat { EmoteId = notRequested.Id, Date = new DateOnly(2026, 7, 2), UseCount = 100 }); // not on the id list
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var rows = await service.GetRowsAsync([emote.Id], new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.Equal(
            [new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7)],
            rows.Select(r => r.Date).ToArray());
        Assert.All(rows, r => Assert.Equal(emote.Id, r.EmoteId));
    }

    [Fact]
    public async Task GetRowsAsync_IncludesBotOnlyRows()
    {
        // Unlike every other query in this service, GetRowsAsync keeps rows with UseCount = 0 and
        // BotUseCount > 0 — the harness needs the channel's whole recorded activity for its
        // bot-inclusive total, not the human-only view the usage grid shows.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "rowstest2");
        var emote = await SeedEmoteAsync(db, channel.Id, "BotOnly");
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 3), UseCount = 0, BotUseCount = 3 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var rows = await service.GetRowsAsync([emote.Id], new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        var row = Assert.Single(rows);
        Assert.Equal(0, row.UseCount);
        Assert.Equal(3, row.BotUseCount);
    }

    [Fact]
    public async Task GetRowsAsync_IncludesSharedChatOnlyRows_WithTheColumnPassedThroughRaw()
    {
        // GetRowsAsync is the raw pass-through the #73 design's B4 requires: unlike the product UI
        // queries, which drop a row without own usage entirely, the harness needs
        // SharedChatUseCount itself, unfiltered.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "rowstest-sharedonly");
        var emote = await SeedEmoteAsync(db, channel.Id, "SharedOnly");
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 3), UseCount = 0, BotUseCount = 0, SharedChatUseCount = 4 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var rows = await service.GetRowsAsync([emote.Id], new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        var row = Assert.Single(rows);
        Assert.Equal(0, row.UseCount);
        Assert.Equal(0, row.BotUseCount);
        Assert.Equal(4, row.SharedChatUseCount);
    }

    [Fact]
    public async Task GetRowsAsync_ForEmptyIdList_ReturnsEmptyWithoutQuerying()
    {
        await using var db = fixture.CreateDbContext();

        var service = new UsageStatQueryService(db);
        var rows = await service.GetRowsAsync([], new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetRowsAsync_ForASingleDayWindow_ReturnsOnlyThatDay()
    {
        // from == to is the narrowest legal window (not the from > to guard) — the boundary day
        // must still come back, and its neighbours must not.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "rowstest4");
        var emote = await SeedEmoteAsync(db, channel.Id, "OneDay");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 2), UseCount = 1 },  // day before
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 3), UseCount = 4 },  // the window
            new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 7, 4), UseCount = 1 }); // day after
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var rows = await service.GetRowsAsync([emote.Id], new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 3));

        var row = Assert.Single(rows);
        Assert.Equal(new DateOnly(2026, 7, 3), row.Date);
        Assert.Equal(4, row.UseCount);
    }

    [Fact]
    public async Task GetRowsAsync_OrdersByEmoteIdThenDate()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "rowstest3");
        var emoteA = await SeedEmoteAsync(db, channel.Id, "A");
        var emoteB = await SeedEmoteAsync(db, channel.Id, "B");
        var (first, second) = string.CompareOrdinal(emoteA.Id, emoteB.Id) <= 0 ? (emoteA, emoteB) : (emoteB, emoteA);
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = first.Id, Date = new DateOnly(2026, 7, 3), UseCount = 1 },
            new UsageStat { EmoteId = first.Id, Date = new DateOnly(2026, 7, 1), UseCount = 1 },
            new UsageStat { EmoteId = second.Id, Date = new DateOnly(2026, 7, 2), UseCount = 1 });
        await db.SaveChangesAsync();

        var service = new UsageStatQueryService(db);
        var rows = await service.GetRowsAsync([first.Id, second.Id], new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7));

        Assert.Equal(
            [(first.Id, new DateOnly(2026, 7, 1)), (first.Id, new DateOnly(2026, 7, 3)), (second.Id, new DateOnly(2026, 7, 2))],
            rows.Select(r => (r.EmoteId, r.Date)).ToArray());
    }

    [Fact]
    public async Task GetRowsAsync_FromAfterTo_Throws()
    {
        await using var db = fixture.CreateDbContext();

        var service = new UsageStatQueryService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetRowsAsync(["irrelevant-id"], new DateOnly(2026, 7, 7), new DateOnly(2026, 7, 1)));
    }

    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string channelName)
    {
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private static async Task<Emote> SeedEmoteAsync(
        AppDbContext db,
        string channelId,
        string name,
        bool isArchived = false,
        DateTime? firstSeenAt = null,
        DateTime? archivedAt = null,
        DateTime? lastSyncedAt = null)
    {
        var emote = new Emote
        {
            ChannelId = channelId,
            Name = name,
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp",
            IsArchived = isArchived,
            FirstSeenAt = firstSeenAt,
            ArchivedAt = archivedAt
        };
        if (lastSyncedAt is not null)
        {
            emote.LastSyncedAt = lastSyncedAt.Value;
        }

        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }
}
