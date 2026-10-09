using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;

namespace EmotePurge.Infrastructure.Tests.Fixtures;

// Seeds chat-log backfill rows for the tests of the hooks that touch them (leave, account deletion,
// retention). Plain EF inserts: the rows carry only what the schema requires.
internal static class BackfillRunSeed
{
    public static async Task<ChatLogBackfillRun> AddRunAsync(
        AppDbContext db,
        string channelId,
        ChatLogBackfillRunStatus status,
        string requestedByTwitchUserId = "seed-requester",
        string requestedByLogin = "seedrequester",
        DateTime? finishedAtUtc = null,
        bool withSnapshot = false)
    {
        var run = new ChatLogBackfillRun
        {
            ChannelId = channelId,
            Status = status,
            RequestedMonths = 1,
            WindowFrom = new DateOnly(2026, 8, 1),
            WindowTo = new DateOnly(2026, 9, 1),
            WeeksTotal = 5,
            EmoteSetId = "01SEEDSET0000000000000000",
            ArchiveBaseUrl = "https://logs.cyex.app/",
            RequestedByTwitchUserId = requestedByTwitchUserId,
            RequestedByLogin = requestedByLogin,
            RequestedAtUtc = DateTime.UtcNow,
            FinishedAtUtc = finishedAtUtc
        };
        if (withSnapshot)
        {
            run.Emotes.Add(new ChatLogBackfillRunEmote
            {
                EmoteId = Guid.NewGuid().ToString(),
                SevenTvEmoteId = "7tv-seed",
                Name = "SeedAlias"
            });
        }

        db.ChatLogBackfillRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    public static async Task AddCoverageDayAsync(AppDbContext db, string channelId, DateOnly day, long? runId)
    {
        db.ChatLogBackfillCoverage.Add(new ChatLogBackfillCoverageDay
        {
            ChannelId = channelId,
            Day = day,
            EmoteSetId = "01SEEDSET0000000000000000",
            ArchiveHost = "logs.cyex.app",
            RunId = runId,
            CompletedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
