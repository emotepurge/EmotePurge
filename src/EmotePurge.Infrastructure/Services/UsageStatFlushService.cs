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

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Validation still runs over Emote.Id alone (spec section 5, rule 4): a set switch does not
        // change which emotes exist, so the distinct id list for the FK check collapses the set
        // dimension back out — nothing here decides between two keys of the same emote.
        var emoteIds = usageCounts.Keys.Select(k => k.EmoteId).Distinct(StringComparer.Ordinal).ToList();

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

        var validIds = validEmotes.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

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

        // The projection now yields one row per key (spec section 5, rule 4), not per emote id: two
        // keys that share an EmoteId but differ in EmoteSetId (a cache swap between two chat
        // messages) write two separate rows instead of being summed together.
        var validEntries = usageCounts
            .Where(kvp => validIds.Contains(kvp.Key.EmoteId))
            .ToList();

        var entryEmoteIds = validEntries.Select(e => e.Key.EmoteId).ToArray();
        var entryEmoteSetIds = validEntries.Select(e => e.Key.EmoteSetId).ToArray();
        var useCounts = validEntries.Select(e => e.Value.Human).ToArray();
        var botUseCounts = validEntries.Select(e => e.Value.Bot).ToArray();
        var sharedChatUseCounts = validEntries.Select(e => e.Value.SharedChat).ToArray();

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
            INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
            SELECT input."EmoteId", input."EmoteSetId", @date, input."UseCount", input."BotUseCount", input."SharedChatUseCount"
            FROM UNNEST(@emoteIds, @emoteSetIds, @useCounts, @botUseCounts, @sharedChatUseCounts) AS input("EmoteId", "EmoteSetId", "UseCount", "BotUseCount", "SharedChatUseCount")
            ON CONFLICT ("EmoteId", "EmoteSetId", "Date")
            DO UPDATE SET
                "UseCount" = "UsageStats"."UseCount" + EXCLUDED."UseCount",
                "BotUseCount" = "UsageStats"."BotUseCount" + EXCLUDED."BotUseCount",
                "SharedChatUseCount" = "UsageStats"."SharedChatUseCount" + EXCLUDED."SharedChatUseCount";
            """;

        await db.Database.ExecuteSqlRawAsync(
            sql,
            [
                new NpgsqlParameter("date", NpgsqlDbType.Date) { Value = today },
                new NpgsqlParameter("emoteIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = entryEmoteIds },
                new NpgsqlParameter("emoteSetIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = entryEmoteSetIds },
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
