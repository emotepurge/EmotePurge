using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Services;

public class UsageStatFlushService(AppDbContext db, ILogger<UsageStatFlushService> logger) : IUsageStatFlushService
{
    public async Task<IReadOnlyCollection<string>> FlushAsync(IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> usageCounts, CancellationToken cancellationToken = default)
    {
        if (usageCounts.Count == 0)
        {
            return [];
        }

        // Provisional (T1.1): the schema and the SQL below still know only (EmoteId, Date), not the
        // observed set — that arrives with the index swap in T1.3b and the rewritten SQL in T1.4.
        // Until then, two keys that share an EmoteId but differ in EmoteSetId (a cache swap between
        // two chat messages) are summed into a single row here. This is the exact state T1.4
        // replaces; nothing downstream may come to rely on it.
        var countsByEmoteId = usageCounts
            .GroupBy(kvp => kvp.Key.EmoteId)
            .ToDictionary(
                g => g.Key,
                g => new EmoteUsageCounts(
                    g.Sum(kvp => kvp.Value.Human),
                    g.Sum(kvp => kvp.Value.Bot),
                    g.Sum(kvp => kvp.Value.SharedChat)));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var emoteIds = countsByEmoteId.Keys.ToList();

        // A channel leave deactivates instead of deleting nowadays, but archived emotes can still
        // be hard-deleted by an admin purge — and a count buffered before that would otherwise
        // violate the UsageStat.EmoteId FK and take the whole batch down with it.
        //
        // The owning channel name rides along on the same projection (a navigation join, no GroupBy —
        // rule 10 stays untouched) so the caller can announce exactly the channels that were written
        // without a second query.
        var validEmotes = await db.Emotes
            .Where(e => emoteIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Channel.ChannelName })
            .ToListAsync(cancellationToken);

        var validIds = validEmotes.Select(e => e.Id).ToList();

        if (validIds.Count < emoteIds.Count)
        {
            logger.LogWarning(
                "{Count} gepufferte Usage-Counts für inzwischen gelöschte Emotes verworfen.",
                emoteIds.Count - validIds.Count);
        }

        if (validIds.Count == 0)
        {
            return [];
        }

        var useCounts = validIds.Select(id => countsByEmoteId[id].Human).ToArray();
        var botUseCounts = validIds.Select(id => countsByEmoteId[id].Bot).ToArray();
        var sharedChatUseCounts = validIds.Select(id => countsByEmoteId[id].SharedChat).ToArray();

        // Atomic upsert rather than read-then-insert. The previous version decided per emote between
        // += and Add based on a prior SELECT, which is only correct while there is exactly one
        // writer: the final flush in UsageFlushWorker.StopAsync can overlap a regular 30s tick, and
        // both would then see "row missing" and insert it — one loses on the unique index and its
        // entire batch (up to 30s of chat across ~1.000 emotes) is discarded. This also drops the
        // extra SELECT with its ~1.000-element IN list every 30 seconds. Holds for all three
        // columns: "UseCount", "BotUseCount" and "SharedChatUseCount" are addended independently,
        // same reasoning either way.
        //
        // No "only rows with Human > 0" filter: a batch where an emote came only from bots still
        // gets a row (UseCount = 0, BotUseCount = n) — bot usage is preserved, not discarded (E1).
        // Same for a batch where an emote came only from a foreign room during a Shared Chat
        // session: it still gets a row (UseCount = 0, BotUseCount = 0, SharedChatUseCount = n) —
        // this is the negative probe from the #73 design's B6, not an edge case to special-case
        // away.
        const string sql = """
            INSERT INTO "UsageStats" ("EmoteId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
            SELECT input."EmoteId", @date, input."UseCount", input."BotUseCount", input."SharedChatUseCount"
            FROM UNNEST(@emoteIds, @useCounts, @botUseCounts, @sharedChatUseCounts) AS input("EmoteId", "UseCount", "BotUseCount", "SharedChatUseCount")
            ON CONFLICT ("EmoteId", "Date")
            DO UPDATE SET
                "UseCount" = "UsageStats"."UseCount" + EXCLUDED."UseCount",
                "BotUseCount" = "UsageStats"."BotUseCount" + EXCLUDED."BotUseCount",
                "SharedChatUseCount" = "UsageStats"."SharedChatUseCount" + EXCLUDED."SharedChatUseCount";
            """;

        await db.Database.ExecuteSqlRawAsync(
            sql,
            [
                new NpgsqlParameter("date", NpgsqlDbType.Date) { Value = today },
                new NpgsqlParameter("emoteIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = validIds.ToArray() },
                new NpgsqlParameter("useCounts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = useCounts },
                new NpgsqlParameter("botUseCounts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = botUseCounts },
                new NpgsqlParameter("sharedChatUseCounts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = sharedChatUseCounts },
            ],
            cancellationToken);

        // Stored channel names are already normalized (rule 9), so the stored values are the
        // normalized ones — no second normalization pass.
        return validEmotes
            .Select(e => e.ChannelName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
