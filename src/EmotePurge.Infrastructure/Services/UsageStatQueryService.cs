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
        string channelName, DateOnly from, DateOnly to, string? emoteSetId = null, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException(FromMustPrecedeToMessage, nameof(from));
        }

        var normalized = ChannelName.Normalize(channelName);

        // The channel row first, for its active set id: "no set asked for" means "the one we
        // currently observe", and only the row knows which that is. An unknown channel answers
        // empty, exactly as it did when the emote query alone came back with nothing.
        var channel = await db.Channels
            .Where(c => c.ChannelName == normalized)
            .Select(c => new { c.Id, c.ActiveEmoteSetId })
            .FirstOrDefaultAsync(cancellationToken);
        if (channel is null)
        {
            return [];
        }

        var setId = emoteSetId ?? channel.ActiveEmoteSetId;
        var isActiveSet = string.Equals(setId, channel.ActiveEmoteSetId, StringComparison.Ordinal);

        // GroupBy+Sum fails to translate when the filtered source still carries the
        // Emote/Channel navigation joins from the Where clause (EF Core/Npgsql limitation:
        // falls back to client-eval "g.AsQueryable().Sum(...)" and throws). Resolving the
        // channel's emote IDs into a plain list first keeps the grouped query scoped to a
        // single table, which translates cleanly.
        // Every row of the channel, archived ones included — the base set is narrowed below rather
        // than here, because the two views need different halves of this list: the active set's
        // view excludes archived emotes (they are gone from 7TV and must not reappear as delete
        // candidates just because they still carry history), while a non-active set's view is a
        // historical one and keeps them (spec E16/E23). The name-twin lookup needs the whole list
        // either way — an emote's twin under another set is usually precisely an archived row.
        var allChannelEmotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id)
            .Select(e => new { e.Id, e.Name, e.SevenTvEmoteId, e.ImageUrl, e.FirstSeenAt, e.IsArchived })
            .ToListAsync(cancellationToken);

        if (allChannelEmotes.Count == 0)
        {
            return [];
        }

        var emoteIds = allChannelEmotes.Select(e => e.Id).ToList();

        // Both range bounds are inclusive, so the window is one day longer than the difference —
        // and the preceding window has to be exactly as long for the two sums to be comparable.
        var windowLength = to.DayNumber - from.DayNumber + 1;
        var previousFrom = from.AddDays(-windowLength);

        // One pass, three aggregates, all three over UseCount alone — the channel's own human
        // usage, which is what this grid judges deletion candidates by. Both side columns are
        // outside that: a bot-only row (UseCount = 0, BotUseCount > 0) and a shared-only row
        // (UseCount = 0, SharedChatUseCount > 0, usage mirrored in from another channel's shared
        // chat) read the same way here, namely as no use at all. Reading UseCount alone, and
        // filtering on the set id rather than on anything outside the row, is what keeps the
        // covering index (EmoteId, EmoteSetId, Date) INCLUDE (UseCount) serving this as an
        // index-only scan — the reason the index was widened by exactly the new key column and
        // nothing else (docs/DECISIONS.md 2026-09-06 Task 4; #200 spec 4.1).
        // LastUsedDate is deliberately unbounded in time: the max is the emote's last use ever, and
        // clipping it to the range would make it a restatement of the total. The two sums need no
        // date-independent predicate — a row without own usage contributes 0 to a sum on its own.
        // LastUsedDate does need one: a row's mere existence is no proof of use once the flush can
        // write UseCount = 0 rows, so neither a bot-only nor a shared-only day may read as
        // "last used".
        var aggregates = await db.UsageStats
            .Where(u => emoteIds.Contains(u.EmoteId) && u.EmoteSetId == setId)
            .GroupBy(u => u.EmoteId)
            .Select(g => new
            {
                EmoteId = g.Key,
                TotalUseCount = g.Sum(u => u.Date >= from && u.Date <= to ? u.UseCount : 0),
                PreviousWindowUseCount = g.Sum(u => u.Date >= previousFrom && u.Date < from ? u.UseCount : 0),
                LastUsedDate = g.Max(u => u.UseCount > 0 ? (DateOnly?)u.Date : null)
            })
            .ToDictionaryAsync(g => g.EmoteId, cancellationToken);

        var nameTwinSetIds = await LoadNameTwinSetIdsAsync(allChannelEmotes.Select(e => (e.Id, e.Name, e.SevenTvEmoteId)), setId, cancellationToken);

        // Active set: zero-filled for every unarchived emote (not just ones with a UsageStat row
        // already) — an unused-but-active emote must still be findable/selectable in a usage-stats
        // UI. Non-active set: no zero-filling at all, because "every row of the channel" would be a
        // list of a thousand zeroes for a set that held forty. The aggregate dictionary is exactly
        // the "has at least one count under this set" test spec E16 asks for — it is keyed by the
        // set-filtered query above and carries no date predicate of its own.
        return allChannelEmotes
            .Where(e => isActiveSet ? !e.IsArchived : aggregates.ContainsKey(e.Id))
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
                    e.FirstSeenAt,
                    e.IsArchived,
                    nameTwinSetIds.TryGetValue(e.Id, out var twins) ? twins : []);
            })
            .OrderByDescending(t => t.TotalUseCount)
            .ToList();
    }

    public async Task<EmoteUsageSeriesDto?> GetDailySeriesAsync(
        string channelName, string emoteId, DateOnly from, DateOnly to, string? emoteSetId = null, CancellationToken cancellationToken = default)
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
        // The channel's active set id comes along on the same projection — a plain navigation read
        // with no GroupBy behind it, so rule 10 is untouched and it saves a second round trip.
        var emote = await db.Emotes
            .Where(e => e.Id == emoteId && e.Channel.ChannelName == normalized)
            .Select(e => new { e.Id, e.Name, e.ChannelId, e.Channel.ActiveEmoteSetId })
            .FirstOrDefaultAsync(cancellationToken);
        if (emote is null)
        {
            return null;
        }

        var setId = emoteSetId ?? emote.ActiveEmoteSetId;

        // Sparse on purpose (only days with usage). Value and predicate both run over UseCount
        // alone, same reasoning as GetUsageContextAsync — a bot-only and a shared-only day are
        // equally absent from this series, because neither is a day this channel used the emote.
        // The set filter is what makes the drilldown agree with the row it was opened from: the
        // grid's total is one set's counts, so the days behind it have to be the same set's.
        var days = await db.UsageStats
            .Where(u => u.EmoteId == emote.Id && u.EmoteSetId == setId && u.Date >= from && u.Date <= to && u.UseCount > 0)
            .OrderBy(u => u.Date)
            .Select(u => new EmoteDailyUsageDto(u.Date, u.UseCount))
            .ToListAsync(cancellationToken);

        // First/last use ever, unbounded in time — same reasoning as LastUsedDate in
        // GetUsageContextAsync, including the UseCount > 0 predicate against rows with no own
        // usage. Bounded by the set, though, and deliberately so: "first used" next to a set's
        // numbers must mean first used under that set. Single-table GroupBy, so rule 10 is not
        // even touched.
        var bounds = await db.UsageStats
            .Where(u => u.EmoteId == emote.Id && u.EmoteSetId == setId && u.UseCount > 0)
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
        string channelName, DateOnly from, DateOnly to, string? emoteSetId = null, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException(FromMustPrecedeToMessage, nameof(from));
        }

        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.Channels
            .Where(c => c.ChannelName == normalized)
            .Select(c => new { c.Id, c.ActiveEmoteSetId })
            .FirstOrDefaultAsync(cancellationToken);
        if (channel is null)
        {
            return new ChannelUsageSeriesDto(from, to, [], []);
        }

        var setId = emoteSetId ?? channel.ActiveEmoteSetId;
        var isActiveSet = string.Equals(setId, channel.ActiveEmoteSetId, StringComparison.Ordinal);

        // Same base set as GetUsageContextAsync, and for the same reasons: the active set's sheet
        // excludes archived emotes because it is a deletion grid, a non-active set's sheet keeps
        // them because it is a historical view. Both Id and SevenTvEmoteId are projected here so
        // the entries below can be named by the 7TV id without a second query or a join — the id
        // list is the only place that mapping exists, and it is already in memory.
        var emotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && (!isActiveSet || !e.IsArchived))
            .Select(e => new { e.Id, e.SevenTvEmoteId })
            .ToListAsync(cancellationToken);
        var emoteIds = emotes.Select(e => e.Id).ToList();
        var sevenTvEmoteIdByEmoteId = emotes.ToDictionary(e => e.Id, e => e.SevenTvEmoteId, StringComparer.Ordinal);

        var liveDays = await db.ChannelLiveDays
            .Where(l => l.ChannelId == channel.Id && l.Date >= from && l.Date <= to && l.LiveMinutes > 0)
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
        // (EmoteId, EmoteSetId, Date) INCLUDE (UseCount) serves as an index-only scan; see the
        // index note on GetUsageContextAsync.
        var rows = await db.UsageStats
            .Where(u => emoteIds.Contains(u.EmoteId) && u.EmoteSetId == setId && u.Date >= from && u.Date <= to && u.UseCount > 0)
            .OrderBy(u => u.EmoteId).ThenBy(u => u.Date)
            .Select(u => new { u.EmoteId, u.Date, u.UseCount })
            .ToListAsync(cancellationToken);

        // Named by the 7TV id from #200 on, with the guid carried along for one transition (spec
        // 6.5, step 1) — an entry only exists where a row exists, so the guid is never empty here.
        var entries = rows
            .GroupBy(r => r.EmoteId)
            .Select(g => new EmoteSeriesEntryDto(
                sevenTvEmoteIdByEmoteId[g.Key],
                g.Key,
                g.Select(r => new[] { r.Date.DayNumber - from.DayNumber, r.UseCount }).ToList()))
            .ToList();

        return new ChannelUsageSeriesDto(from, to, liveDayOffsets, entries);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetTotalsByEmoteIdsAsync(
        IReadOnlyCollection<string> emoteIds, DateOnly from, DateOnly to, string emoteSetId, CancellationToken cancellationToken = default)
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

        // The date range is deliberately NOT a WHERE-level filter (unlike this method's own shape
        // before spec section 9's set-session eligible/useCount split, T6.3 fix round 1) — a row
        // that exists under this set but falls outside [from, to] must still make its id present
        // here, with a possibly-zero sum, so the interface doc's "missing = never observed under
        // this set at all" distinction actually holds. Same conditional-sum shape
        // GetUsageContextAsync's own aggregates query already uses for the identical reason.
        return await db.UsageStats
            .Where(u => ids.Contains(u.EmoteId) && u.EmoteSetId == emoteSetId)
            .GroupBy(u => u.EmoteId)
            .Select(g => new { EmoteId = g.Key, TotalUseCount = g.Sum(u => u.Date >= from && u.Date <= to ? u.UseCount : 0) })
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

        // Rule 10: a plain Where over UsageStats with a scalar id list and the date range, then a
        // GroupBy over that same single table — no navigation join anywhere near it. Materialized
        // to a plain list first for the same reason GetTotalsByEmoteIdsAsync does: Contains against
        // the caller's own collection type can fail to translate. UseCount > 0 is deliberately
        // absent — see the interface doc comment, a bot-only row is exactly what the harness's
        // bot-inclusive total needs.
        // Summed across emote sets (spec E15): since #200 a day can carry two rows for one emote,
        // one per set the cache observed that day, and the harness compares against the chat log,
        // which knows nothing of sets. Handing it two rows for one day would make its per-day
        // comparison wrong in a way that looks like a counting bug; the sum is the number it has
        // always been reading. No set filter, deliberately — set-agnostic is the point.
        var ids = emoteIds.ToList();

        return await db.UsageStats
            .AsNoTracking()
            .Where(u => ids.Contains(u.EmoteId) && u.Date >= from && u.Date <= to)
            .GroupBy(u => new { u.EmoteId, u.Date })
            .OrderBy(g => g.Key.EmoteId).ThenBy(g => g.Key.Date)
            .Select(g => new UsageStatRowDto(
                g.Key.EmoteId,
                g.Key.Date,
                g.Sum(u => u.UseCount),
                g.Sum(u => u.BotUseCount),
                g.Sum(u => u.SharedChatUseCount)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// For each emote of the channel that shares its name with a different 7TV emote of the same
    /// channel: the set ids under which one of those namesakes was actually counted, minus the set
    /// being looked at. Returns nothing for emotes with a unique name, so callers treat a missing
    /// key as "no twins" (spec E24).
    /// </summary>
    private async Task<Dictionary<string, IReadOnlyList<string>>> LoadNameTwinSetIdsAsync(
        IEnumerable<(string Id, string Name, string SevenTvEmoteId)> channelEmotes,
        string emoteSetId,
        CancellationToken cancellationToken)
    {
        // Ordinal throughout, because that is how chat matching decides what a message meant
        // (EmoteNameMatching) — a twin found by a looser comparison would point at a set whose
        // counts a chat message could never have landed in.
        var twinGroups = channelEmotes
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .Where(g => g.Select(e => e.SevenTvEmoteId).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToList();

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (twinGroups.Count == 0)
        {
            return result;
        }

        // Rule 10 again, and the reason this is a second query rather than a join: the ids go down
        // as a plain scalar list, so the distinct pass runs over UsageStats alone and is served by
        // the (EmoteId, EmoteSetId, Date) index. Deliberately unbounded by date — the question is
        // "was this name ever counted somewhere else", and a range would turn a missing hint into a
        // statement about the range rather than about the history.
        var twinEmoteIds = twinGroups.SelectMany(g => g).Select(e => e.Id).ToList();
        var countedSets = await db.UsageStats
            .Where(u => twinEmoteIds.Contains(u.EmoteId))
            .Select(u => new { u.EmoteId, u.EmoteSetId })
            .Distinct()
            .ToListAsync(cancellationToken);

        var setIdsByEmoteId = countedSets
            .GroupBy(p => p.EmoteId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.EmoteSetId).ToList(), StringComparer.Ordinal);

        foreach (var group in twinGroups)
        {
            foreach (var emote in group)
            {
                var twins = group
                    .Where(other => !string.Equals(other.SevenTvEmoteId, emote.SevenTvEmoteId, StringComparison.Ordinal))
                    .SelectMany(other => setIdsByEmoteId.TryGetValue(other.Id, out var sets) ? sets : Enumerable.Empty<string>())
                    .Where(id => !string.Equals(id, emoteSetId, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();

                if (twins.Count > 0)
                {
                    result[emote.Id] = twins;
                }
            }
        }

        return result;
    }
}
