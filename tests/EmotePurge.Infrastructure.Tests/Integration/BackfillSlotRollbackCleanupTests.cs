using EmotePurge.Infrastructure.Migrations;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Step 3b of the chat-log backfill rollback (#375, spec 12.3, AK 39): the statement in
// EmoteSlotRollbackSql.DeleteBackfillCreatedSlotsSql, run as the operator runs it after step 3 and
// before step 4. Runs against the shared head-schema database with unique ids and asserts by id, so
// rows of other tests do not matter.
[Collection("Postgres")]
public class BackfillSlotRollbackCleanupTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Step3b_DeletesOnlyTheBackfillCreatedSlotWithoutLiveUsage()
    {
        var k = Guid.NewGuid().ToString("N");
        await using var db = fixture.CreateDbContext();
        await SeedAsync(db, k);

        await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.DeleteBackfillCreatedSlotsSql);

        await using var verify = fixture.CreateDbContext();
        var left = await verify.EmoteSlots.Where(s => s.Id.EndsWith(k)).Select(s => s.Id.Replace(k, "")).ToListAsync();
        // (i) created, no live usage: gone, together with its imported row.
        Assert.DoesNotContain("s-created", left);
        Assert.False(await verify.UsageStats.AnyAsync(u => u.SlotId == "s-created" + k));
        // (ii) created but used live (Source = 0), the #373 hand-off: stays with its row.
        Assert.Contains("s-live", left);
        Assert.True(await verify.UsageStats.AnyAsync(u => u.SlotId == "s-live" + k && u.Source == 0));
        // (iv) a seed the enqueue settled is not CreatedSlot: stays, so does an archived slot with an archive time.
        Assert.Contains("s-seed", left);
        Assert.Contains("s-archived", left);
        // An active slot is never touched, even if a snapshot row claims it.
        Assert.Contains("s-active", left);
        Assert.Equal(["s-active", "s-archived", "s-live", "s-seed"], left.Order().ToList());
        // The emote itself is untouched.
        Assert.True(await verify.Emotes.AnyAsync(e => e.Id == "e1" + k));
    }

    [Fact]
    public async Task Step3b_AfterStep3_FindsNothingMoreOfAPlaceholderTheCleanupRemoved_AndASecondRunChangesNothing()
    {
        var k = Guid.NewGuid().ToString("N");
        await using var db = fixture.CreateDbContext();
        await SeedAsync(db, k);

        // (iii) step 3 removes the placeholder emote; its slots go with it by cascade.
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Emotes" WHERE "Id" = {0};""", "e2" + k);
        Assert.False(await db.EmoteSlots.AnyAsync(s => s.Id == "s-ph" + k));

        await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.DeleteBackfillCreatedSlotsSql);
        var afterFirst = await db.EmoteSlots.Where(s => s.Id.EndsWith(k)).CountAsync();
        await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.DeleteBackfillCreatedSlotsSql);

        Assert.Equal(4, afterFirst);
        Assert.Equal(4, await db.EmoteSlots.Where(s => s.Id.EndsWith(k)).CountAsync());
    }

    [Theory]
    [InlineData(nameof(EmoteSlotRollbackSql.DeleteBackfillCreatedSlotsSql))]
    [InlineData(nameof(EmoteSlotRollbackSql.CountRowlessUnsettledSeedsSql))]
    [InlineData(nameof(EmoteSlotRollbackSql.SweepRowlessUnsettledSeedsSql))]
    public void OperationsDocQuotesTheStatementIdentically(string constantName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "Operations.md")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var doc = File.ReadAllLines(Path.Combine(dir.FullName, "docs", "Operations.md")).Select(l => l.Trim()).ToList();
        var text = (string)typeof(EmoteSlotRollbackSql).GetField(constantName)!.GetRawConstantValue()!;
        var statement = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var found = Enumerable.Range(0, doc.Count).Any(i => doc.Skip(i).Take(statement.Count).SequenceEqual(statement));
        Assert.True(found, $"{constantName} not found verbatim in docs/Operations.md");
    }

    private static async Task SeedAsync(AppDbContext db, string k)
    {
#pragma warning disable EF1002 // locally generated Guid, not user input
        await db.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO "Channels" ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
            VALUES ('c{k}', 'tw{k}', 'n{k}', 'SETA', true, now(), NULL);
            INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "LastSyncedAt")
            VALUES ('e1{k}', '7a{k}', 'c{k}', 'EmoteOne', '', false, now()),
                   ('e2{k}', '7b{k}', 'c{k}', 'EmoteTwo', '', true, now());
            INSERT INTO "EmoteSlots" ("Id", "EmoteId", "Name", "AddedAt", "AddedAtFromRest", "IsArchived", "ArchivedAt", "LastSyncedAt")
            VALUES ('s-created{k}',  'e1{k}', 'Created',  '2026-03-01', true, true,  NULL,  now()),
                   ('s-live{k}',     'e1{k}', 'Live',     '2026-03-02', true, true,  NULL,  now()),
                   ('s-seed{k}',     'e1{k}', 'Seed',     '2026-03-03', true, true,  NULL,  now()),
                   ('s-archived{k}', 'e1{k}', 'Archived', '2026-03-04', true, true,  now(), now()),
                   ('s-active{k}',   'e1{k}', 'Active',   '2026-03-05', true, false, NULL,  now()),
                   ('s-ph{k}',       'e2{k}', 'Ph',       '2026-03-06', true, true,  NULL,  now());
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source", "SlotId")
            VALUES ('e1{k}', 'SETB', '2026-08-01', 3, 0, 0, 1, 's-created{k}'),
                   ('e1{k}', 'SETB', '2026-08-02', 2, 0, 0, 1, 's-live{k}'),
                   ('e1{k}', 'SETB', '2026-08-03', 4, 0, 0, 0, 's-live{k}'),
                   ('e1{k}', 'SETB', '2026-08-04', 5, 0, 0, 1, 's-seed{k}');
            INSERT INTO "ChatLogBackfillRuns"
                ("ChannelId", "Status", "RequestedMonths", "WindowFrom", "WindowTo", "WeeksTotal", "EmoteSetId", "ArchiveBaseUrl",
                 "RequestedByTwitchUserId", "RequestedByLogin", "RequestedAtUtc")
            VALUES ('c{k}', 'completed', 1, '2026-07-01', '2026-08-05', 5, 'SETB', 'https://logs.cyex.app/', '42', 'run{k}', now());
            INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow", "SlotId", "CreatedSlot")
            SELECT r."Id", v.e, v.s, v.n, false, v.slot, v.created
            FROM "ChatLogBackfillRuns" r,
                 (VALUES ('e1{k}', '7a{k}', 'Created',  's-created{k}',  true),
                         ('e1{k}', '7a{k}', 'Live',     's-live{k}',     true),
                         ('e1{k}', '7a{k}', 'Seed',     's-seed{k}',     false),
                         ('e1{k}', '7a{k}', 'Archived', 's-archived{k}', false),
                         ('e1{k}', '7a{k}', 'Active',   's-active{k}',   true),
                         ('e2{k}', '7b{k}', 'Ph',       's-ph{k}',       true)) v(e, s, n, slot, created)
            WHERE r."RequestedByLogin" = 'run{k}';
            """);
#pragma warning restore EF1002
    }
}
