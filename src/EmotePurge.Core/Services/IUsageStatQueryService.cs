namespace EmotePurge.Core.Services;

public record EmoteUsageDto(string EmoteName, DateOnly Date, int UseCount);

/// <summary>
/// One emote with everything needed to judge it as a deletion candidate.
/// </summary>
/// <param name="TotalUseCount">
/// Sum of <c>UseCount</c> over the requested range and the requested emote set — this channel's
/// own human usage, and nothing else. Messages from known bots (<c>BotUseCount</c>) and messages
/// mirrored in from another channel's shared chat (<c>SharedChatUseCount</c>) are both outside it,
/// and so are counts this emote earned under a different set (#200).
/// </param>
/// <param name="LastUsedDate">
/// The last day this emote was used under the requested emote set — deliberately <em>not</em>
/// bounded by the requested range, though it is bounded by the set, so that it cannot contradict
/// the total standing next to it. Range-bounded it would collapse into the total ("0 uses in the
/// range" already says that) and
/// switching the range to 7 days would report almost the whole set as never used. <c>null</c> means
/// never used since tracking began: the flush only ever writes rows for days with actual usage, so
/// an absent maximum is the honest answer rather than a missing one. A row with
/// <c>UseCount = 0</c> does not count as "used" for this field either — the flush can write such a
/// row for a day that saw only bot messages or only mirrored shared-chat messages, and neither is a
/// day this channel used the emote.
/// </param>
/// <param name="PreviousWindowUseCount">
/// Sum over the equally long window immediately preceding the requested range (<c>from</c>
/// exclusive), the same own-usage total as
/// <see cref="TotalUseCount"/>. Deliberately a raw number: whether that reads as rising, stable or
/// falling is a wording decision, and one the caller has to be able to suppress when the history is
/// too short to support it.
/// </param>
/// <param name="FirstSeenAt">
/// When the emote entered the 7TV set. <c>null</c> means unknown — never "new".
/// </param>
/// <param name="IsArchived">
/// Whether this emote row is archived (gone from 7TV). Always <c>false</c> in the active set's
/// view, which excludes archived emotes outright. Only a non-active set can carry <c>true</c>
/// here: there the base set is "every row of the channel with at least one count under that set",
/// and an emote that has since left 7TV still carries the counts it earned while it was in the set
/// (#200, spec E23 — the UI badges such a row and keeps it out of the deletion selection).
/// </param>
/// <param name="NameTwinEmoteSetIds">
/// Emote set ids under which a <em>different</em> <c>SevenTvEmoteId</c> of the same channel bearing
/// the same <c>Emote.Name</c> carries at least one <c>UsageStat</c> row. Ascending ordinal, and
/// empty in the ordinary case — a name unique within the channel, or twins that were never counted
/// anywhere else. The set the caller asked about is never among them: a twin counted <em>there</em>
/// stands in the same view already and needs no pointer (spec E24). This is a hint about where a
/// same-named emote's history went, not a claim that the two are the same emote — chat matching
/// decides by name, so a name shared across sets is exactly the case where the counts split.
/// </param>
public record EmoteUsageContextDto(
    string EmoteId,
    string EmoteName,
    string SevenTvEmoteId,
    string ImageUrl,
    int TotalUseCount,
    DateOnly? LastUsedDate,
    int PreviousWindowUseCount,
    DateTime? FirstSeenAt,
    bool IsArchived,
    IReadOnlyList<string> NameTwinEmoteSetIds);

public record EmoteDailyUsageDto(DateOnly Date, int UseCount);

