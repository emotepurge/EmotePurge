using System.Text.Json;

using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// The Postgres collection shares one database across test classes, so every assertion here filters
/// down to this class's own channel-name prefix instead of reading the whole table — a second test
/// class writing entries must not be able to shift this one's pages.
/// </summary>
[Collection("Postgres")]
public class AuditLogQueryServiceTests(PostgresFixture fixture)
{
    private const string ChannelPrefix = "auditquery";

    [Fact]
    public async Task ListAsync_ReturnsNewestFirst()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-order";
        await SeedAsync(db, channel,
            (AuditActions.ChannelJoin, new DateTime(2099, 7, 29, 10, 0, 0, DateTimeKind.Utc)),
            (AuditActions.ChannelLeave, new DateTime(2099, 7, 31, 10, 0, 0, DateTimeKind.Utc)),
            (AuditActions.ChannelPurge, new DateTime(2099, 7, 30, 10, 0, 0, DateTimeKind.Utc)));

        var page = await new AuditLogQueryService(db).ListAsync(1, 50);

        var actions = page.Items.Where(i => i.ChannelName == channel).Select(i => i.Action).ToList();
        Assert.Equal([AuditActions.ChannelLeave, AuditActions.ChannelPurge, AuditActions.ChannelJoin], actions);
    }

    [Fact]
    public async Task ListAsync_BreaksTimestampTiesById_SoNoRowAppearsOnTwoPages()
    {
        // Entries written inside one transaction share a timestamp to the tick. Without the Id
        // tiebreaker, Postgres may order them differently per query and Skip/Take would then return
        // the same row twice while dropping another entirely.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-ties";
        var sameInstant = new DateTime(2099, 7, 31, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(db, channel,
            (AuditActions.ChannelJoin, sameInstant),
            (AuditActions.ChannelLeave, sameInstant),
            (AuditActions.ChannelPurge, sameInstant));

        var service = new AuditLogQueryService(db);
        var all = await service.ListAsync(1, 50);
        var expected = all.Items.Where(i => i.ChannelName == channel).Select(i => i.Id).ToList();

        // Newest (highest Id) first, because the timestamps cannot decide it.
        Assert.Equal(expected.OrderByDescending(id => id), expected);
        Assert.Equal(3, expected.Count);
    }

    [Fact]
    public async Task ListAsync_PagesWithoutOverlap_AndReportsTotals()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-paging";
        var baseTime = new DateTime(2099, 7, 31, 8, 0, 0, DateTimeKind.Utc);
        await SeedAsync(db, channel, Enumerable.Range(0, 5)
            .Select(i => (AuditActions.ChannelJoin, baseTime.AddMinutes(i)))
            .ToArray());

        var service = new AuditLogQueryService(db);
        var first = await service.ListAsync(1, 2);
        var second = await service.ListAsync(2, 2);

        Assert.Equal(2, first.Items.Count);
        Assert.Equal(1, first.Page);
        Assert.Equal(2, first.PageSize);
        // TotalCount is the whole table, not this channel's slice — the admin log is unfiltered today.
        Assert.True(first.TotalCount >= 5);
        Assert.Equal((int)Math.Ceiling(first.TotalCount / 2.0), first.TotalPages);
        Assert.Empty(first.Items.Select(i => i.Id).Intersect(second.Items.Select(i => i.Id)));
    }

    [Fact]
    public async Task ListAsync_ProjectsEveryField_AndWhitelistsTheDetails()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-fields";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 9, 0, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncDeleted,
            ChannelName = channel,
            TargetType = "voteSession",
            TargetId = "42",
            DetailsJson = """{"emoteCount": 12}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db).ListAsync(1, 100);

        var dto = Assert.Single(page.Items, i => i.ChannelName == channel);
        Assert.Equal("sensitron", dto.ActorLogin);
        Assert.Equal(AuditActions.EmotesSyncDeleted, dto.Action);
        Assert.Equal("voteSession", dto.TargetType);
        Assert.Equal("42", dto.TargetId);
        // Never the raw column: the client receives a closed shape it cannot be surprised by.
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.EmoteCount, 12, null), dto.Detail);
    }

    [Theory]
    // The three recognized shapes, one row each.
    [InlineData("""{"emoteCount": 12}""", AuditLogDetail.Kinds.EmoteCount, 12L, null)]
    [InlineData("""{"removedEntries": 3}""", AuditLogDetail.Kinds.RemovedEntries, 3L, null)]
    [InlineData("""{"title": "Sommer-Purge"}""", AuditLogDetail.Kinds.Title, null, "Sommer-Purge")]
    // Fixed precedence when a payload carries more than one known key.
    [InlineData("""{"title": "x", "emoteCount": 7}""", AuditLogDetail.Kinds.EmoteCount, 7L, null)]
    // Everything unrecognized degrades to "no detail" instead of leaking or throwing. `login` is
    // the one that matters: it is present on the user-scoped actions and must never render.
    [InlineData("""{"login": "handofblood"}""", null, null, null)]
    [InlineData("""{"ip": "203.0.113.7"}""", null, null, null)]
    [InlineData("""{"emoteCount": "twelve"}""", null, null, null)]
    [InlineData("""[1, 2]""", null, null, null)]
    [InlineData("""17""", null, null, null)]
    [InlineData(null, null, null, null)]
    // No case for syntactically invalid JSON: the column is jsonb, so Postgres rejects it on insert
    // and it cannot reach the reader through this path. ProjectDetail still catches JsonException —
    // the guard costs nothing and is what keeps the column type from being load-bearing.
    public async Task ListAsync_ProjectsDetails_DefensivelyAndByWhitelist(
        string? detailsJson, string? expectedKind, long? expectedCount, string? expectedText)
    {
        await using var db = fixture.CreateDbContext();
        // Truncated to the column's 25 characters — one channel per theory case, so the cases stay
        // independent inside the shared database.
        var channel = $"{ChannelPrefix}-d{Guid.NewGuid():N}"[..25];
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 10, 0, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.VoteSessionDelete,
            ChannelName = channel,
            DetailsJson = detailsJson
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        if (expectedKind is null)
        {
            Assert.Null(dto.Detail);
            return;
        }

        Assert.Equal(new AuditLogDetail(expectedKind, expectedCount, expectedText), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_ProjectsAnImportFromAChannel_OnBothCountAndSource()
    {
        // The precedence pin (R1): the payload also carries emoteCount, which would win and drop the
        // provenance if the sourceKind check did not run first.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-import-channel";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 17, 0, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 5, "sourceChannelName": "otherchannel", "sourceKind": "channel"}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.ImportedFromChannel, 5, "otherchannel"), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_ProjectsAnImportFromAFile_WithNoSource()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-import-file";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 17, 30, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 3, "sourceChannelName": null, "sourceKind": "file"}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.ImportedFromFile, 3, null), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_ProjectsAnImportFromAForeignChannel_OnBothCountAndSource()
    {
        // The third sourceKind (foreign-import spec E6/F5.3). Without this word in the renderer's
        // vocabulary the row would fall through to the bare emoteCount branch and lose the one thing
        // it cannot be reconstructed from — where the emotes came from. Audit rows are write-once,
        // so that loss would be permanent and completely silent.
        // It renders as ImportedFromChannel, like a tracked-channel import: what the reader needs is
        // that it came from a channel and which one, not which of our two read paths saw it.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-imp-fgn";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 18, 15, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 7, "sourceChannelName": "handofblood", "sourceKind": "seventv-channel"}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.ImportedFromChannel, 7, "handofblood"), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_ProjectsAnImportFromTheLeaderboard_OnBothCountAndSortCode()
    {
        // The fourth sourceKind (leaderboard-import spec E2/E8/E9): a network-wide 7TV ranking has
        // no source channel, so Text carries the language-neutral sort wire code instead of a
        // channel name — the frontend, not this service, turns it into "7TV Trend heute"/"7TV Top
        // insgesamt" (rule 7).
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-imp-ldb";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 18, 30, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 9, "sourceChannelName": null, "sourceKind": "seventv-leaderboard", "leaderboardSort": "TRENDING_DAILY"}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.ImportedFromLeaderboard, 9, "TRENDING_DAILY"), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_FallsBackToTheBareCount_WhenALeaderboardSourceCarriesAnUnknownSortCode()
    {
        // The rollback case (F1 Station 5, task 6a brief): a row written with "seventv-leaderboard"
        // must never make the reader throw or invent an origin, whether the code is simply
        // unrecognized or this feature was ever reverted after such rows existed. Degrading to the
        // bare count is the same choice ProjectDetail already makes for a channel import that
        // cannot name its channel.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-imp-ldb-unk";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 18, 35, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 9, "sourceChannelName": null, "sourceKind": "seventv-leaderboard", "leaderboardSort": "TRENDING_WEEKLY"}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.EmoteCount, 9, null), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_FallsBackToTheBareCount_WhenAForeignChannelSourceCarriesNoName()
    {
        // Same degradation as the tracked-channel case below: the endpoint rejects this combination,
        // so a row like this only exists if something got in around it — and then "7 emotes" is
        // honest while "from " with nothing after it is not.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-fgn-nonm";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 18, 20, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 7, "sourceChannelName": null, "sourceKind": "seventv-channel"}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.EmoteCount, 7, null), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_FallsBackToTheBareCount_WhenAChannelSourceCarriesNoName()
    {
        // The endpoint rejects this combination, so it should never reach the column. If it ever
        // does, the reader degrades to the bare count rather than inventing an origin: the channel
        // kind would render "3 emotes from " with nothing after it, and the file kind would claim
        // an origin this row never had. Belt and braces, on a column ten call sites write.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-imp-noname";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 17, 45, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 3, "sourceChannelName": null, "sourceKind": "channel"}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.EmoteCount, 3, null), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_KeepsAFileImportAFileImport_WhenAStraySourceChannelIsPresent()
    {
        // The finding a second opinion caught: picking the kind by "is a name present" filed a file
        // import under a channel origin it never had. sourceKind decides, the stray name is dropped.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-imp-stray";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 18, 0, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 3, "sourceChannelName": "otherchannel", "sourceKind": "file"}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.ImportedFromFile, 3, null), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_LeavesABareEmoteCountProjectionUnchanged_WhenThereIsNoSourceKind()
    {
        // The gegenprobe for the precedence change: an unrelated action that also carries a bare
        // emoteCount (e.g. emotes.syncRestored) must not be caught by the new sourceKind check.
        await using var db = fixture.CreateDbContext();
        // "gegenprobe" was one character over the ChannelName column's 25-char limit; kept short.
        var channel = $"{ChannelPrefix}-import-bare";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 18, 0, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncRestored,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 9}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.EmoteCount, 9, null), dto.Detail);
    }

    [Fact]
    public async Task ListAsync_TruncatesDetailText_BecauseTitlesAreUserInput()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-detail-long";
        var title = new string('a', 500);
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 11, 0, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.VoteSessionDelete,
            ChannelName = channel,
            DetailsJson = JsonSerializer.Serialize(new { title })
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        Assert.Equal(200, Assert.Single(page.Items).Detail?.Text?.Length);
    }

    [Fact]
    public async Task ListAsync_FiltersByAction_AndCountsOnlyTheFilteredSet()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-actionfilter";
        var baseTime = new DateTime(2099, 7, 31, 13, 0, 0, DateTimeKind.Utc);
        await SeedAsync(db, channel,
            (AuditActions.ChannelJoin, baseTime),
            (AuditActions.ChannelLeave, baseTime.AddMinutes(1)),
            (AuditActions.ChannelJoin, baseTime.AddMinutes(2)));

        // Channel narrows to this test's rows; action is the filter under test.
        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(AuditActions.ChannelJoin, channel, null));

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(1, page.TotalPages);
        Assert.All(page.Items, i => Assert.Equal(AuditActions.ChannelJoin, i.Action));
        Assert.All(page.Items, i => Assert.Equal(channel, i.ChannelName));
    }

    [Fact]
    public async Task ListAsync_NormalizesTheChannelFilter_BeforeMatching()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-normalize";
        await SeedAsync(db, channel,
            (AuditActions.ChannelJoin, new DateTime(2099, 7, 31, 14, 0, 0, DateTimeKind.Utc)));

        // Raw admin input: Twitch names get typed with capitals and stray whitespace (Regel 9).
        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, $"  {channel.ToUpperInvariant()}  ", null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(channel, dto.ChannelName);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ListAsync_FiltersActorBySubstring_CaseInsensitively()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-actorfilter";
        var baseTime = new DateTime(2099, 7, 31, 15, 0, 0, DateTimeKind.Utc);
        db.AuditLogEntries.AddRange(
            NewEntry(channel, AuditActions.ChannelJoin, baseTime, actorLogin: "handofblood"),
            NewEntry(channel, AuditActions.ChannelJoin, baseTime.AddMinutes(1), actorLogin: "sensitron"));
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, "OFBLO"));

        var dto = Assert.Single(page.Items);
        Assert.Equal("handofblood", dto.ActorLogin);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ListAsync_TreatsBlankFilterValues_AsNoFilter()
    {
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-blank";
        await SeedAsync(db, channel,
            (AuditActions.ChannelJoin, new DateTime(2099, 7, 31, 16, 0, 0, DateTimeKind.Utc)));

        // The endpoint already nulls blank query params; the service still must not turn a stray
        // whitespace value into a filter that matches nothing.
        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 100, new AuditLogFilter("   ", channel, "   "));

        var dto = Assert.Single(page.Items);
        Assert.Equal(AuditActions.ChannelJoin, dto.Action);
    }

    [Fact]
    public async Task ListAsync_ProjectsTheTargetEmoteSet_WhenTheDetailsNameOne()
    {
        // AK 32/spec 6.7: annotates whichever import Kind the row already had (ImportedFromChannel
        // here) with the target set, rather than picking a Kind of its own.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-imp-target";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 19, 0, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = channel,
            TargetType = "emoteSet",
            TargetId = "set-target-a",
            DetailsJson = """
                {"emoteCount": 5, "sourceChannelName": "otherchannel", "sourceKind": "channel",
                 "targetEmoteSetId": "set-target-a", "targetIsActiveSetOfChannel": true}
                """
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(AuditLogDetail.Kinds.ImportedFromChannel, dto.Detail!.Kind);
        Assert.Equal(new AuditLogTargetEmoteSet("set-target-a", true, null), dto.Detail.TargetEmoteSet);
    }

    [Fact]
    public async Task ListAsync_ProjectsTheSetCentricImportsOwner_AsTheTargetEmoteSetsOwnerLogin()
    {
        // The set-centric endpoint's own shape (6.7): no targetIsActiveSetOfChannel key at all (no
        // Channel row to compare against), but targetOwnerTwitchLogin instead — ChannelName is null
        // on this row too (a global-admin-only entry), which the filter below matches on TargetId.
        await using var db = fixture.CreateDbContext();
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 19, 15, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = null,
            TargetType = "emoteSet",
            TargetId = "set-target-b",
            DetailsJson = """
                {"emoteCount": 2, "sourceChannelName": "sourcechannel", "sourceKind": "channel",
                 "targetEmoteSetId": "set-target-b", "targetOwnerSevenTvUserId": "owner-seven-tv-id",
                 "targetOwnerTwitchLogin": "handofblood"}
                """
        });
        await db.SaveChangesAsync();

        // No ChannelName filter possible here (the row's own ChannelName is null, and the filter
        // never matches a null column) — page size widened well past this class's usual 50 so a
        // channel.synced-free but import-heavy shared database still surfaces this exact row.
        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 500, new AuditLogFilter(AuditActions.EmotesSyncImported, null, null));

        var dto = Assert.Single(page.Items, i => i.TargetId == "set-target-b");
        Assert.Null(dto.ChannelName);
        Assert.Equal(new AuditLogTargetEmoteSet("set-target-b", null, "handofblood"), dto.Detail!.TargetEmoteSet);
    }

    [Fact]
    public async Task ListAsync_LeavesTargetEmoteSetNull_WhenTheDetailsNameNone()
    {
        // AK 32's other half: an import row that never reported a target set (E5 — the client omitted
        // TargetEmoteSetId) projects a complete, valid detail with TargetEmoteSet simply null — not a
        // missing row, not an exception.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-imp-no-target";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 19, 30, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncImported,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 4, "sourceChannelName": null, "sourceKind": "file"}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(AuditLogDetail.Kinds.ImportedFromFile, dto.Detail!.Kind);
        Assert.Null(dto.Detail.TargetEmoteSet);
    }

    [Fact]
    public async Task ListAsync_ProjectsTheSyncDeletedTargetEmoteSet_WhenTheSetIsTheActiveSet()
    {
        // Spec 6.6 (K5/T5.2): EmoteService.MarkDeletedAsync's set-scoped overload writes
        // "emoteSetId", a different property name than the import ladder's own "targetEmoteSetId"
        // (6.7) — this pins that ProjectDetail reads it too, on the bare EmoteCount kind.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-del-active";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 20, 0, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncDeleted,
            ChannelName = channel,
            TargetType = "emoteSet",
            TargetId = "set-del-active",
            DetailsJson = """{"emoteCount": 4, "emoteSetId": "set-del-active", "targetIsActiveSetOfChannel": true}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(AuditLogDetail.Kinds.EmoteCount, dto.Detail!.Kind);
        Assert.Equal(4, dto.Detail.Count);
        Assert.Equal(new AuditLogTargetEmoteSet("set-del-active", true, null), dto.Detail.TargetEmoteSet);
    }

    [Fact]
    public async Task ListAsync_ProjectsTheSyncRestoredTargetEmoteSet_WhenTheSetIsNotTheActiveSet()
    {
        // The paper-only branch (6.6): a report against a set that is not the channel's active one
        // still names its target, with a literal false rather than null.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-res-inactive";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 20, 15, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncRestored,
            ChannelName = channel,
            TargetType = "emoteSet",
            TargetId = "set-res-halloween",
            DetailsJson = """{"emoteCount": 2, "emoteSetId": "set-res-halloween", "targetIsActiveSetOfChannel": false}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogTargetEmoteSet("set-res-halloween", false, null), dto.Detail!.TargetEmoteSet);
    }

    [Fact]
    public async Task ListAsync_LeavesTheSyncDeletedTargetEmoteSetNull_ForALegacyBodyRow()
    {
        // The legacy `{ emoteIds }` body (spec 6.6, E3) never writes emoteSetId at all — this pins
        // that the set-scoped projection degrades the same way the import ladder's E5 case does,
        // rather than only being exercised incidentally by an unrelated theory case.
        await using var db = fixture.CreateDbContext();
        var channel = $"{ChannelPrefix}-del-legacy";
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            OccurredAtUtc = new DateTime(2099, 7, 31, 20, 30, 0, DateTimeKind.Utc),
            ActorTwitchUserId = "4711",
            ActorLogin = "sensitron",
            Action = AuditActions.EmotesSyncDeleted,
            ChannelName = channel,
            DetailsJson = """{"emoteCount": 6}"""
        });
        await db.SaveChangesAsync();

        var page = await new AuditLogQueryService(db)
            .ListAsync(1, 50, new AuditLogFilter(null, channel, null));

        var dto = Assert.Single(page.Items);
        Assert.Equal(AuditLogDetail.Kinds.EmoteCount, dto.Detail!.Kind);
        Assert.Null(dto.Detail.TargetEmoteSet);
    }

    private static AuditLogEntry NewEntry(string channelName, string action, DateTime occurredAtUtc, string actorLogin)
        => new()
        {
            OccurredAtUtc = occurredAtUtc,
            ActorTwitchUserId = "4711",
            ActorLogin = actorLogin,
            Action = action,
            ChannelName = channelName
        };

    private static async Task SeedAsync(AppDbContext db, string channelName, params (string Action, DateTime OccurredAtUtc)[] entries)
    {
        foreach (var (action, occurredAtUtc) in entries)
        {
            db.AuditLogEntries.Add(new AuditLogEntry
            {
                OccurredAtUtc = occurredAtUtc,
                ActorTwitchUserId = "4711",
                ActorLogin = "sensitron",
                Action = action,
                ChannelName = channelName
            });
        }

        await db.SaveChangesAsync();
    }
}
