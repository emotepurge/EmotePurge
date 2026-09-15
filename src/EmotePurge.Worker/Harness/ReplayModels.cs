using System.Collections;
using System.Text.Json.Serialization;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// One emote of the channel as the harness sees it: the three timestamps that decide which day it
/// may be counted on, plus the archive flag.
/// <para>
/// This is deliberately <b>not</b> the DTO that <c>IUsageStatQueryService</c> hands out
/// (<c>EmoteLifetimeDto</c>), even though the two have the same shape. The pure calculation of the
/// harness must not wait for — or be coupled to — the query layer, so the entry point maps between
/// the two. The duplication is a decision, not an oversight: do not "clean it up" by making this
/// an alias.
/// </para>
/// </summary>
public sealed record ReplayEmote(
    string Id,
    string Name,
    bool IsArchived,
    DateTime? FirstSeenAt,
    DateTime? ArchivedAt,
    DateTime LastSyncedAt);

/// <summary>
/// One live <c>UsageStat</c> row of the window. Same shape as <c>UsageStatRowDto</c> and, like
/// <see cref="ReplayEmote"/>, deliberately its own type — see the remark there.
/// </summary>
public sealed record ReplayUsageRow(string EmoteId, DateOnly Date, int UseCount, int BotUseCount, int SharedChatUseCount);

/// <summary>
/// The frozen comparison window plus the channel's bot-split cutover and the explicitly configured
/// shared-chat cutover (D4).
/// <para>
/// <see cref="BotSplitCutover"/> is the earliest day whose live rows can carry a
/// <c>BotUseCount</c> at all; it stays in the identity, the report header and the report line
/// unchanged (Freeze), but as of <c>harness-2</c> it no longer decides which days the gate rates —
/// see <see cref="SharedChatCutover"/>.
/// </para>
/// <para>
/// <see cref="SharedChatCutover"/> is the day from which <c>HumanOnly</c> is true, and the only
/// input to that decision (B5): <c>Day &gt;= SharedChatCutover</c>. This is not merely convenient,
/// it is more correct than gating on <see cref="BotSplitCutover"/> ever was — the bot split
/// deployed on 2026-09-01, before #73, so every day at or after the shared-chat cutover
/// necessarily also lies after the bot-split deploy and carries bots separately, independent of
/// when this particular channel happened to show its first bot. It is the rolled-out *contract*
/// that decides, not the first sighting in the data — exactly the E4 imprecision the bot cutover
/// carries. <c>null</c> means no cutover was configured; days before it (and every day if it is
/// <c>null</c>) cannot be compared human-only and never enter the gate.
/// </para>
/// </summary>
public sealed record ReplayWindow(DateOnly From, DateOnly To, DateOnly? BotSplitCutover, DateOnly? SharedChatCutover);

/// <summary>Why a chat token did not count towards an emote on a given day.</summary>
public enum UnmatchedReason
{
    /// <summary>No emote of the channel ever carried that name.</summary>
    UnknownName,

    /// <summary>
    /// The name is carried by more than one emote of the day. The hit still counts on the
    /// coalesced id (exactly as the live path would), and this reason is an additional marker.
    /// </summary>
    AmbiguousName,

    /// <summary>The emote of that name was added to the set after this day.</summary>
    BeforeFirstSeen,

    /// <summary>
    /// The emote of that name was archived before this day — including "archived without a date",
    /// which is excluded from the day map and counted separately in the final report.
    /// </summary>
    AfterArchived,
}

/// <summary>
/// The <see cref="ReplayDayLine.Status"/> values the calculator itself branches on. Any other
/// string is allowed and is treated as "this day has no usable log".
/// </summary>
public static class ReplayDayStatuses
{
    /// <summary>The whole day was fetched and counted.</summary>
    public const string Complete = "Complete";

    /// <summary>The archive has no log for that day (the normal 404 case).</summary>
    public const string NoLog = "NoLog";