/// <summary>
/// One emote's day-by-day usage series for the drilldown (idea A5).
/// </summary>
/// <param name="Days">
/// Only days with actual usage, ascending, each day's <c>UseCount</c> this channel's own human
/// usage — see <see cref="EmoteUsageContextDto.TotalUseCount"/>.
/// A missing day inside [From, To] means 0 — the flush only ever writes rows for days with real
/// usage, and inventing zero rows server-side just to transport them would be the expensive way to
/// say nothing; the client zero-fills for rendering. A day whose row has <c>UseCount = 0</c>
/// (only bot or only mirrored shared-chat messages) is missing here too, for the same reason.
/// </param>
/// <param name="TotalUseCount">Sum over [From, To] of the per-day values in <see cref="Days"/>.</param>
/// <param name="FirstUsedDate">
/// First use under the requested emote set, deliberately not bounded by the range — same reasoning
/// and same set bound as <see cref="EmoteUsageContextDto.LastUsedDate"/>. <c>null</c> = never used
/// under that set since tracking began, and a <c>UseCount = 0</c> row does not count as a use.
/// </param>
/// <param name="LastUsedDate">
/// Last use under the same set, equally unbounded in time and equally blind to rows without own
/// usage.
/// </param>
/// <param name="LiveDays">
/// Days within [From, To] on which the channel was live (any coverage at all), ascending — so the
/// chart can tell "offline day" apart from "dead emote" (idea A10). Coverage data only exists
/// since the worker's live poll shipped: an absent day before that means "unknown", not
/// "offline", and the consumer must render it unmarked rather than as a statement.
/// </param>
public record EmoteUsageSeriesDto(
    string EmoteId,
    string EmoteName,
    DateOnly From,
    DateOnly To,
    int TotalUseCount,
    DateOnly? FirstUsedDate,
    DateOnly? LastUsedDate,
    IReadOnlyList<EmoteDailyUsageDto> Days,
    IReadOnlyList<DateOnly> LiveDays);

/// <summary>
/// One emote's day rows inside <see cref="ChannelUsageSeriesDto"/>.
/// </summary>
/// <param name="Days">
/// One <c>[dayOffset, useCount]</c> pair per day with actual usage, ascending, where the offset
/// counts days from the range's <c>From</c> and <c>useCount</c> is the day's <c>UseCount</c> — see
/// <see cref="EmoteUsageContextDto.TotalUseCount"/>.
/// Sparse for the same reason the single-emote series is sparse, and offset-encoded rather than
/// ISO-dated because this is the batch: a channel-wide response carries thousands of these, an ISO
/// date costs about five times what an offset does, and nothing between the Api and the browser
/// compresses JSON (the reverse proxy's gzip covers text/html only). Pairs rather than two parallel
/// arrays so the two halves cannot desynchronize.
/// </param>
/// <param name="SevenTvEmoteId">
/// The 7TV id of the emote, and from #200 on the entry's identity: a set view unions our own rows
/// with 7TV's live membership list, and the 7TV id is the only key both sides share.
/// </param>
/// <param name="EmoteId">
/// The <c>Emote.Id</c> guid of the row these days came from — carried alongside
/// <paramref name="SevenTvEmoteId"/> for one transition only (spec 6.5, step 1). It is never empty
/// here, because a series entry can only exist where a database row exists, and it will be dropped
/// in a step of its own once no client reads it any more.
/// </param>
public record EmoteSeriesEntryDto(string SevenTvEmoteId, string EmoteId, IReadOnlyList<int[]> Days);

/// <summary>
/// Every active emote's daily usage for one channel and range in a single response — the batch twin
/// of <see cref="EmoteUsageSeriesDto"/>.
/// </summary>
/// <remarks>
/// Exists because the consumer is a hover readout over a sheet of up to a thousand emotes. Asking
/// <see cref="IUsageStatQueryService.GetDailySeriesAsync"/> per emote would turn pointer movement
/// into requests against the endpoint group's rate limiter, and every one of those requests pays
/// for the access filter's two uncached 7TV lookups. One call per (channel, range) removes the
/// question instead of budgeting for it.
/// </remarks>
/// <param name="LiveDays">
/// Day offsets from <paramref name="From"/> on which the channel was live — channel-level, so
/// carried once here rather than repeated per emote as the single-emote series has to. Same
/// "absent means unknown, not offline" caveat as <see cref="EmoteUsageSeriesDto.LiveDays"/>.
/// </param>
/// <param name="Emotes">
/// Only emotes with at least one day of own usage in the range (rows with <c>UseCount = 0</c>, be
/// they bot-only or shared-chat-only, do not count) under the requested emote set. Unarchived only
/// while the requested set is the channel's active one; a non-active set reports its archived rows
/// too, for the reason given on <see cref="EmoteUsageContextDto.IsArchived"/>. An emote the caller
/// knows about but does not find here has no own usage in the window — the same statement the
/// omitted days inside an entry make, one level up.
/// </param>
public record ChannelUsageSeriesDto(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<int> LiveDays,
    IReadOnlyList<EmoteSeriesEntryDto> Emotes);

