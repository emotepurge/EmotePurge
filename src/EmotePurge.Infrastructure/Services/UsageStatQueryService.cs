using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class UsageStatQueryService(AppDbContext db, SlotReconcileOptions? slotReconcile = null) : IUsageStatQueryService
{
    private const string FromMustPrecedeToMessage = "'from' must be less than or equal to 'to'.";

    // Null (the unit-test constructor) is off, like the flag's default. It decides nothing but the mode of
    // each usage-grid row (plan #376 R1): off, every row is in emote mode — the reading from before slots.
    private readonly bool slotReconcileEnabled = slotReconcile?.Enabled ?? false;

    public async Task<IReadOnlyList<EmoteUsageDto>> GetUsageStatsAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        // UseCount alone, like every product read path in this class: neither bot nor shared-chat
        // usage is this channel's own human usage. A debug raw list, so it never carried the D5
        // transitional sum to begin with and needed nothing reverted when that bridge fell. Raw by
        // design: one entry per row, so since M2 (slot plan #373) the same name and day can appear
        // twice — once per slot of the emote, or once per set — and nothing here sums them.
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

        // Every emote of the channel, archived ones included — the base set is narrowed by the row
        // formation rather than here, because the two views need different halves of this list: the active
        // set's view excludes archived emotes (they are gone from 7TV and must not reappear as delete
        // candidates just because they still carry history), while a non-active set's view is a historical
        // one and keeps them (spec E16/E23). The name-twin lookup needs the whole list either way — an
        // emote's twin under another set is usually precisely an archived row.
        var allChannelEmotes = await LoadChannelEmotesAsync(channel.Id, cancellationToken);
        if (allChannelEmotes.Count == 0)
        {
            return [];
        }

        var view = await LoadUsageViewAsync(channel.Id, setId, isActiveSet, allChannelEmotes, cancellationToken);

        // Both range bounds are inclusive, so the window is one day longer than the difference —
        // and the preceding window has to be exactly as long for the two sums to be comparable.
        var windowLength = to.DayNumber - from.DayNumber + 1;
        var previousFrom = from.AddDays(-windowLength);

        // One pass, three aggregates per slot (plan #376 2.2), all three over UseCount alone — the channel's
        // own human usage, which is what this grid judges deletion candidates by. Both side columns are
        // outside that: a bot-only row (UseCount = 0, BotUseCount > 0) and a shared-only row (UseCount = 0,
        // SharedChatUseCount > 0, usage mirrored in from another channel's shared chat) read the same way
        // here, namely as no use at all. Rule 10: the slot ids go down as a plain scalar list and the
        // grouped query stays on UsageStats alone; reading UseCount alone and filtering on the set id rather
        // than on anything outside the row is what keeps the unique slot index (SlotId, EmoteSetId, Date)
        // INCLUDE (UseCount) serving this as an index-only scan. Only the slots some row carries are asked
        // for. LastUsedDate is deliberately unbounded in time: the max is the last use ever, and clipping it
        // to the range would make it a restatement of the total. The two sums need no date-independent
        // predicate — a row without own usage contributes 0 to a sum on its own. LastUsedDate does need one:
        // a row's mere existence is no proof of use once the flush can write UseCount = 0 rows, so neither a
        // bot-only nor a shared-only day may read as "last used".
        var aggregateSlotIds = AggregateSlotIdsOf(view.Rows);
        var aggregates = aggregateSlotIds.Count == 0
            ? new Dictionary<string, SlotAggregate>(StringComparer.Ordinal)
            : await db.UsageStats
                .Where(u => aggregateSlotIds.Contains(u.SlotId) && u.EmoteSetId == setId)
                .GroupBy(u => u.SlotId)
                .Select(g => new
                {
                    SlotId = g.Key,
                    TotalUseCount = g.Sum(u => u.Date >= from && u.Date <= to ? u.UseCount : 0),
                    PreviousWindowUseCount = g.Sum(u => u.Date >= previousFrom && u.Date < from ? u.UseCount : 0),
                    LastUsedDate = g.Max(u => u.UseCount > 0 ? (DateOnly?)u.Date : null)
                })
                .ToDictionaryAsync(
                    g => g.SlotId,
                    g => new SlotAggregate(g.TotalUseCount, g.PreviousWindowUseCount, g.LastUsedDate),
                    StringComparer.Ordinal,
                    cancellationToken);

        var nameTwinSetIds = await LoadNameTwinSetIdsAsync(allChannelEmotes, view, setId, cancellationToken);
        var emotesById = allChannelEmotes.ToDictionary(e => e.Id, StringComparer.Ordinal);

        // The base order is the emote list as the database returned it, the rows of one emote in the order of
        // their slots; the sort by total is stable on top of it. With the flag off that is one row per emote
        // in exactly the order the pre-slot code sorted — ties included, which no contract promises but a
        // prod reading should not shuffle for nothing (R1).
        var emotePosition = allChannelEmotes
            .Select((e, index) => (e.Id, index))
            .ToDictionary(p => p.Id, p => p.index, StringComparer.Ordinal);

        return view.Rows
            .Select((row, index) => (Row: row, Index: index))
            .OrderBy(r => emotePosition[r.Row.EmoteId])
            .ThenBy(r => r.Index)
            .Select(r =>
            {
                var row = r.Row;
                var emote = emotesById[row.EmoteId];

                // An emote-mode row folds the aggregates of all its slots (the emote's whole count under the
                // set, the pre-slot number); a slot-mode row carries exactly its one slot (R2 invariant).
                var total = 0;
                var previous = 0;
                DateOnly? lastUsed = null;
                foreach (var slotId in row.AggregateSlotIds)
                {
                    if (aggregates.TryGetValue(slotId, out var aggregate))
                    {
                        total += aggregate.TotalUseCount;
                        previous += aggregate.PreviousWindowUseCount;
                        if (aggregate.LastUsedDate is { } used && (lastUsed is null || used > lastUsed))
                        {
                            lastUsed = used;
                        }
                    }
                }

                return new EmoteUsageContextDto(
                    row.EmoteId,
                    row.SlotId,
                    row.Name,
                    emote.SevenTvEmoteId,
                    emote.ImageUrl,
                    total,
                    lastUsed,
                    previous,
                    row.FirstSeenAt,
                    row.FirstSeenAtFromRest,
                    row.IsArchived,
                    nameTwinSetIds[r.Index],
                    row.SiblingNames);
            })
            .OrderByDescending(t => t.TotalUseCount)
            .ToList();
    }

    public async Task<EmoteUsageSeriesDto?> GetDailySeriesAsync(
        string channelName, string emoteId, DateOnly from, DateOnly to, EmoteSetScope setScope = default, CancellationToken cancellationToken = default)
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

        return await BuildDailySeriesAsync(
            db.UsageStats.Where(u => u.EmoteId == emote.Id),
            emote.Id,
            emote.Name,
            slotId: null,
            emote.ChannelId,
            emote.ActiveEmoteSetId,
            from,
            to,
            setScope,
            cancellationToken);
    }

    public async Task<EmoteUsageSeriesDto?> GetDailySlotSeriesAsync(
        string channelName, string slotId, DateOnly from, DateOnly to, EmoteSetScope setScope = default, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException(FromMustPrecedeToMessage, nameof(from));
        }

        var normalized = ChannelName.Normalize(channelName);

        // Plan 376 2.5: slot -> emote -> channel, resolved against the channel of the route exactly like the
        // emote lookup above, so a slot id of another channel is as unknown as a made-up one. Archived slots and
        // unsettled seeds are allowed (ballot and history cases). A seed shows the emote's name (R2: its own name
        // can be stale); a settled slot shows its own.
        var slot = await db.EmoteSlots
            .Where(s => s.Id == slotId && s.Emote.Channel.ChannelName == normalized)
            .Select(s => new
            {
                s.Id,
                s.EmoteId,
                SlotName = s.Name,
                s.SeedUnsettled,
                EmoteName = s.Emote.Name,
                s.Emote.ChannelId,
                s.Emote.Channel.ActiveEmoteSetId
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (slot is null)
        {
            return null;
        }

        return await BuildDailySeriesAsync(
            db.UsageStats.Where(u => u.SlotId == slot.Id),
            slot.EmoteId,
            slot.SeedUnsettled ? slot.EmoteName : slot.SlotName,
            slot.Id,
            slot.ChannelId,
            slot.ActiveEmoteSetId,
            from,
            to,
            setScope,
            cancellationToken);
    }

    private async Task<EmoteUsageSeriesDto> BuildDailySeriesAsync(
        IQueryable<UsageStat> rows,
        string emoteId,
        string name,
        string? slotId,
        string channelId,
        string? activeEmoteSetId,
        DateOnly from,
        DateOnly to,
        EmoteSetScope setScope,
        CancellationToken cancellationToken)
    {
        // AllSets drops the set predicate from both queries below: a null-session's row is
        // set-agnostic, so the drilldown opened from it has to be too.
        var allSets = setScope.IsAllSets;
        var setId = setScope.SetId ?? activeEmoteSetId;

        // Sparse on purpose (only days with usage). Value and predicate both run over UseCount
        // alone, same reasoning as GetUsageContextAsync — a bot-only and a shared-only day are
        // equally absent from this series, because neither is a day this channel used the emote.
        // The set filter is what makes the drilldown agree with the row it was opened from: the
        // grid's total is one set's counts, so the days behind it have to be the same set's.
        // Grouped by day: the same day can carry one row per set and, since M2 (slot plan #373), one
        // per slot of the emote, and the DTO promises one entry per day. Single-table GroupBy, so
        // rule 10 is untouched.
        var days = await rows
            .Where(u => (allSets || u.EmoteSetId == setId) && u.Date >= from && u.Date <= to && u.UseCount > 0)
            .GroupBy(u => u.Date)
            .Select(g => new { Date = g.Key, UseCount = g.Sum(u => u.UseCount) })
            .OrderBy(d => d.Date)
            .ToListAsync(cancellationToken);
        var dailyUsage = days.Select(d => new EmoteDailyUsageDto(d.Date, d.UseCount)).ToList();

        // First/last use ever, unbounded in time — same reasoning as LastUsedDate in
        // GetUsageContextAsync, including the UseCount > 0 predicate against rows with no own
        // usage. Bounded by the set, though, and deliberately so: "first used" next to a set's
        // numbers must mean first used under that set (under AllSets: first used in the channel). Single-table GroupBy, so rule 10 is not
        // even touched.
        var bounds = await rows
            .Where(u => (allSets || u.EmoteSetId == setId) && u.UseCount > 0)
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
        // Narrowed to the days the viewed set has counts on (the same set predicate as the emote's
        // own days above, none under AllSets): the live-day rule, shared with the channel series.
        // The denominator stays set-wide — it is NOT the emote's own days, or every emote would
        // read "used on all live days".
        var countedDates = await LoadSetCountedDatesAsync(channelId, allSets ? null : setId, from, to, cancellationToken);
        var liveDays = await LoadQualifyingLiveDaysAsync(channelId, countedDates, from, to, cancellationToken);

        return new EmoteUsageSeriesDto(
            emoteId,
            name,
            from,
            to,
            dailyUsage.Sum(d => d.UseCount),
            bounds?.First,
            bounds?.Last,
            dailyUsage,
            liveDays,
            slotId);
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

        // The same rows as GetUsageContextAsync (plan #376 2.4), built from the same snapshot by the same row
        // formation, so a card and its curve can never disagree: the active set's sheet excludes archived
        // emotes because it is a deletion grid, a non-active set's sheet keeps them because it is a
        // historical view.
        var emotes = await LoadChannelEmotesAsync(channel.Id, cancellationToken);

        // Loaded from the database for ALL emotes of the channel, archived included, and never
        // derived from the rows below: those exclude archived emotes for the active set, so
        // archiving an emote would retroactively change historical live days and the bounds.
        // One query for every set at once: the viewed set's counted days and the channel-wide first
        // counted day (any set) both fall out of the same (Date, EmoteSetId) pairs in memory.
        var pairs = await LoadChannelCountedPairsAsync(channel.Id, from, to, cancellationToken);
        var countedDates = pairs
            .Where(p => string.Equals(p.EmoteSetId, setId, StringComparison.Ordinal))
            .Select(p => p.Date)
            .Distinct()
            .Order()
            .ToList();
        DateOnly? channelFirstCountedDay = pairs.Count > 0 ? pairs.Min(p => p.Date) : null;
        var liveDays = await LoadQualifyingLiveDaysAsync(channel.Id, countedDates, from, to, cancellationToken);
        var liveDayOffsets = liveDays.Select(d => d.DayNumber - from.DayNumber).ToList();
        DateOnly? firstCountedDay = countedDates.Count > 0 ? countedDates[0] : null;
        DateOnly? lastCountedDay = countedDates.Count > 0 ? countedDates[^1] : null;

        if (emotes.Count == 0)
        {
            return new ChannelUsageSeriesDto(from, to, liveDayOffsets, [], firstCountedDay, lastCountedDay, channelFirstCountedDay);
        }

        var view = await LoadUsageViewAsync(channel.Id, setId, isActiveSet, emotes, cancellationToken);
        var aggregateSlotIds = AggregateSlotIdsOf(view.Rows);
        if (aggregateSlotIds.Count == 0)
        {
            return new ChannelUsageSeriesDto(from, to, liveDayOffsets, [], firstCountedDay, lastCountedDay, channelFirstCountedDay);
        }

        // One scan over the slots the rows carry, then partitioned in memory. Deliberately not a GroupBy in
        // SQL: per slot and day the slot index already makes a row unique, so there is nothing to push down
        // for a slot-mode row, and an emote-mode row's per-day sum over its slots is the same partitioning
        // done one level up. Value and predicate run over UseCount alone: a row whose only days carry no own
        // usage — bot-only or shared-only — drops out of Emotes entirely, the same way a row with no days at
        // all does. Rule 10 again (a scalar slot-id list, UsageStats alone), and reading UseCount alone is
        // what the unique slot index (SlotId, EmoteSetId, Date) INCLUDE (UseCount) serves as an index-only
        // scan.
        var days = await db.UsageStats
            .Where(u => aggregateSlotIds.Contains(u.SlotId) && u.EmoteSetId == setId && u.Date >= from && u.Date <= to && u.UseCount > 0)
            .Select(u => new { u.SlotId, u.Date, u.UseCount })
            .ToListAsync(cancellationToken);
        var daysBySlot = days.ToLookup(d => d.SlotId, StringComparer.Ordinal);
        var sevenTvEmoteIdByEmoteId = emotes.ToDictionary(e => e.Id, e => e.SevenTvEmoteId, StringComparer.Ordinal);

        // Named by the 7TV id from #200 on, with the guid carried along for one transition (spec 6.5, step 1)
        // — an entry only exists where a row exists, so the guid is never empty here. One entry per grid row in
        // the row formation's order (by EmoteId, within one emote by slot); a slot-mode entry is the days of
        // its one slot, an emote-mode entry sums each day over the emote's slots, as the series did per emote
        // between M2 (#373) and this child. A row without a day of own usage in the range has no entry.
        var entries = new List<EmoteSeriesEntryDto>();
        foreach (var row in view.Rows)
        {
            var rowDays = row.AggregateSlotIds
                .SelectMany(slotId => daysBySlot[slotId])
                .GroupBy(d => d.Date)
                .OrderBy(day => day.Key)
                .Select(day => new[] { day.Key.DayNumber - from.DayNumber, day.Sum(d => d.UseCount) })
                .ToList();
            if (rowDays.Count > 0)
            {
                entries.Add(new EmoteSeriesEntryDto(sevenTvEmoteIdByEmoteId[row.EmoteId], row.EmoteId, row.SlotId, row.Name, rowDays));
            }
        }

        return new ChannelUsageSeriesDto(from, to, liveDayOffsets, entries, firstCountedDay, lastCountedDay, channelFirstCountedDay);
    }

    /// <summary>
    /// The distinct days in [from, to] on which at least one emote of the channel — archived ones
    /// included — has a row with own usage (<c>UseCount &gt; 0</c>) under the given set; every set
    /// when <paramref name="setId"/> is null. Ascending. This is what makes a live day count for a
    /// set: a day the set shows no evidence of is not one of its live days.
    /// </summary>
    private async Task<List<DateOnly>> LoadSetCountedDatesAsync(
        string channelId, string? setId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var query = db.UsageStats
            .Where(u => db.Emotes.Where(e => e.ChannelId == channelId).Select(e => e.Id).Contains(u.EmoteId)
                && u.Date >= from && u.Date <= to && u.UseCount > 0);

        // Branched rather than "(allSets || set = @p)": a bool parameter in the predicate would keep
        // the set filter in the SQL text and let the planner reuse a plan for the wrong shape.
        if (setId is not null)
        {
            query = query.Where(u => u.EmoteSetId == setId);
        }

        return await query
            .Select(u => u.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The distinct (day, set) pairs in [from, to] with own usage on any emote of the channel,
    /// archived ones included. Rows of emotes of other channels are excluded by the emote subquery,
    /// even when they carry the same set id.
    /// </summary>
    private async Task<List<CountedPair>> LoadChannelCountedPairsAsync(
        string channelId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        return await db.UsageStats
            .Where(u => db.Emotes.Where(e => e.ChannelId == channelId).Select(e => e.Id).Contains(u.EmoteId)
                && u.Date >= from && u.Date <= to && u.UseCount > 0)
            .Select(u => new CountedPair(u.Date, u.EmoteSetId))
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private async Task<List<DateOnly>> LoadQualifyingLiveDaysAsync(
        string channelId, List<DateOnly> countedDates, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (countedDates.Count == 0)
        {
            return [];
        }

        // LiveMinutes > 0 is defensive — the poll never writes a zero row. Served by the covering
        // index (ChannelId, Date) INCLUDE (LiveMinutes).
        var liveDays = await db.ChannelLiveDays
            .Where(l => l.ChannelId == channelId && l.Date >= from && l.Date <= to && l.LiveMinutes > 0)
            .OrderBy(l => l.Date)
            .Select(l => l.Date)
            .ToListAsync(cancellationToken);
        var counted = countedDates.ToHashSet();
        return liveDays.Where(counted.Contains).ToList();
    }

    public async Task<IReadOnlyDictionary<string, int>> GetTotalsByEmoteIdsAsync(
        IReadOnlyCollection<string> emoteIds, DateOnly from, DateOnly to, EmoteSetScope setScope, CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            throw new ArgumentException(FromMustPrecedeToMessage, nameof(from));
        }

        if (setScope.IsActiveSet)
        {
            throw new ArgumentException("The active set cannot be resolved without a channel; pass a set or all sets.", nameof(setScope));
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
        var allSets = setScope.IsAllSets;
        var setId = setScope.SetId;
        return await db.UsageStats
            .Where(u => ids.Contains(u.EmoteId) && (allSets || u.EmoteSetId == setId))
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

        // Live rows only (#349): the date says since when *our counting* separates bots
        // (IEmoteSetStatusService.BotsExcludedSince), and a chat-log import counts bots with
        // today's rules for days we never counted. Letting an imported row move the date back would
        // claim a separation we did not perform; the caption errs toward naming a date rather than
        // reading as clean, so the live start is the honest one.
        return await db.UsageStats
            .Where(u => emoteIds.Contains(u.EmoteId) && u.Source == UsageStatSource.Live && u.BotUseCount > 0)
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

        // Live rows only, like the bot date above and for the same reason (#349).
        return await db.UsageStats
            .Where(u => emoteIds.Contains(u.EmoteId) && u.Source == UsageStatSource.Live && u.SharedChatUseCount > 0)
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
    /// Every emote of the channel, archived ones included, in the order the database returns them — the
    /// input both grid readers hand to the row formation and the base order of the totals' sort.
    /// </summary>
    private async Task<List<ChannelEmote>> LoadChannelEmotesAsync(string channelId, CancellationToken cancellationToken)
    {
        return await db.Emotes
            .Where(e => e.ChannelId == channelId)
            .Select(e => new ChannelEmote(e.Id, e.Name, e.SevenTvEmoteId, e.ImageUrl, e.FirstSeenAt, e.IsArchived))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The rows of the usage grid for one channel and one requested set (plan #376 2.2/2.3): loads the
    /// snapshot <see cref="UsageViewRows.Build"/> reads — every slot of the channel's emotes with whether it
    /// carries a row under the set, and the seeds of the set, both from one query
    /// (<see cref="UsageViewSlotSnapshot.LoadAsync"/>) — and forms the rows. <c>/totals</c> and
    /// <c>/series</c> both come through here, so they read the same snapshot shape.
    /// </summary>
    private async Task<UsageView> LoadUsageViewAsync(
        string channelId, string emoteSetId, bool isActiveSet, List<ChannelEmote> emotes, CancellationToken cancellationToken)
    {
        var snapshot = await UsageViewSlotSnapshot.LoadAsync(db, channelId, emoteSetId, cancellationToken);

        var rows = UsageViewRows.Build(
            emotes.ConvertAll(e => new UsageViewEmote(e.Id, e.Name, e.FirstSeenAt, e.IsArchived)),
            snapshot.Slots,
            snapshot.SeedIdsOfSet,
            snapshot.SlotIdsWithRowsInSet,
            isActiveSet,
            slotReconcileEnabled);
        return new UsageView(rows, snapshot.Slots);
    }

    /// <summary>
    /// For each row of the view, aligned by index: the set ids under which a namesake of the row — a
    /// different 7TV emote of the same channel bearing the row's name — was actually counted, minus the set
    /// being looked at; ascending ordinal, empty for a unique name (spec E24). An emote-mode row compares
    /// <c>Emote.Name</c> with the other emotes' <c>Emote.Name</c>, the pre-slot rule; a slot-mode row
    /// compares its slot name with the other emotes' <c>Emote.Name</c> and their own slot names (plan #376
    /// 2.6). Two slots of one emote are never twins.
    /// </summary>
    private async Task<IReadOnlyList<string>[]> LoadNameTwinSetIdsAsync(
        List<ChannelEmote> channelEmotes,
        UsageView view,
        string emoteSetId,
        CancellationToken cancellationToken)
    {
        // Ordinal throughout, because that is how chat matching decides what a message meant
        // (EmoteNameMatching) — a twin found by a looser comparison would point at a set whose
        // counts a chat message could never have landed in.
        var emoteIdsByEmoteName = channelEmotes
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Id).ToList(), StringComparer.Ordinal);

        // Own slots only (plan 2.1: not an unsettled seed), archived ones included: a seed's name may be stale
        // and is no name the emote is known by.
        var emoteIdsBySlotName = view.Slots
            .Where(s => !s.SeedUnsettled)
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(s => s.EmoteId).ToList(), StringComparer.Ordinal);
        var sevenTvEmoteIdByEmoteId = channelEmotes.ToDictionary(e => e.Id, e => e.SevenTvEmoteId, StringComparer.Ordinal);

        var twinEmoteIdsByRow = new List<string>[view.Rows.Count];
        for (var index = 0; index < view.Rows.Count; index++)
        {
            var row = view.Rows[index];
            var ownSevenTvEmoteId = sevenTvEmoteIdByEmoteId[row.EmoteId];
            IEnumerable<string> bearers = emoteIdsByEmoteName.GetValueOrDefault(row.Name) ?? [];
            if (row.SlotId is not null && emoteIdsBySlotName.TryGetValue(row.Name, out var slotBearers))
            {
                bearers = bearers.Concat(slotBearers);
            }

            // A slot of an emote outside the snapshot's emote list (an insert between the two reads) is no
            // twin anyone can be pointed at; it is skipped, not looked up.
            twinEmoteIdsByRow[index] = bearers
                .Where(id => sevenTvEmoteIdByEmoteId.TryGetValue(id, out var sevenTvEmoteId)
                             && !string.Equals(sevenTvEmoteId, ownSevenTvEmoteId, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var result = new IReadOnlyList<string>[view.Rows.Count];
        Array.Fill(result, []);
        var twinEmoteIds = twinEmoteIdsByRow.SelectMany(ids => ids).Distinct(StringComparer.Ordinal).ToList();
        if (twinEmoteIds.Count == 0)
        {
            return result;
        }

        // Rule 10 again, and the reason this is a second query rather than a join: the ids go down
        // as a plain scalar list, so the distinct pass runs over UsageStats alone and is served by
        // the (EmoteId, EmoteSetId, Date) index. Deliberately unbounded by date — the question is
        // "was this name ever counted somewhere else", and a range would turn a missing hint into a
        // statement about the range rather than about the history. A twin is the other emote as a
        // whole, so every set any of its slots was counted under counts.
        var countedSets = await db.UsageStats
            .Where(u => twinEmoteIds.Contains(u.EmoteId))
            .Select(u => new { u.EmoteId, u.EmoteSetId })
            .Distinct()
            .ToListAsync(cancellationToken);

        var setIdsByEmoteId = countedSets
            .GroupBy(p => p.EmoteId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.EmoteSetId).ToList(), StringComparer.Ordinal);

        for (var index = 0; index < view.Rows.Count; index++)
        {
            result[index] = twinEmoteIdsByRow[index]
                .SelectMany(id => setIdsByEmoteId.TryGetValue(id, out var sets) ? sets : Enumerable.Empty<string>())
                .Where(id => !string.Equals(id, emoteSetId, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }

        return result;
    }

    /// <summary>The distinct slots whose aggregates some row of the view carries.</summary>
    private static List<string> AggregateSlotIdsOf(IReadOnlyList<UsageViewRow> rows) =>
        rows.SelectMany(r => r.AggregateSlotIds).Distinct(StringComparer.Ordinal).ToList();

    private sealed record CountedPair(DateOnly Date, string EmoteSetId);

    private sealed record ChannelEmote(string Id, string Name, string SevenTvEmoteId, string ImageUrl, DateTime? FirstSeenAt, bool IsArchived);

    private sealed record UsageView(IReadOnlyList<UsageViewRow> Rows, IReadOnlyList<UsageViewSlot> Slots);

    private sealed record SlotAggregate(int TotalUseCount, int PreviousWindowUseCount, DateOnly? LastUsedDate);
}