    /// <summary>
    /// The archive answered 429. Vocabulary only: the harness deliberately never writes a day line
    /// with this status, because a day line means "finished" and the resume would then skip that day
    /// forever. It writes an event line instead, and the run-level count reaches the report through
    /// <see cref="ReplayFidelityCalculator.Compute"/>'s <c>rateLimitedDays</c> parameter. Should such
    /// a line ever appear anyway, the calculator treats it like every other non-Complete status: the
    /// day has no usable log.
    /// </summary>
    public const string RateLimited = "RateLimited";
}

/// <summary>Stable tokens for <see cref="ReplayGateMetrics.GateIneligibleReasons"/>.</summary>
public static class ReplayGateIneligibleReasons
{
    /// <summary>The run did not cover the whole window.</summary>
    public const string RunIncomplete = "run-incomplete";

    /// <summary>The window was not the pre-registered 30 days.</summary>
    public const string WindowNotThirtyDays = "window-not-30-days";

    /// <summary>Fewer than the pre-registered 20 rated days.</summary>
    public const string RatedDaysBelowTwenty = "rated-days-below-20";

    /// <summary>No live usage at all over the rated days, so the deviation has no denominator.</summary>
    public const string LiveTotalZero = "live-total-zero";

    /// <summary>
    /// The two sides disagree about how much shared chat there was over the rated days (D3/B5).
    /// Live and replay derive the room classification independently — IRC tags through TwitchLib
    /// against Justlog tags through our own parser — so a divergence here is a divergence about the
    /// classification #73 introduced, which is the one thing the run exists to certify. A fidelity
    /// number computed on top of that would measure the wrong thing while looking healthy, hence a
    /// refusal rather than a number.
    /// </summary>
    public const string SharedChatAsymmetric = "shared-chat-asymmetric";

    /// <summary>
    /// A diagnostic run (<c>--diagnostic</c>): the numbers are computed as usual, but the run had
    /// no shared-chat cutover to gate against, or the operator explicitly asked for numbers without
    /// a verdict. Set unconditionally on a diagnostic run, in addition to whatever other reasons
    /// apply (D4/Plan-Entscheidung 7).
    /// </summary>
    public const string DiagnosticRun = "diagnostic-run";
}

/// <summary>
/// Everything one archive day contributed, as written to the JSONL protocol. The dictionaries are
/// keyed by emote id and by <see cref="UnmatchedReason"/> name; no chatter id ever appears here —
/// the per-cell chatter sets are collapsed into <see cref="KHistogram"/> and dropped.
/// <para>
/// Note for later consumers: the collection members give this record reference equality, so two
/// separately built lines are never <c>Equals</c>. Only <see cref="ReplayFinalReport"/> carries a
/// value-equality guarantee (it is the reproducibility contract of the run).
/// </para>
/// <para>
/// <see cref="SharedChatMessageCount"/> and <see cref="SharedChatCounts"/> (#73) are easy to
/// confuse and count different things. The former is a message-level control number and counts
/// only unambiguously foreign messages (<c>MessageOrigin.Foreign</c>) — it does not move for an
/// indeterminate one, which has its own counter, <see cref="IndeterminateMessageCount"/>. The
/// latter is an emote-hit dictionary and receives hits from <b>both</b> foreign and indeterminate
/// messages (<c>UsageCategory.SharedChat</c> folds the two together, D2/B2), because the rule that
/// hides the shared-chat *component* from being counted as this channel's own use is the same for
/// both — a hit is a hit. The control sum: <c>Σ SharedChatCounts &gt; 0</c> implies
/// <c>SharedChatMessageCount + IndeterminateMessageCount &gt; 0</c>, never
/// <c>SharedChatMessageCount &gt; 0</c> alone.
/// </para>
/// </summary>
public sealed record ReplayDayLine(
    DateOnly Day,
    string Status,
    long Bytes,
    string? BodySha256Hex,
    int MessageCount,
    int BotMessageCount,
    int SharedChatMessageCount,
    int IndeterminateMessageCount,
    int NonPrivmsgLines,
    int MalformedLines,
    int OutsideDayCount,
    IReadOnlyDictionary<string, int> HumanCounts,
    IReadOnlyDictionary<string, int> BotCounts,
    IReadOnlyDictionary<string, int> SharedChatCounts,
    IReadOnlyDictionary<string, int> UnmatchedByReason,
    int FirstSeenUnknownHits,
    IReadOnlyList<int> KHistogram,
    int CellCount,
    int DistinctChatters);

