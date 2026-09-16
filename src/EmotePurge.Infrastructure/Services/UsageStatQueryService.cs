using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class UsageStatQueryService(AppDbContext db) : IUsageStatQueryService
{
    private const string FromMustPrecedeToMessage = "'from' must be less than or equal to 'to'.";

    public async Task<IReadOnlyList<EmoteUsageDto>> GetUsageStatsAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        // UseCount alone, like every product read path in this class: neither bot nor shared-chat
        // usage is this channel's own human usage. A debug raw list, so it never carried the D5
        // transitional sum to begin with and needed nothing reverted when that bridge fell.
        return await db.UsageStats
            .Where(u => u.Emote.Channel.ChannelName == normalized)
            .OrderByDescending(u => u.Date).ThenByDescending(u => u.UseCount)
            .Select(u => new EmoteUsageDto(u.Emote.Name, u.Date, u.UseCount))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EmoteUsageContextDto>> GetUsageContextAsync(
        string channelName, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException(FromMustPrecedeToMessage, nameof(from));
        }

        var normalized = ChannelName.Normalize(channelName);

        // GroupBy+Sum fails to translate when the filtered source still carries the
        // Emote/Channel navigation joins from the Where clause (EF Core/Npgsql limitation:
        // falls back to client-eval "g.AsQueryable().Sum(...)" and throws). Resolving the
        // channel's emote IDs into a plain list first keeps the grouped query scoped to a
        // single table, which translates cleanly.
        // Archived (already-deleted) emotes are excluded — they shouldn't reappear as delete
        // candidates in a usage-stats UI just because they still have historical UsageStat rows.
        var channelEmotes = await db.Emotes
            .Where(e => e.Channel.ChannelName == normalized && !e.IsArchived)
            .Select(e => new { e.Id, e.Name, e.SevenTvEmoteId, e.ImageUrl, e.FirstSeenAt })
            .ToListAsync(cancellationToken);

        if (channelEmotes.Count == 0)
        {
            return [];
        }

        var emoteIds = channelEmotes.Select(e => e.Id).ToList();

        // Both range bounds are inclusive, so the window is one day longer than the difference —
        // and the preceding window has to be exactly as long for the two sums to be comparable.
        var windowLength = to.DayNumber - from.DayNumber + 1;
        var previousFrom = from.AddDays(-windowLength);

        // One pass, three aggregates, all three over UseCount alone — the channel's own human
        // usage, which is what this grid judges deletion candidates by. Both side columns are
        // outside that: a bot-only row (UseCount = 0, BotUseCount > 0) and a shared-only row
        // (UseCount = 0, SharedChatUseCount > 0, usage mirrored in from another channel's shared
        // chat) read the same way here, namely as no use at all. Reading UseCount alone is also
        // what keeps the covering index (EmoteId, Date) INCLUDE (UseCount) serving this as an
        // index-only scan — the reason the index was left untouched through the D5 transition
        // rather than widened and narrowed again (docs/DECISIONS.md 2026-09-06, Task 4).
        // LastUsedDate is deliberately unbounded in time: the max is the emote's last use ever, and
        // clipping it to the range would make it a restatement of the total. The two sums need no
        // date-independent predicate — a row without own usage contributes 0 to a sum on its own.
        // LastUsedDate does need one: a row's mere existence is no proof of use once the flush can
        // write UseCount = 0 rows, so neither a bot-only nor a shared-only day may read as
        // "last used".
        var aggregates = await db.UsageStats
            .Where(u => emoteIds.Contains(u.EmoteId))
            .GroupBy(u => u.EmoteId)
            .Select(g => new
            {
                EmoteId = g.Key,
                TotalUseCount = g.Sum(u => u.Date >= from && u.Date <= to ? u.UseCount : 0),
                PreviousWindowUseCount = g.Sum(u => u.Date >= previousFrom && u.Date < from ? u.UseCount : 0),
                LastUsedDate = g.Max(u => u.UseCount > 0 ? (DateOnly?)u.Date : null)
            })
            .ToDictionaryAsync(g => g.EmoteId, cancellationToken);

        // Zero-filled for every active emote (not just ones with a UsageStat row already) —
        // an unused-but-active emote must still be findable/selectable in a usage-stats UI.
        return channelEmotes
            .Select(e =>
            {
                var aggregate = aggregates.GetValueOrDefault(e.Id);
                return new EmoteUsageContextDto(
                    e.Id,
                    e.Name,
                    e.SevenTvEmoteId,
                    e.ImageUrl,
                    aggregate?.TotalUseCount ?? 0,
                    aggregate?.LastUsedDate,
                    aggregate?.PreviousWindowUseCount ?? 0,
                    e.FirstSeenAt);
            })
            .OrderByDescending(t => t.TotalUseCount)
            .ToList();
    }

    public async Task<EmoteUsageSeriesDto?> GetDailySeriesAsync(
        string channelName, string emoteId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException(FromMustPrecedeToMessage, nameof(from));
        }

        var normalized = ChannelName.Normalize(channelName);

        // Resolved against the channel, not looked up by id alone: emoteId is a client-supplied
        // value, and without the join a caller with access to channel A could read the series of an
        // emote from channel B. IsArchived is deliberately not filtered — an archived emote is
        // unreachable from the usage grid, but a subset vote session still lists it as a ballot
        // member, and its history is real.
        var emote = await db.Emotes
            .Where(e => e.Id == emoteId && e.Channel.ChannelName == normalized)
            .Select(e => new { e.Id, e.Name, e.ChannelId })
            .FirstOrDefaultAsync(cancellationToken);
        if (emote is null)
        {
            return null;
        }

        // Sparse on purpose (only days with usage). Value and predicate both run over UseCount
        // alone, same reasoning as GetUsageContextAsync — a bot-only and a shared-only day are
        // equally absent from this series, because neither is a day this channel used the emote.
        var days = await db.UsageStats
            .Where(u => u.EmoteId == emote.Id && u.Date >= from && u.Date <= to && u.UseCount > 0)
            .OrderBy(u => u.Date)
            .Select(u => new EmoteDailyUsageDto(u.Date, u.UseCount))
            .ToListAsync(cancellationToken);

        // First/last use ever, unbounded in time — same reasoning as LastUsedDate in
        // GetUsageContextAsync, including the UseCount > 0 predicate against rows with no own
        // usage. Single-table GroupBy, so rule 10 is not even touched.
        var bounds = await db.UsageStats
            .Where(u => u.EmoteId == emote.Id && u.UseCount > 0)
            .GroupBy(u => u.EmoteId)
            .Select(g => new
            {
                First = g.Min(u => (DateOnly?)u.Date),
                Last = g.Max(u => (DateOnly?)u.Date)
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Range-bounded unlike the bounds above: the consumer overlays these on exactly the
        // rendered window. LiveMinutes > 0 is defensive — the poll never writes a zero row.
        // Served by the covering index (ChannelId, Date) INCLUDE (LiveMinutes).
        var liveDays = await db.ChannelLiveDays
            .Where(l => l.ChannelId == emote.ChannelId && l.Date >= from && l.Date <= to && l.LiveMinutes > 0)
            .OrderBy(l => l.Date)
            .Select(l => l.Date)
            .ToListAsync(cancellationToken);

        return new EmoteUsageSeriesDto(
            emote.Id,
            emote.Name,
            from,
            to,
            days.Sum(d => d.UseCount),
            bounds?.First,
            bounds?.Last,
            days,
            liveDays);
    }

    public async Task<ChannelUsageSeriesDto> GetChannelSeriesAsync(
        string channelName, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException(FromMustPrecedeToMessage, nameof(from));
        }

        var normalized = ChannelName.Normalize(channelName);

        var channelId = await db.Channels
            .Where(c => c.ChannelName == normalized)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (channelId is null)
        {
            return new ChannelUsageSeriesDto(from, to, [], []);
        }

        // Same exclusion as GetUsageContextAsync: this feeds the usage grid, and an archived emote
        // is not on it. The single-emote series keeps archived emotes because a ballot can still
        // list them — that caller asks by id and knows what it is asking for.
        var emoteIds = await db.Emotes
            .Where(e => e.ChannelId == channelId && !e.IsArchived)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        var liveDays = await db.ChannelLiveDays
            .Where(l => l.ChannelId == channelId && l.Date >= from && l.Date <= to && l.LiveMinutes > 0)
            .OrderBy(l => l.Date)
            .Select(l => l.Date)
            .ToListAsync(cancellationToken);
        var liveDayOffsets = liveDays.Select(d => d.DayNumber - from.DayNumber).ToList();

        if (emoteIds.Count == 0)
        {
            return new ChannelUsageSeriesDto(from, to, liveDayOffsets, []);
        }

        // One scan over the whole channel, then grouped in memory. Deliberately not a GroupBy in
        // SQL: the grouping here is pure partitioning with no aggregate to push down, so the
        // database would do the same work and hand back the same number of rows either way — and
        // rule 10 makes a navigation-joined GroupBy the fragile shape to reach for. Ordering by
        // (EmoteId, Date) is what lets the in-memory GroupBy below emit each emote's days already
        // ascending. Value and predicate run over UseCount alone: an emote whose only rows carry no
        // own usage — bot-only or shared-only — drops out of Emotes entirely, the same way an emote
        // with no rows at all does. Reading UseCount alone is what the covering index
        // (EmoteId, Date) INCLUDE (UseCount) serves as an index-only scan; see the index note on
        // GetUsageContextAsync.
        var rows = await db.UsageStats
            .Where(u => emoteIds.Contains(u.EmoteId) && u.Date >= from && u.Date <= to && u.UseCount > 0)
            .OrderBy(u => u.EmoteId).ThenBy(u => u.Date)
            .Select(u => new { u.EmoteId, u.Date, u.UseCount })
            .ToListAsync(cancellationToken);

        var emotes = rows
            .GroupBy(r => r.EmoteId)
            .Select(g => new EmoteSeriesEntryDto(
                g.Key,
                g.Select(r => new[] { r.Date.DayNumber - from.DayNumber, r.UseCount }).ToList()))
            .ToList();

        return new ChannelUsageSeriesDto(from, to, liveDayOffsets, emotes);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetTotalsByEmoteIdsAsync(
        IReadOnlyCollection<string> emoteIds, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException(FromMustPrecedeToMessage, nameof(from));
        }

        if (emoteIds.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        // Materialized list rather than the caller's collection: the same rule-10 reason as above,
        // the grouped query has to stay scoped to a single table. The sum runs over UseCount alone,
        // same reasoning as GetUsageContextAsync — and the one caller, the usage column beside a
        // vote session's ballot, deliberately gets no caption of its own for that: it is context
        // next to the ballot, not the score, and the bot split changed it just as quietly
        // (docs/DECISIONS.md 2026-09-08).
        var ids = emoteIds.ToList();

        return await db.UsageStats
            .Where(u => ids.Contains(u.EmoteId) && u.Date >= from && u.Date <= to)
            .GroupBy(u => u.EmoteId)
            .Select(g => new { EmoteId = g.Key, TotalUseCount = g.Sum(u => u.UseCount) })
            .ToDictionaryAsync(g => g.EmoteId, g => g.TotalUseCount, cancellationToken);
    }

    public async Task<DateOnly?> GetEarliestBotUsageDateAsync(string channelId, CancellationToken cancellationToken = default)
    {
        // Rule 10: resolve the channel's emote ids to a plain scalar list first, then aggregate
        // over UsageStats alone — the same shape EmoteSetStatusService used before this method
        // absorbed its query (a MIN grouped straight off a Where that still carries the Emote
        // navigation risks the client-eval fallback that GroupBy hits there). Archived emotes are
        // deliberately included: a bot sighting on an emote since deleted from 7TV still tells us
        // when the separation started for this channel. Projected to DateOnly? — a non-nullable
        // Min throws on an empty result set, and "no bot ever seen" is exactly the empty case this
        // has to handle without an exception.
        var emoteIds = await db.Emotes
            .Where(e => e.ChannelId == channelId)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        return await db.UsageStats
            .Where(u => emoteIds.Contains(u.EmoteId) && u.BotUseCount > 0)
            .Select(u => (DateOnly?)u.Date)
            .MinAsync(cancellationToken);
    }

    public async Task<DateOnly?> GetEarliestSharedChatUsageDateAsync(string channelId, CancellationToken cancellationToken = default)
    {
        // The twin of GetEarliestBotUsageDateAsync, and identical in shape for the same reasons:
        // rule 10 (resolve the emote ids to a plain scalar list before aggregating over UsageStats
        // alone), archived emotes deliberately included (a shared-chat sighting on an emote since
        // deleted from 7TV still tells us when the separation started for this channel), and the
        // projection to DateOnly? because a non-nullable Min throws on the empty result set that
        // "no shared chat ever seen" produces.
        var emoteIds = await db.Emotes
            .Where(e => e.ChannelId == channelId)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        return await db.UsageStats
            .Where(u => emoteIds.Contains(u.EmoteId) && u.SharedChatUseCount > 0)
            .Select(u => (DateOnly?)u.Date)
            .MinAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EmoteLifetimeDto>> GetEmoteLifetimesAsync(string channelId, CancellationToken cancellationToken = default)
    {
        // A plain projection over Emotes with a scalar ChannelId filter — no navigation join, no
        // GroupBy, so rule 10 does not even come into play here. Archived emotes are deliberately
        // included (see the interface doc comment), and the ordering is ordinal on Id so the
        // harness's hash over this list is stable regardless of insertion order. That ordinal
        // guarantee rests on the database's collation, though: OrderBy(e => e.Id) translates to a
        // plain ORDER BY "Id" with no COLLATE "C", so a non-C collation could in principle order
        // differently from string.CompareOrdinal. It holds for the ids actually stored here — hex
        // GUIDs with hyphens at fixed positions, a character set essentially every collation orders
        // the same way — and a collation change would only ever produce a different (still
        // deterministic) input hash and thus a fresh report file, never a wrong count.
        return await db.Emotes
            .AsNoTracking()
            .Where(e => e.ChannelId == channelId)
            .OrderBy(e => e.Id)
            .Select(e => new EmoteLifetimeDto(e.Id, e.Name, e.IsArchived, e.FirstSeenAt, e.ArchivedAt, e.LastSyncedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UsageStatRowDto>> GetRowsAsync(
        IReadOnlyCollection<string> emoteIds, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException("'from' darf nicht nach 'to' liegen.", nameof(from));
        }

        if (emoteIds.Count == 0)
        {
            return [];
        }

        // Rule 10: a plain Where over UsageStats with a scalar id list and the date range — no
        // join, no GroupBy. Materialized to a plain list first for the same reason
        // GetTotalsByEmoteIdsAsync does: Contains against the caller's own collection type can
        // fail to translate. UseCount > 0 is deliberately absent — see the interface doc comment,
        // a bot-only row is exactly what the harness's bot-inclusive total needs.
        var ids = emoteIds.ToList();

        return await db.UsageStats
            .AsNoTracking()
            .Where(u => ids.Contains(u.EmoteId) && u.Date >= from && u.Date <= to)
            .OrderBy(u => u.EmoteId).ThenBy(u => u.Date)
            .Select(u => new UsageStatRowDto(u.EmoteId, u.Date, u.UseCount, u.BotUseCount, u.SharedChatUseCount))
            .ToListAsync(cancellationToken);
    }
}