/// <summary>
/// One emote's lifetime bounds for the chat-log backfill harness (issue #69). Not a usage context —
/// it carries no counts, only what "did this emote exist on day X" needs to judge a historical
/// UsageStat row against.
/// </summary>
/// <param name="IsArchived">Whether the emote is currently archived (gone from 7TV).</param>
/// <param name="FirstSeenAt">
/// Same "null means unknown, never guessed" convention as <see cref="EmoteUsageContextDto.FirstSeenAt"/>.
/// </param>
/// <param name="ArchivedAt">
/// When the emote was (last) archived. Null on an archived emote means "archived before this
/// column existed, date unknown" — see the field's own comment on <c>Emote</c>.
/// </param>
/// <param name="LastSyncedAt">
/// When the emote row was last touched by a sync (rename, restore, dispatch-REMOVE) — not stamped
/// by REST-reconcile archiving, so it alone cannot prove "stable since". The harness combines it
/// with <paramref name="ArchivedAt"/> to build its own stable-subset rule; this method does not
/// judge that itself.
/// </param>
public record EmoteLifetimeDto(string Id, string Name, bool IsArchived, DateTime? FirstSeenAt, DateTime? ArchivedAt, DateTime LastSyncedAt);

/// <summary>
/// One <c>UsageStat</c> row per emote and day for the chat-log backfill harness (issue #69),
/// unfiltered by count. Deliberately carries no emote set id: the harness compares its own chat
/// count for a day against what we recorded that day, and a set switch splits our record in two
/// without splitting the chat. The three counts are therefore summed across set ids (spec E15), and
/// the shape stays what the harness has always read — one row per <c>(EmoteId, Date)</c>.
/// </summary>
public record UsageStatRowDto(string EmoteId, DateOnly Date, int UseCount, int BotUseCount, int SharedChatUseCount);