/// <summary>One day's log-to-live ratio, used to report gap days and questionable-coverage days.</summary>
public sealed record ReplayDayRatio(DateOnly Day, long LogTotal, long LiveTotal, double? Ratio);

/// <summary>
/// The pre-registered numbers of issue #69, over rated days only, human-only, and over the full
/// import population (every emote with live &gt; 0 or log &gt; 0, archived and renamed ones
/// included — Codex-adversarial decision D1). The thresholds themselves live in the issue, never
/// here: this record hands out the values, the human reads them against #69.
/// <para>
/// <c>HumanLogTotal</c> and <c>HumanLiveTotal</c> carry "human" in the name on purpose: they are
/// the denominators of the gate and exclude bots on both sides (live is <c>UseCount</c> alone, log
/// is the human hit count), whereas <see cref="ReplayPlausibility"/> right next to them in the
/// report sums both sides <b>with</b> bots. The three numbers are not comparable.
/// </para>
/// <para>
/// <b>The bottom-quartile pair was rebuilt in #97 to stop reading the tie-break's GUID instead of
/// the counts.</b> The old formula ranked both sides with <see cref="ReplayFidelityCalculator"/>'s
/// <c>Ranking</c> — descending by count, ties broken ordinally by the emote's internal id — and took
/// the bottom <c>BottomQuartileSize</c> ids off each ranking. Live and log share the very same id
/// space (one <c>PopulationEntry</c> per emote, on both sides), so at an exact-tie block straddling
/// the cutoff, the same id decided inclusion on <i>both</i> sides at once, nesting the two selections
/// into one another and forcing their overlap towards its maximum — a papaplatte trial run over 28
/// days measured a 182-entry quartile with an 88-entry (live) / 87-entry (log) tie block sitting
/// exactly on the boundary, and the reported precision of 0.9505 was in large part that nesting, not
/// a measured rank deviation. <c>BottomQuartilePrecision</c> now compares two value-defined sets
/// instead: <c>QuartileLiveSet = { e : e.Live &lt;= liveCutoff }</c> and the same for
/// <c>QuartileLogSet</c> with <c>logCutoff</c>, where each cutoff is the count sitting at position
/// <c>PopulationSize - BottomQuartileSize</c> of its own descending order (exactly what
/// <c>BottomQuartileLiveTieCount</c>/<c>BottomQuartileLogTieCount</c> already located). Neither set
/// construction looks at an id to decide membership, so a tie block straddling the cutoff is included
/// or excluded <b>whole</b>, identically regardless of which GUIDs its members happen to carry.
/// <c>BottomQuartilePrecision = |QuartileLiveSet ∩ QuartileLogSet| / |QuartileLogSet|</c>. A perfect
/// log (<c>Log == Live</c> for every entry) makes both cutoffs equal and the two sets identical, so
/// the value is still exactly 1.0 — the pre-registered 0.8 threshold keeps the meaning it always had.
/// <c>BottomQuartileLiveSize</c> and <c>BottomQuartileLogSize</c> report the two sets' actual sizes:
/// each is at least <c>BottomQuartileSize</c> and can run well past it when a plateau sits on the
/// cutoff, at which point the "quartile" is, honestly, a larger plateau than a quarter of the
/// population. <c>Ranking</c> and its ordinal id tie-break are unchanged and still decide Top20Recall
/// (D-97 leaves that formula untouched — see <c>Top20LiveTieCount</c> below for why that is
/// acceptable rather than the same defect left standing).
/// </para>
/// <para>
/// <c>BottomQuartileLiveTieCount</c> and <c>BottomQuartileLogTieCount</c> are unchanged since the
/// #69 nachtrag and keep read-only visibility into the tie-break: each counts how many population
/// entries share the exact count value sitting at its side's cutoff. They now explain why
/// <c>BottomQuartileLiveSize</c>/<c>BottomQuartileLogSize</c> can exceed the nominal
/// <c>BottomQuartileSize</c> — a long-tailed population (many emotes tied at, say, one use over the
/// whole window) can make a tie count most of <c>BottomQuartileSize</c>, at which point the quartile
/// sets above are mostly that one plateau. They were never a second gate and still are not.
/// </para>
/// <para>
/// <c>TailDeviation</c> is <c>Σ|e.Log − e.Live| / Σ e.Live</c> over <c>QuartileLiveSet</c> — a
/// reporting-only figure, deliberately excluded from the gate and from <c>GateIneligibleReasons</c>.
/// It exists because a rank-based set comparison, however tie-safe, cannot see a uniform tail loss: a
/// log that undercounts every low-traffic emote by the same fraction never changes who ranks at the
/// bottom, so <c>BottomQuartilePrecision</c> stays perfect while real volume silently disappears.
/// There is no pre-registered, calibrated threshold for this number (unlike the three #69 figures),
/// so it is reported for a human to read, never compared against a cutoff. <c>null</c> when
/// <c>Σ e.Live</c> over <c>QuartileLiveSet</c> is 0 — possible when <c>liveCutoff</c> is 0, i.e. at
/// least <c>BottomQuartileSize</c> emotes never occurred live at all — because that is "no
/// denominator", not "zero deviation".
/// </para>
/// <para>
/// <c>Top20LiveTieCount</c> and <c>Top20LogTieCount</c> are the same visibility as the quartile tie
/// counts, mirrored at the top cutoff (position <c>Top20Size - 1</c> of each descending order,
/// counted over the <b>whole</b> population, not just the top 20) — added in #97 purely for
/// symmetry with the quartile's now-visible tie-break. <c>Top20Recall</c>'s formula is unchanged:
/// exact ties are rare at the qualified channels' four/five-digit usage counts, so the structural
/// defect that forced the quartile rebuild is not worth the same rebuild here — these two counts
/// exist so a reader can confirm that for a given run rather than assume it.
/// </para>
/// <para>
/// <c>SharedChatLogTotal</c> and <c>SharedChatLiveTotal</c> are the two sides of the #73 split over
/// the same rated days, reported apart so a reader sees the split instead of inferring it — and
/// they are the input of the <see cref="ReplayGateIneligibleReasons.SharedChatAsymmetric"/>
/// eligibility condition. They are <b>not</b> part of any pre-registered figure: neither of them
/// enters <c>TotalDeviation</c>, the rankings or the three published thresholds.
/// </para>
/// <para>
/// What this deliberately does not check (D3, honestly): the bot/foreign boundary <i>inside</i> the
/// foreign share. A foreign bot filed as an own bot on one side and as foreign on the other moves
/// neither the three-component day total nor the human-only gate. That boundary is product-side
/// inconsequential — both categories are excluded from the target grid, so no user sees it and no
/// deletion rests on it. The boundary that does carry weight, own human against foreign human, is
/// measured directly by the human-only gate: a foreign message counted as own live lands in
/// <c>UseCount</c> and not in the replay's human counts, which moves <c>TotalDeviation</c>.
/// </para>
/// </summary>
public sealed record ReplayGateMetrics(
    int RatedDays,
    int PopulationSize,
    long HumanLogTotal,
    long HumanLiveTotal,
    long SharedChatLogTotal,
    long SharedChatLiveTotal,
    double? TotalDeviation,
    double? Top20Recall,
    int Top20Size,
    int Top20LiveTieCount,
    int Top20LogTieCount,
    double? BottomQuartilePrecision,
    int BottomQuartileSize,
    int BottomQuartileLiveSize,
    int BottomQuartileLogSize,
    int BottomQuartileLiveTieCount,
    int BottomQuartileLogTieCount,
    double? TailDeviation,
    bool GateEligible,
    ValueList<string> GateIneligibleReasons);

