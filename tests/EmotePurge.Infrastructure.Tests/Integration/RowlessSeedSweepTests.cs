using EmotePurge.Infrastructure.Migrations;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The one-off sweep of row-less unsettled seeds after the removal of the slot flag (#379, part B):
// EmoteSlotRollbackSql.SweepRowlessUnsettledSeedsSql, run as the operator runs it. Runs against the
// shared head-schema database with unique ids and asserts by id, so rows of other tests do not matter.
[Collection("Postgres")]
public class RowlessSeedSweepTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Sweep_DeletesOnlyUnsettledSeedsWithoutRows_AndASecondRunKeepsTheRest()
    {
        var k = Guid.NewGuid().ToString("N");
        await using var db = fixture.CreateDbContext();
        await SeedAsync(db, k);

        var counted = await CountAsync(db);
        var deleted = await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.SweepRowlessUnsettledSeedsSql);
        await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.SweepRowlessUnsettledSeedsSql);

        await using var verify = fixture.CreateDbContext();
        var left = await verify.EmoteSlots.Where(s => s.Id.EndsWith(k)).Select(s => s.Id.Replace(k, "")).ToListAsync();
        // (i) unsettled seed without rows and (vi) one that a finished run's snapshot row points to: gone.
        Assert.DoesNotContain("s-bare", left);
        Assert.DoesNotContain("s-snap", left);
        // (ii) rows of another set, (iii) only imported rows, (iv) settled empty archived seed, (v) own slot: stay.
        // (vii) the seed an active (running) run's snapshot row points to: stays, and is not counted.
        Assert.Equal(["s-active", "s-imported", "s-own", "s-settled", "s-twoset"], left.Order().ToList());
        // The snapshot row of the finished run stays.
        var snapshotSlot = "s-snap" + k;
        Assert.Equal(1, await verify.Database.SqlQuery<int>(
            $"""SELECT count(*)::int AS "Value" FROM "ChatLogBackfillRunEmotes" WHERE "SlotId" = {snapshotSlot}""").SingleAsync());
        // The count statement names the same rows the delete removes, and nothing is left to count.
        Assert.Equal(counted, deleted);
        Assert.Equal(0, await CountAsync(db));
    }

    private static async Task<long> CountAsync(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = EmoteSlotRollbackSql.CountRowlessUnsettledSeedsSql;
            return (long)(await command.ExecuteScalarAsync())!;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task SeedAsync(AppDbContext db, string k)
    {
#pragma warning disable EF1002 // locally generated Guid, not user input
        await db.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO "Channels" ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
            VALUES ('c{k}', 'tw{k}', 'n{k}', 'SETA', true, now(), NULL);
            INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "LastSyncedAt")
            VALUES ('e1{k}', '7a{k}', 'c{k}', 'EmoteOne', '', false, now());
            INSERT INTO "EmoteSlots" ("Id", "EmoteId", "Name", "AddedAt", "AddedAtFromRest", "IsArchived", "ArchivedAt", "LastSyncedAt", "SeedUnsettled")
            VALUES ('s-bare{k}',     'e1{k}', 'Bare',     NULL,         false, false, NULL, now(), true),
                   ('s-snap{k}',     'e1{k}', 'Snap',     NULL,         false, false, NULL, now(), true),
                   ('s-active{k}',   'e1{k}', 'Active',   NULL,         false, false, NULL, now(), true),
                   ('s-twoset{k}',   'e1{k}', 'TwoSet',   NULL,         false, false, NULL, now(), true),
                   ('s-imported{k}', 'e1{k}', 'Imported', NULL,         false, false, NULL, now(), true),
                   ('s-settled{k}',  'e1{k}', 'Settled',  '2026-03-04', true,  true,  now(), now(), false),
                   ('s-own{k}',      'e1{k}', 'Own',      '2026-03-05', true,  false, NULL, now(), false);
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source", "SlotId")
            VALUES ('e1{k}', 'SETB', '2026-08-01', 3, 0, 0, 0, 's-twoset{k}'),
                   ('e1{k}', 'SETB', '2026-08-02', 2, 0, 0, 1, 's-imported{k}');
            INSERT INTO "ChatLogBackfillRuns"
                ("ChannelId", "Status", "RequestedMonths", "WindowFrom", "WindowTo", "WeeksTotal", "EmoteSetId", "ArchiveBaseUrl",
                 "RequestedByTwitchUserId", "RequestedByLogin", "RequestedAtUtc")
            VALUES ('c{k}', 'completed', 1, '2026-07-01', '2026-08-05', 5, 'SETB', 'https://logs.cyex.app/', '42', 'run{k}', now());
            INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow", "SlotId", "CreatedSlot")
            SELECT r."Id", 'e1{k}', '7a{k}', 'Snap', false, 's-snap{k}', false
            FROM "ChatLogBackfillRuns" r WHERE r."RequestedByLogin" = 'run{k}';
            INSERT INTO "ChatLogBackfillRuns"
                ("ChannelId", "Status", "RequestedMonths", "WindowFrom", "WindowTo", "WeeksTotal", "EmoteSetId", "ArchiveBaseUrl",
                 "RequestedByTwitchUserId", "RequestedByLogin", "RequestedAtUtc")
            VALUES ('c{k}', 'running', 1, '2026-07-01', '2026-08-05', 5, 'SETB', 'https://logs.cyex.app/', '42', 'active{k}', now());
            INSERT INTO "ChatLogBackfillRunEmotes" ("RunId", "EmoteId", "SevenTvEmoteId", "Name", "CreatedRow", "SlotId", "CreatedSlot")
            SELECT r."Id", 'e1{k}', '7a{k}', 'Active', false, 's-active{k}', false
            FROM "ChatLogBackfillRuns" r WHERE r."RequestedByLogin" = 'active{k}';
            """);
#pragma warning restore EF1002
    }
}