public interface IUsageStatQueryService
{
    Task<IReadOnlyList<EmoteUsageDto>> GetUsageStatsAsync(string channelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every active emote of the channel with its usage context, zero-filled — an unused emote must
    /// still be findable in a usage UI. Archived emotes are excluded: they are already gone from
    /// 7TV and must not reappear as deletion candidates just because they still carry history.
    /// </summary>
    /// <param name="emoteSetId">
    /// Which emote set's counts to report. <c>null</c> means the channel's currently active set, so
    /// an unchanged caller keeps the behaviour it had before #200. Asking for a <em>non-active</em>
    /// set changes the base set as well as the filter: instead of the channel's unarchived emotes
    /// zero-filled, it is every row of the channel — archived ones included — with at least one
    /// count under that set (spec E16). A set the channel never used answers empty.
    /// </param>
    Task<IReadOnlyList<EmoteUsageContextDto>> GetUsageContextAsync(
        string channelName, DateOnly from, DateOnly to, string? emoteSetId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The daily usage series of a single emote, resolved against the channel — <c>null</c> when
    /// the emote id is unknown or belongs to a different channel (the caller answers 404). Archived
    /// emotes deliberately keep their series: they are unreachable from the usage grid, but a
    /// subset vote session still lists them as ballot members, and their history is real.
    /// </summary>
    /// <param name="emoteSetId">
    /// Which emote set's counts the series reports; <c>null</c> is the channel's active set. The
    /// emote is still addressed by its <c>Emote.Id</c> guid — the drilldown opens from a row the
    /// caller already holds, so nothing is gained by making it name the 7TV id instead.
    /// </param>
    Task<EmoteUsageSeriesDto?> GetDailySeriesAsync(
        string channelName, string emoteId, DateOnly from, DateOnly to, string? emoteSetId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every unarchived emote's daily usage for the channel and range at once. An unknown channel
    /// answers with empty lists rather than <c>null</c>: unlike the single-emote series there is no
    /// id to get wrong, and the caller's access filter has already decided whether the channel may
    /// be looked at.
    /// </summary>
    /// <param name="emoteSetId">
    /// Which emote set's counts to report; <c>null</c> is the channel's active set. As in
    /// <see cref="GetUsageContextAsync"/>, a non-active set widens the base set to the channel's
    /// archived rows as well — the sheet under a non-active set shows what that set was used for,
    /// and half of that can be gone from 7TV by now.
    /// </param>
    Task<ChannelUsageSeriesDto> GetChannelSeriesAsync(
        string channelName, DateOnly from, DateOnly to, string? emoteSetId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Range totals for a known set of emote ids, keyed by id. An id is present with its
    /// range-summed <c>UseCount</c> whenever it has at least one <c>UsageStat</c> row under
    /// <paramref name="emoteSetId"/> at all, in any date — the sum itself can legitimately be
    /// <c>0</c> two different ways: every such row falls outside <paramref name="from"/>–
    /// <paramref name="to"/>, or a row does fall inside the range but carries
    /// <c>UseCount = 0</c> (a bot-only or shared-chat-only day). A <c>WHERE</c>-level date filter
    /// would have made the first of those cases indistinguishable from "never observed under this
    /// set", which is exactly the distinction
    /// <see cref="EmotePurge.Core.Services.IVoteSessionQueryService.GetResultsAsync"/> needs for a
    /// set-session's <c>eligible</c> ballot (spec section 9, AK 80): an id missing here means the
    /// caller may report <c>null</c> ("never counted under this set"), never a fabricated <c>0</c>.
    /// Scoped to the caller's ids rather than to a whole channel, because the one caller (a vote
    /// session's ballot) may hold twenty emotes out of a thousand. Each total is <c>UseCount</c>
    /// alone — see <see cref="EmoteUsageContextDto.TotalUseCount"/>.
    /// </summary>
    /// <param name="emoteSetId">
    /// Which emote set's counts to sum. Required rather than nullable, unlike the channel-scoped
    /// reads: this method takes ids, not a channel, so it has nothing to resolve a "the active one"
    /// default against — the caller holds the session and decides which set its ballot is about.
    /// </param>
    Task<IReadOnlyDictionary<string, int>> GetTotalsByEmoteIdsAsync(
        IReadOnlyCollection<string> emoteIds, DateOnly from, DateOnly to, string emoteSetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The earliest day across all of the channel's emotes — including archived ones — with a
    /// <c>UsageStat</c> row that has <c>BotUseCount &gt; 0</c>, or <c>null</c> if no bot has ever
    /// been seen here. See <see cref="EmoteSetStatusDto.BotsExcludedSince"/> for what "seen" means
    /// here (first sighting, not the deploy day the separation itself started). Consumed by
    /// <c>EmoteSetStatusService</c> and by the chat-log backfill harness (issue #69), which both
    /// need the same cutover day rather than two copies of this rule.
    /// </summary>
    Task<DateOnly?> GetEarliestBotUsageDateAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The earliest day across all of the channel's emotes — including archived ones — with a
    /// <c>UsageStat</c> row that has <c>SharedChatUseCount &gt; 0</c>, or <c>null</c> if no mirrored
    /// shared-chat message has ever been seen here. The twin of
    /// <see cref="GetEarliestBotUsageDateAsync"/>, deliberately a separate method rather than a
    /// parameterized generalization of it: that would make the frozen bot method touchable for the
    /// sake of two callers. See <see cref="EmoteSetStatusDto.SharedChatSeparatedSince"/> for what
    /// "seen" means (first sighting, not the deploy day the separation itself started).
    /// </summary>
    Task<DateOnly?> GetEarliestSharedChatUsageDateAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every emote of the channel — including archived ones — sorted by <see cref="EmoteLifetimeDto.Id"/>
    /// (ordinal). Consumed by the chat-log backfill harness (issue #69), which needs a deterministic
    /// order to hash the returned list as part of its input fingerprint (Task 6). Unlike
    /// <see cref="GetUsageContextAsync"/>, archived emotes are deliberately not excluded: the
    /// harness needs their lifetime bounds to judge historical usage, not to offer them up as
    /// deletion candidates.
    /// </summary>
    Task<IReadOnlyList<EmoteLifetimeDto>> GetEmoteLifetimesAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One row per emote and day for the given emote ids within an inclusive date range, sorted by
    /// <c>(EmoteId, Date)</c>, with all three counts summed over the emote sets that day was counted
    /// under (spec E15). Consumed by the chat-log backfill harness (issue #69) to compute both
    /// its human-only and its bot-inclusive total for the window. Unlike every other query in this
    /// interface, this deliberately does not filter on <c>UseCount &gt; 0</c>: a bot-only row
    /// (<c>UseCount = 0</c>, <c>BotUseCount &gt; 0</c>) is exactly what the harness's bot-inclusive
    /// total needs and the human-only total is expected to exclude on its own — and the same holds
    /// for <c>SharedChatUseCount</c>, needed raw for the harness's shared-chat total (#73).
    /// </summary>
    Task<IReadOnlyList<UsageStatRowDto>> GetRowsAsync(
        IReadOnlyCollection<string> emoteIds, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