/// <summary>
/// Plausibility check (a): both sides summed over all three components — own humans, own bots and
/// shared chat — over every day that has a log. Covers the days before the shared-chat cutover,
/// which the gate cannot use.
/// <para>
/// The field names keep saying "WithBots" although they now also carry the shared-chat component
/// (#73/D3). That imprecision is deliberate: the <c>.report.json</c> field names are the contract a
/// reader compares reports across weeks with, and renaming them would break that comparison for a
/// wording improvement.
/// </para>
/// </summary>
public sealed record ReplayPlausibility(
    long LogTotalWithBots,
    long LiveTotalWithBots,
    double? Ratio,
    long Difference);

/// <summary>
/// Everything that explains the gate numbers without binding anything: the stable-subset
/// diagnostics of the original pre-registration, the shape of the population, the matching
/// reasons, the message-level counters and the privacy figures.
/// <para>
/// Two day counts here are easy to confuse. <c>HumanOnlyDays</c> counts every day of the run at or
/// after the shared-chat cutover, <b>including days without a log</b> — it says how far the
/// human-only comparison could reach at all. How many days metric (b) actually covers is
/// <c>FlaggedIncludedDays</c> (has a log and is human-only) and, after the coverage and gap
/// markers, <c>ReplayGateMetrics.RatedDays</c>.
/// </para>
/// <para>
/// <c>DailyDeviation</c> and <c>DailyDeviationWithOneDayTolerance</c> are the secondary day diff.
/// They exist because <c>UsageStat.Date</c> is the day of the <i>flush</i>, not of the message:
/// the 30-second batch and its requeue rounds push usage around midnight onto the following day,
/// which inflates the daily numbers without the window sums moving at all. The tolerant variant
/// lets a log day be explained by the neighbouring live days; the gap between the two figures is
/// the size of that artefact, and it is what tells a later reader whether a bad coverage ratio was
/// a data problem or a day offset.
/// </para>
/// <para>
/// <c>SignallessRatedDays</c> counts rated days on which neither side saw anything at all (a dead
/// channel day: log total and live total both zero). Such a day passes every rating condition and
/// raises <c>RatedDays</c> towards the pre-registered minimum of 20 without contributing a single
/// comparison. The gate definition is published in #69 and stays as it is; this field exists so a
/// reader can subtract those days instead of being quietly misled by the count.
/// </para>
/// <para>
/// <c>SharedChatByDay</c> carries one entry per day <b>that has a log</b> — not only the rated ones,
/// unlike the two window sums on <see cref="ReplayGateMetrics"/>. That is what makes the three
/// signatures of D3 readable: before the live deploy the live side is 0 while the replay side is
/// &gt; 0; on the deploy day the live side sits between the two (morning flushes under the old rule,
/// afternoon flushes under the new one, added into the same row); afterwards both sides agree. A
/// rollback day inside the window would show the middle signature.
/// </para>
/// </summary>
public sealed record ReplayDiagnostics(
    int StableSubsetSize,
    int QualifiedEmoteCount,
    int MinLiveUsesN,
    int RequiredQualifiedM,
    bool StableSubsetDecisive,
    double? StableSubsetMedianDeviation,
    double? StableSubsetP90Deviation,
    double? StableSubsetSpearman,
    int StableSubsetZeroLogCount,
    int AllEmotesQualifiedCount,
    double? AllEmotesMedianDeviation,
    double? AllEmotesP90Deviation,
    double? AllEmotesSpearman,
    int AllEmotesZeroLogCount,
    int LogOnlyEmoteCount,
    int LiveOnlyEmoteCount,
    double? LogOnlyShareOfLogTotal,
    double? LiveOnlyShareOfLiveTotal,
    double? TotalDeviationIncludingFlaggedDays,
    int FlaggedIncludedDays,
    double? DailyDeviation,
    double? DailyDeviationWithOneDayTolerance,
    long UnknownNameHits,
    long AmbiguousNameHits,
    long BeforeFirstSeenHits,
    long AfterArchivedHits,
    long FirstSeenUnknownHits,
    int AmbiguousNameCount,
    int ArchivedWithoutDateCount,
    long TotalMessages,
    long BotMessages,
    long SharedChatMessages,
    long IndeterminateMessages,
    long OutsideDayCount,
    long NonPrivmsgLines,
    long MalformedLines,
    double? SingleChatterCellShare,
    ValueList<int> KHistogram,
    long CellCount,
    long DistinctChatterDaySum,
    int HumanOnlyDays,
    int LogDays,
    int NoLogDays,
    int SignallessRatedDays,
    double? DayRatioMedian,
    ValueList<ReplayDayRatio> LiveGapDays,
    ValueList<ReplayDayRatio> CoverageQuestionableDays,
    ValueList<ReplayDayRatio> SharedChatByDay);

/// <summary>How the run itself went — the part of the report that is about the fetch, not the numbers.</summary>
/// <param name="RateLimitedDays">
/// How many channel-day requests the archive throttled with a 429, across every run that wrote into
/// this report file. Supplied by the caller rather than counted from <paramref name="DayLineCount"/>'s
/// lines: a throttled day produces no day line at all (see
/// <see cref="ReplayDayStatuses.RateLimited"/>), so deriving it here would report 0 forever.
/// </param>
/// <param name="ResumePoint">
/// The last archive day that has a line. Also supplied by the caller. On a complete run — the only
/// kind that currently produces a final report — this equals <paramref name="WindowTo"/> and is a
/// statement of where the run ended, not an instruction to continue anywhere.
/// </param>
/// <param name="TotalBytes">
/// Also supplied by the caller, and for the same reason as <see cref="RateLimitedDays"/>: an aborted
/// attempt's bytes live only on an event line, never on a day line, so deriving this from
/// <see cref="DayLineCount"/>'s lines alone would under-report every run an abort ever touched.
/// </param>
public sealed record ReplayRunInfo(
    DateOnly WindowFrom,
    DateOnly WindowTo,
    DateOnly? BotSplitCutover,
    DateOnly? SharedChatCutover,
    int WindowDays,
    int DayLineCount,
    long TotalBytes,
    int RateLimitedDays,
    DateOnly? ResumePoint,
    bool RunComplete,
    // Not part of HarnessRunIdentity (Plan-Entscheidung 7, D4): a diagnostic run counts exactly the
    // same as a binding one, it only carries no gate verdict. A binding run may therefore resume a
    // diagnostic file of the same identity and simply rewrite the report without this marker — that
    // is intended, not a bug in the resume logic.
    bool Diagnostic);

/// <summary>
/// The final report: two runs of <see cref="ReplayFidelityCalculator.Compute"/> over the same
/// arguments are <c>Equals</c>. Every measured number comes from the day lines; only the two
/// run-level counters on <see cref="ReplayRunInfo"/> that no day line can carry are passed in.
/// <para>
/// <see cref="Recomputation"/> defaults to <c>null</c> and stays <c>null</c> for every ordinary run
/// — <see cref="ReplayFidelityCalculator.Compute"/> never sets it, it is filled in only by
/// <c>HarnessRunner.RecomputeReportAsync</c> (issue #119) after <c>Compute</c> returns. The
/// <see cref="JsonIgnoreAttribute"/> is load-bearing, not decoration: <c>ReportOptions</c> (unlike
/// <c>LineOptions</c>) carries no <c>DefaultIgnoreCondition</c>, so without it every ordinary run's
/// <c>.report.json</c> would gain a literal <c>"recomputation": null</c> property — bytes an
/// unrelated run did not have before this feature existed. With it, an ordinary report's bytes are
/// unchanged, which is the actual claim behind "purely additive to the JSON shape", the same
/// treatment the #97 tie-count fields got and for the same reason it needs no
/// <c>HarnessRunner.AlgorithmVersion</c> bump: it changes nothing about how a day is counted, only
/// what a report says about how it was produced.
/// </para>
/// </summary>
public sealed record ReplayFinalReport(
    ReplayRunInfo Run,
    ReplayGateMetrics Gate,
    ReplayPlausibility Plausibility,
    ReplayDiagnostics Diagnostics,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HarnessRecomputation? Recomputation = null);

/// <summary>
/// Present exactly when the <see cref="ReplayFinalReport"/> carrying it came from a report-only
/// recompute (issue #119) instead of a run that fetched the archive — see
/// <see cref="ReplayFinalReport.Recomputation"/>.
/// <para>
/// A recompute reads a live snapshot that is, by construction, at least as new as the one the
/// original run compared against — Postgres keeps moving while the frozen <c>.jsonl</c> does not —
/// so <see cref="InputHashMatches"/> and <see cref="BotSplitCutoverMatches"/> are the two things a
/// reader has to check before trusting the numbers as a reproduction of the original run rather
/// than a comparison against a newer database state. Both cutovers in this record are read from the
/// original run's frozen header, never from today's configuration or today's live bot usage — see
/// the remark on <c>HarnessRunner.RecomputeReportAsync</c>.
/// </para>
/// <para>
/// <see cref="DiagnosticSource"/> is <c>"inherited"</c> when the original run's
/// <c>&lt;stem&gt;.report.json</c> exists and is readable, so its own <c>Run.Diagnostic</c> flag can
/// be reused, and <c>"defaulted"</c> when it does not — missing, unparsable or otherwise unreadable
/// are all the same case (the diagnostic flag is never part of <c>HarnessRunIdentity</c> or the
/// header — see the remark on <see cref="ReplayRunInfo.Diagnostic"/> — so a header-only recompute has
/// no other source for it). A defaulted flag falls back to <c>true</c>, not <c>false</c>: the same
/// fail-closed spirit as D4 — a missing original must never silently turn a diagnostic run into a
/// binding verdict just because nothing was left to say otherwise.
/// </para>
/// <para>
/// <see cref="Warnings"/> carries machine-readable codes rather than prose, so a caller can branch
/// on it without parsing a sentence: currently <c>"input-hash-mismatch"</c> and/or
/// <c>"bot-split-cutover-drift"</c>, each also logged as its own English warning line and rendered
/// as a prominent block at the top of the recompute's Markdown.
/// </para>
/// </summary>
public sealed record HarnessRecomputation(
    string SourceFile,
    DateTime RecomputedAtUtc,
    string OriginalInputHash,
    string CurrentInputHash,
    bool InputHashMatches,
    DateOnly? OriginalBotSplitCutover,
    DateOnly? CurrentBotSplitCutover,
    bool BotSplitCutoverMatches,
    string DiagnosticSource,
    ValueList<string> Warnings);

/// <summary>
/// A read-only list with value equality. Exists for one reason: the report must satisfy "two
/// <c>Compute</c> calls produce equal objects", and a record's generated <c>Equals</c> compares
/// list members by reference — <see cref="System.Collections.Immutable.ImmutableArray{T}"/>
/// included, whose own equality is reference equality of the backing array.
/// </summary>
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly T[] _items;

    public ValueList(IEnumerable<T> items) => _items = [.. items];

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public bool Equals(ValueList<T>? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return _items.SequenceEqual(other._items);
    }

    public override bool Equals(object? obj) => Equals(obj as ValueList<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}
