using EmotePurge.Core.Matching;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// Turns the day lines of a harness run into the final report of issue #69 — a pure function of its
/// arguments, so the same run always yields the same numbers and the report can be rebuilt from the
/// JSONL protocol alone.
/// <para>
/// It computes the pre-registered figures and nothing else: the thresholds (deviation ≤ 10 %,
/// top-20 recall ≥ 0.9, bottom-quartile precision ≥ 0.8, at least 20 rated days, window 30 days)
/// are published in issue #69 and are read against these numbers by a human. Deliberately no
/// verdict is computed here.
/// </para>
/// <para>
/// Since #73 the numbers rest on three components instead of two — own humans, own bots and shared
/// chat. Which of them a figure sums differs per figure and is stated at each builder: the day
/// totals and the plausibility check take all three, the pre-registered gate stays human-only, and
/// the shared-chat share of both sides is reported apart with a symmetry condition of its own.
/// </para>
/// </summary>
public static class ReplayFidelityCalculator
{
    private const int RequiredWindowDays = 30;
    private const int RequiredRatedDays = 20;

    // The same figure as the pre-registered deviation threshold of #69, but a different kind of
    // rule: an eligibility condition like RequiredRatedDays, not a fourth pre-registered gate with
    // a threshold of its own (D3). It therefore has to be registered publicly in #69 before the
    // binding run, exactly like the criteria already standing there (DoD, Task 8).
    private const double MaxSharedChatAsymmetry = 0.10;

    private const int TopSize = 20;
    private const int RequiredQualifiedEmotes = 30;
    private const int MinLiveUsesPerThirtyDays = 20;
    private const int MinLiveUsesFloor = 5;
    private const int RoundingDigits = 4;
    private const int MinHistogramLength = 11;

    /// <param name="window">The frozen window plus the channel's bot-split cutover.</param>
    /// <param name="emotes">Every emote of the channel, archived ones included.</param>
    /// <param name="liveRows">The live <c>UsageStat</c> rows of the window.</param>
    /// <param name="days">One line per archive day the run produced.</param>
    /// <param name="windowDays">The window length the run was started with (30 for a binding run).</param>
    /// <param name="runComplete">Whether the run covered the whole window; an aborted run never binds.</param>
    /// <param name="totalBytes">
    /// Every byte the archive sent for this report file, across every run that wrote into it —
    /// finished days and aborted attempts alike. A caller-supplied number for the same reason as
    /// <paramref name="rateLimitedDays"/>: an aborted attempt (429, body timeout, byte cap,
    /// transport failure) never becomes a day line, only an event line, so summing
    /// <paramref name="days"/> alone would silently drop those bytes from the report. The caller is
    /// expected to pass the same total the byte-cap bookkeeping already uses
    /// (<c>existing.Days.Sum(...) + existing.Events.Sum(...)</c> in <see cref="HarnessRunner"/>), so
    /// the two can never disagree.
    /// </param>
    /// <param name="rateLimitedDays">
    /// How many channel-day requests the archive answered with a 429, over every run that wrote into
    /// this report file. A caller-supplied number on purpose: a throttled day never becomes a day
    /// line (that would mark it finished and make the resume skip it), so counting it out of
    /// <paramref name="days"/> would report 0 in every report ever written.
    /// </param>
    /// <param name="resumePoint">
    /// The last archive day that has a line. Equal to the window's end for a complete run.
    /// </param>
    /// <param name="diagnostic">
    /// Whether this run was started with <c>--diagnostic</c> (D4). Every number below is computed
    /// exactly as for a binding run; only the gate's verdict is withheld — the gate builder adds
    /// <see cref="ReplayGateIneligibleReasons.DiagnosticRun"/> unconditionally when this is
    /// <c>true</c>, on top of whatever other reasons apply. Deliberately not part of
    /// <c>HarnessRunIdentity</c> either (Plan-Entscheidung 7): it changes the verdict, not the
    /// counting.
    /// </param>
    public static ReplayFinalReport Compute(
        ReplayWindow window,
        IReadOnlyList<ReplayEmote> emotes,
        IReadOnlyList<ReplayUsageRow> liveRows,
        IReadOnlyList<ReplayDayLine> days,
        int windowDays,
        bool runComplete,
        long totalBytes,
        int rateLimitedDays,
        DateOnly? resumePoint,
        bool diagnostic)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(emotes);
        ArgumentNullException.ThrowIfNull(liveRows);
        ArgumentNullException.ThrowIfNull(days);

        var ambiguousNames = EmoteNameMatching.Coalesce(
            emotes.Select(e => new KeyValuePair<string, string>(e.Name, e.Id))).AmbiguousNames;

        var (facts, dayRatioMedian) = BuildDayFacts(window, days, liveRows);
        var ratedDays = DaySet(facts, f => f.Rated);
        var humanOnlyLogDays = DaySet(facts, f => f is { HasLog: true, HumanOnly: true });
        var logDays = DaySet(facts, f => f.HasLog);

        var population = BuildPopulation(days, liveRows, ratedDays);
        var gate = BuildGate(
            population,
            ratedDays.Count,
            facts.Where(f => f.Rated).Sum(f => f.SharedChatLogTotal),
            facts.Where(f => f.Rated).Sum(f => f.SharedChatLiveTotal),
            windowDays,
            runComplete,
            diagnostic);
        var plausibility = BuildPlausibility(days, liveRows, logDays);
        var diagnostics = BuildDiagnostics(
            window, emotes, days, liveRows, facts, population, ambiguousNames, humanOnlyLogDays, dayRatioMedian);

        var run = new ReplayRunInfo(
            window.From,
            window.To,
            window.BotSplitCutover,
            window.SharedChatCutover,
            windowDays,
            days.Count,
            totalBytes,
            rateLimitedDays,
            resumePoint,
            runComplete,
            diagnostic);

        return new ReplayFinalReport(run, gate, plausibility, diagnostics);
    }

    /// <summary>
    /// Classifies every day: does it have a log, is it comparable human-only, what is its
    /// log-to-live ratio, is a live gap suspected, is its coverage questionable, does it count.
    /// <para>
    /// Both totals sum <b>all three</b> components (D3, first pair): human + bot + shared chat
    /// against <c>UseCount + BotUseCount + SharedChatUseCount</c>. The question this day total
    /// answers is "does the archive have this day at all", and for that question the day total is
    /// the right one precisely because it is <i>invariant</i> against both splits — a split moves
    /// mass between columns, it neither creates nor destroys any. That is why <c>Ratio</c> stays
    /// meaningful even on days before the live deploy, where the live row still carries foreign
    /// hits inside <c>UseCount</c> that the replay side already books apart. A human-only day total
    /// would read those days as missing coverage and drop them out of the rated set; the binding
    /// gate below is human-only for the opposite reason and needs the cutover of D4 to survive it.
    /// </para>
    /// <para>
    /// The median that the coverage check (Codex-adversarial D2) compares against is taken over the
    /// days that <b>have a log</b> and a defined ratio. Days without a log have a log total of zero
    /// and would drag the median towards zero, which would then flag the intact days as
    /// questionable — the opposite of what the check is for. This is a deliberate reading of the
    /// design's intent; see the task report.
    /// </para>
    /// </summary>
    private static (List<DayFact> Facts, double? RatioMedian) BuildDayFacts(
        ReplayWindow window,
        IReadOnlyList<ReplayDayLine> days,
        IReadOnlyList<ReplayUsageRow> liveRows)
    {
        var liveByDay = new Dictionary<DateOnly, long>();
        var sharedChatLiveByDay = new Dictionary<DateOnly, long>();
        foreach (var row in liveRows)
        {
            liveByDay[row.Date] =
                liveByDay.GetValueOrDefault(row.Date) + row.UseCount + row.BotUseCount + row.SharedChatUseCount;
            sharedChatLiveByDay[row.Date] = sharedChatLiveByDay.GetValueOrDefault(row.Date) + row.SharedChatUseCount;
        }

        var facts = new List<DayFact>(days.Count);
        foreach (var line in days.OrderBy(d => d.Day))
        {
            var sharedChatLogTotal = Sum(line.SharedChatCounts);
            var logTotal = Sum(line.HumanCounts) + Sum(line.BotCounts) + sharedChatLogTotal;
            var liveTotal = liveByDay.GetValueOrDefault(line.Day);
            facts.Add(new DayFact
            {
                Day = line.Day,
                HasLog = line.Status == ReplayDayStatuses.Complete,
                // As of harness-2 this depends on the shared-chat cutover alone (B5) — the
                // bot-split cutover no longer enters the condition; see the remark at
                // ReplayWindow.SharedChatCutover for why that is correct, not just permitted.
                HumanOnly = window.SharedChatCutover is { } cutover && line.Day >= cutover,
                LogTotal = logTotal,
                LiveTotal = liveTotal,
                SharedChatLogTotal = sharedChatLogTotal,
                SharedChatLiveTotal = sharedChatLiveByDay.GetValueOrDefault(line.Day),
                Ratio = liveTotal == 0 ? null : (double)logTotal / liveTotal,
                LiveGapSuspected = liveTotal == 0 && logTotal > 0,
            });
        }

        var ratios = facts
            .Where(f => f.HasLog && f.Ratio is not null)
            .Select(f => f.Ratio!.Value)
            .Order()
            .ToList();
        var median = Percentile(ratios, 0.5);

        foreach (var fact in facts)
        {
            fact.CoverageQuestionable = fact.HasLog
                && median is { } m
                && fact.Ratio is { } ratio
                && (ratio < m / 2 || ratio > 2 * m);
            fact.Rated = fact is { HasLog: true, HumanOnly: true, LiveGapSuspected: false, CoverageQuestionable: false };
        }

        return (facts, median);
    }

    /// <summary>
    /// The full import population over the given days, human-only on both sides: live is
    /// <c>UseCount</c>, log is the human hit count. Every emote that appears on at least one side
    /// is in, log-only and live-only included — that is exactly the case the gate must not hide
    /// (Codex-adversarial D1). <c>UseCount</c> is the target contract (D3).
    /// </summary>
    private static List<PopulationEntry> BuildPopulation(
        IReadOnlyList<ReplayDayLine> days,
        IReadOnlyList<ReplayUsageRow> liveRows,
        HashSet<DateOnly> daySet)
    {
        var log = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in days)
        {
            if (!daySet.Contains(line.Day))
            {
                continue;
            }

            foreach (var (emoteId, count) in line.HumanCounts)
            {
                log[emoteId] = log.GetValueOrDefault(emoteId) + count;
            }
        }

        var live = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in liveRows.Where(row => daySet.Contains(row.Date)))
        {
            live[row.EmoteId] = live.GetValueOrDefault(row.EmoteId) + row.UseCount;
        }

        var ids = new HashSet<string>(log.Keys, StringComparer.Ordinal);
        ids.UnionWith(live.Keys);

        return ids
            .Select(id => new PopulationEntry(id, live.GetValueOrDefault(id), log.GetValueOrDefault(id)))
            .Where(e => e.Live > 0 || e.Log > 0)
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The pre-registered numbers, human-only (D3, third pair), plus the shared-chat window sums of
    /// both sides over the same rated days and the symmetry condition they feed.
    /// </summary>
    /// <param name="sharedChatLogTotal">Σ shared-chat hits of the replay side over the rated days.</param>
    /// <param name="sharedChatLiveTotal">Σ <c>SharedChatUseCount</c> of the live rows on the rated days.</param>
    private static ReplayGateMetrics BuildGate(
        List<PopulationEntry> population,
        int ratedDays,
        long sharedChatLogTotal,
        long sharedChatLiveTotal,
        int windowDays,
        bool runComplete,
        bool diagnostic)
    {
        var logTotal = population.Sum(e => e.Log);
        var liveTotal = population.Sum(e => e.Live);
        var totalDeviation = liveTotal == 0
            ? null
            : (double?)population.Sum(e => Math.Abs(e.Log - e.Live)) / liveTotal;

        var liveRanking = Ranking(population, e => e.Live);
        var logRanking = Ranking(population, e => e.Log);
        var count = population.Count;

        var topSize = Math.Min(TopSize, count);
        double? top20Recall = topSize == 0
            ? null
            : (double)Overlap(liveRanking.Take(topSize), logRanking.Take(topSize)) / topSize;

        var top20LiveTieCount = CountTiesAtBoundary(population, e => e.Live, topSize - 1);
        var top20LogTieCount = CountTiesAtBoundary(population, e => e.Log, topSize - 1);

        var quartileSize = count == 0 ? 0 : Math.Max(1, count / 4);

        // The tie-safe quartile sets (D-97): every entry at or below the cutoff *value*, not the
        // nominal count of entries. See BuildQuartileSet for why this makes BottomQuartilePrecision
        // independent of the ordinal id tie-break that Ranking() still applies for Top20Recall.
        var liveCutoff = BoundaryValue(population, e => e.Live, count - quartileSize);
        var logCutoff = BoundaryValue(population, e => e.Log, count - quartileSize);
        var quartileLiveSet = BuildQuartileSet(population, e => e.Live, liveCutoff);
        var quartileLogSet = BuildQuartileSet(population, e => e.Log, logCutoff);

        double? bottomQuartilePrecision = quartileLogSet.Count == 0
            ? null
            : (double)Overlap(quartileLiveSet.Select(e => e.Id), quartileLogSet.Select(e => e.Id)) / quartileLogSet.Count;

        var liveTailSum = quartileLiveSet.Sum(e => e.Live);
        double? tailDeviation = liveTailSum == 0
            ? null
            : (double)quartileLiveSet.Sum(e => Math.Abs(e.Log - e.Live)) / liveTailSum;

        var liveTieCount = CountTiesAtBoundary(population, e => e.Live, count - quartileSize);
        var logTieCount = CountTiesAtBoundary(population, e => e.Log, count - quartileSize);

        var reasons = new List<string>();
        if (!runComplete)
        {
            reasons.Add(ReplayGateIneligibleReasons.RunIncomplete);
        }

        if (windowDays != RequiredWindowDays)
        {
            reasons.Add(ReplayGateIneligibleReasons.WindowNotThirtyDays);
        }

        if (ratedDays < RequiredRatedDays)
        {
            reasons.Add(ReplayGateIneligibleReasons.RatedDaysBelowTwenty);
        }

        if (totalDeviation is null)
        {
            reasons.Add(ReplayGateIneligibleReasons.LiveTotalZero);
        }

        if (!SharedChatSymmetric(sharedChatLogTotal, sharedChatLiveTotal))
        {
            reasons.Add(ReplayGateIneligibleReasons.SharedChatAsymmetric);
        }

        if (diagnostic)
        {
            // Unconditional, in addition to whatever else applies: the numbers above are computed
            // exactly as for a binding run, only the verdict is withheld (D4).
            reasons.Add(ReplayGateIneligibleReasons.DiagnosticRun);
        }

        return new ReplayGateMetrics(
            ratedDays,
            count,
            logTotal,
            liveTotal,
            sharedChatLogTotal,
            sharedChatLiveTotal,
            Round(totalDeviation),
            Round(top20Recall),
            topSize,
            top20LiveTieCount,
            top20LogTieCount,
            Round(bottomQuartilePrecision),
            quartileSize,
            quartileLiveSet.Count,
            quartileLogSet.Count,
            liveTieCount,
            logTieCount,
            Round(tailDeviation),
            reasons.Count == 0,
            new ValueList<string>(reasons));
    }

    /// <summary>
    /// Whether the two sides agree about the shared-chat volume of the rated days, within
    /// <see cref="MaxSharedChatAsymmetry"/> of the live figure.
    /// <para>
    /// Three cases, in this order. Both sides zero is symmetric: a channel that never had a single
    /// Stream-Together session satisfies the condition emptily. A live figure of zero while the
    /// replay side saw something is <b>not</b> symmetric — that is precisely the pre-deploy
    /// signature of D3, and it must refuse a run, not pass one for lack of a denominator. Otherwise
    /// the relative difference is measured against the live side, the same denominator the
    /// pre-registered deviation uses.
    /// </para>
    /// </summary>
    private static bool SharedChatSymmetric(long logTotal, long liveTotal)
    {
        if (logTotal == 0 && liveTotal == 0)
        {
            return true;
        }

        if (liveTotal == 0)
        {
            return false;
        }

        return (double)Math.Abs(logTotal - liveTotal) / liveTotal <= MaxSharedChatAsymmetry;
    }

    /// <summary>
    /// The value sitting at position <paramref name="indexFromTop"/> (0-based) of the population
    /// ordered descending by <paramref name="rankedValue"/> — <c>null</c> when the index falls
    /// outside the population (an empty population, or a nominal slice of size 0). Used both for the
    /// bottom-quartile cutoff (<c>indexFromTop = count - quartileSize</c>, the largest value that
    /// would still fall in the nominal quartile) and the top-20 cutoff
    /// (<c>indexFromTop = topSize - 1</c>, the smallest value still inside the nominal top slice).
    /// </summary>
    private static long? BoundaryValue(
        List<PopulationEntry> population, Func<PopulationEntry, long> rankedValue, int indexFromTop)
    {
        if (indexFromTop < 0 || indexFromTop >= population.Count)
        {
            return null;
        }

        return rankedValue(population.OrderByDescending(rankedValue).ElementAt(indexFromTop));
    }

    /// <summary>
    /// How many population entries share the exact value sitting at <paramref name="indexFromTop"/>'s
    /// cut point — the size of the tie block <see cref="Ranking"/>'s ordinal id tie-break would have
    /// to arbitrate at that boundary. Purely descriptive: since #97 it does not change which ids the
    /// bottom-quartile sets contain (<see cref="BuildQuartileSet"/>) and never did for the top-20
    /// slice (Top20Recall keeps <see cref="Ranking"/>'s ordinal tie-break, D-97 leaves the formula
    /// untouched there because exact ties are rare at the qualified channels' four/five-digit
    /// counts).
    /// </summary>
    private static int CountTiesAtBoundary(
        List<PopulationEntry> population, Func<PopulationEntry, long> rankedValue, int indexFromTop)
    {
        var boundaryValue = BoundaryValue(population, rankedValue, indexFromTop);
        return boundaryValue is null ? 0 : population.Count(e => rankedValue(e) == boundaryValue);
    }

    /// <summary>
    /// Every population entry whose <paramref name="rankedValue"/> is at or below
    /// <paramref name="cutoff"/> — the tie-safe replacement for <c>Ranking(...).TakeLast(n)</c> in
    /// the bottom-quartile gate (#97). Because membership is decided purely by value, a tie block
    /// straddling the cutoff is included <b>whole</b> on both sides instead of being split by the
    /// ordinal id tie-break, which is what let the id decide <c>BottomQuartilePrecision</c> instead
    /// of the counts: with the same id used as the secondary sort key on both the live and the log
    /// ranking, a tied entity's id pulled it into or out of <i>both</i> selections together, forcing
    /// the overlap towards its maximum regardless of how the two sides actually compared. Can be
    /// larger than the nominal <c>BottomQuartileSize</c> when a plateau sits on the cutoff; never
    /// smaller. <c>null</c> (an empty population) yields the empty set.
    /// </summary>
    private static List<PopulationEntry> BuildQuartileSet(
        List<PopulationEntry> population, Func<PopulationEntry, long> rankedValue, long? cutoff)
        => cutoff is { } value ? population.Where(e => rankedValue(e) <= value).ToList() : [];

    /// <summary>
    /// Plausibility check (a), over every day that has a log and with bots and shared chat on both
    /// sides (D3, second pair). The field names of <see cref="ReplayPlausibility"/> keep saying
    /// "WithBots" although they carry three components — the report's field names are a frozen
    /// contract; see the remark there.
    /// </summary>
    private static ReplayPlausibility BuildPlausibility(
        IReadOnlyList<ReplayDayLine> days,
        IReadOnlyList<ReplayUsageRow> liveRows,
        HashSet<DateOnly> logDays)
    {
        long log = 0;
        foreach (var line in days.Where(line => logDays.Contains(line.Day)))
        {
            log += Sum(line.HumanCounts) + Sum(line.BotCounts) + Sum(line.SharedChatCounts);
        }

        long live = 0;
        foreach (var row in liveRows.Where(row => logDays.Contains(row.Date)))
        {
            live += row.UseCount + row.BotUseCount + row.SharedChatUseCount;
        }

        return new ReplayPlausibility(log, live, Round(live == 0 ? null : (double?)log / live), log - live);
    }

    private static ReplayDiagnostics BuildDiagnostics(
        ReplayWindow window,
        IReadOnlyList<ReplayEmote> emotes,
        IReadOnlyList<ReplayDayLine> days,
        IReadOnlyList<ReplayUsageRow> liveRows,
        List<DayFact> facts,
        List<PopulationEntry> population,
        IReadOnlySet<string> ambiguousNames,
        HashSet<DateOnly> humanOnlyLogDays,
        double? dayRatioMedian)
    {
        var ratedDays = facts.Count(f => f.Rated);
        var ratedDaySet = DaySet(facts, f => f.Rated);
        var minLiveUses = Math.Max(
            MinLiveUsesFloor,
            (int)Math.Ceiling((double)MinLiveUsesPerThirtyDays * ratedDays / RequiredWindowDays));
        var (dailyDeviation, tolerantDailyDeviation) = DailyDeviations(days, liveRows, ratedDaySet);

        var stableIds = emotes
            .Where(e => IsStable(e, window.From, ambiguousNames))
            .Select(e => e.Id)
            .ToHashSet(StringComparer.Ordinal);

        var qualifiedStable = population.Where(e => e.Live >= minLiveUses && stableIds.Contains(e.Id)).ToList();
        var qualifiedAll = population.Where(e => e.Live >= minLiveUses).ToList();
        var stable = Spread(qualifiedStable);
        var all = Spread(qualifiedAll);

        var logTotal = population.Sum(e => e.Log);
        var liveTotal = population.Sum(e => e.Live);
        var logOnly = population.Where(e => e.Live == 0).ToList();
        var liveOnly = population.Where(e => e.Log == 0).ToList();

        var histogram = new int[Math.Max(MinHistogramLength, days.Count == 0 ? 0 : days.Max(d => d.KHistogram.Count))];
        foreach (var line in days)
        {
            for (var k = 0; k < line.KHistogram.Count; k++)
            {
                histogram[k] += line.KHistogram[k];
            }
        }

        long cellCount = days.Sum(d => (long)d.CellCount);

        return new ReplayDiagnostics(
            stableIds.Count,
            qualifiedStable.Count,
            minLiveUses,
            RequiredQualifiedEmotes,
            qualifiedStable.Count >= RequiredQualifiedEmotes,
            Round(stable.Median),
            Round(stable.P90),
            Round(stable.Spearman),
            stable.ZeroLogCount,
            qualifiedAll.Count,
            Round(all.Median),
            Round(all.P90),
            Round(all.Spearman),
            all.ZeroLogCount,
            logOnly.Count,
            liveOnly.Count,
            Round(logTotal == 0 ? null : (double?)logOnly.Sum(e => e.Log) / logTotal),
            Round(liveTotal == 0 ? null : (double?)liveOnly.Sum(e => e.Live) / liveTotal),
            Round(Deviation(BuildPopulation(days, liveRows, humanOnlyLogDays))),
            humanOnlyLogDays.Count,
            Round(dailyDeviation),
            Round(tolerantDailyDeviation),
            Reason(days, UnmatchedReason.UnknownName),
            Reason(days, UnmatchedReason.AmbiguousName),
            Reason(days, UnmatchedReason.BeforeFirstSeen),
            Reason(days, UnmatchedReason.AfterArchived),
            days.Sum(d => (long)d.FirstSeenUnknownHits),
            ambiguousNames.Count,
            emotes.Count(e => e.IsArchived && e.ArchivedAt is null),
            days.Sum(d => (long)d.MessageCount),
            days.Sum(d => (long)d.BotMessageCount),
            days.Sum(d => (long)d.SharedChatMessageCount),
            days.Sum(d => (long)d.IndeterminateMessageCount),
            days.Sum(d => (long)d.OutsideDayCount),
            days.Sum(d => (long)d.NonPrivmsgLines),
            days.Sum(d => (long)d.MalformedLines),
            Round(cellCount == 0 ? null : (double?)histogram[1] / cellCount),
            new ValueList<int>(histogram),
            cellCount,
            days.Sum(d => (long)d.DistinctChatters),
            facts.Count(f => f.HumanOnly),
            facts.Count(f => f.HasLog),
            facts.Count(f => !f.HasLog),
            facts.Count(f => f is { Rated: true, LogTotal: 0, LiveTotal: 0 }),
            Round(dayRatioMedian),
            Ratios(facts, f => f.LiveGapSuspected),
            Ratios(facts, f => f.CoverageQuestionable),
            SharedChatRatios(facts));
    }

    /// <summary>
    /// The secondary day diff over the rated days, human-only, per (emote, day) cell — once exact
    /// and once with the ±1-day tolerance the design asks for, both divided by the same live total
    /// as the window-sum deviation so the two are readable against each other.
    /// <para>
    /// The tolerance exists because <c>UsageStat.Date</c> is the day of the flush: usage around
    /// midnight lands on the following live day while the log keeps it on its own. The tolerant
    /// figure therefore first settles each day against itself, then lets whatever is left over be
    /// explained by the <b>calendar</b> neighbours (yesterday before tomorrow, both only if they are
    /// rated days themselves); what neither side can explain remains as the deviation. Days,
    /// neighbours and emotes are all walked in a fixed order, so the result is deterministic.
    /// </para>
    /// </summary>
    private static (double? Exact, double? Tolerant) DailyDeviations(
        IReadOnlyList<ReplayDayLine> days,
        IReadOnlyList<ReplayUsageRow> liveRows,
        HashSet<DateOnly> ratedDays)
    {
        var orderedDays = ratedDays.Order().ToList();
        var dayIndex = new Dictionary<DateOnly, int>();
        for (var i = 0; i < orderedDays.Count; i++)
        {
            dayIndex[orderedDays[i]] = i;
        }

        var log = new Dictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var line in days)
        {
            if (!dayIndex.TryGetValue(line.Day, out var index))
            {
                continue;
            }

            foreach (var (emoteId, count) in line.HumanCounts)
            {
                Bucket(log, emoteId, orderedDays.Count)[index] += count;
            }
        }

        var live = new Dictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var row in liveRows)
        {
            if (dayIndex.TryGetValue(row.Date, out var index))
            {
                Bucket(live, row.EmoteId, orderedDays.Count)[index] += row.UseCount;
            }
        }

        var emoteIds = new HashSet<string>(log.Keys, StringComparer.Ordinal);
        emoteIds.UnionWith(live.Keys);

        long liveTotal = 0;
        long exact = 0;
        long tolerant = 0;
        foreach (var emoteId in emoteIds.Order(StringComparer.Ordinal))
        {
            var logDays = log.GetValueOrDefault(emoteId) ?? new long[orderedDays.Count];
            var liveDays = live.GetValueOrDefault(emoteId) ?? new long[orderedDays.Count];
            var openLog = (long[])logDays.Clone();
            var openLive = (long[])liveDays.Clone();

            for (var i = 0; i < orderedDays.Count; i++)
            {
                liveTotal += liveDays[i];
                exact += Math.Abs(logDays[i] - liveDays[i]);
                Settle(openLog, openLive, i, i);
            }

            for (var i = 0; i < orderedDays.Count; i++)
            {
                if (dayIndex.TryGetValue(orderedDays[i].AddDays(-1), out var previous))
                {
                    Settle(openLog, openLive, i, previous);
                }

                if (dayIndex.TryGetValue(orderedDays[i].AddDays(1), out var next))
                {
                    Settle(openLog, openLive, i, next);
                }
            }

            tolerant += openLog.Sum() + openLive.Sum();
        }

        return liveTotal == 0
            ? (null, null)
            : ((double?)exact / liveTotal, (double?)tolerant / liveTotal);
    }

    /// <summary>Books as many log hits of day <paramref name="from"/> against the live count of day <paramref name="against"/> as both sides still have open.</summary>
    private static void Settle(long[] openLog, long[] openLive, int from, int against)
    {
        var settled = Math.Min(openLog[from], openLive[against]);
        openLog[from] -= settled;
        openLive[against] -= settled;
    }

    private static long[] Bucket(Dictionary<string, long[]> buckets, string emoteId, int length)
    {
        if (!buckets.TryGetValue(emoteId, out var bucket))
        {
            buckets[emoteId] = bucket = new long[length];
        }

        return bucket;
    }

    /// <summary>
    /// The stable subset: last synced before the window <b>and</b> either never archived or archived
    /// before the window (the REST resync archives without stamping <c>LastSyncedAt</c>, so the
    /// first condition alone would not do), and not carrying an ambiguous name.
    /// <para>
    /// "Ambiguous" is decided here over the <b>channel-wide</b> coalescence of every emote, not per
    /// day: an emote whose name collided at any point in the channel's life is unstable for the
    /// whole window. That is a different question from the per-hit
    /// <see cref="UnmatchedReason.AmbiguousName"/> marker of <see cref="ReplayDayCounter"/>, which
    /// asks whether the name was ambiguous <i>on that day's map</i> — a name can be ambiguous
    /// channel-wide and unambiguous on a given day, and both readings are correct for their purpose.
    /// </para>
    /// </summary>
    private static bool IsStable(ReplayEmote emote, DateOnly windowFrom, IReadOnlySet<string> ambiguousNames)
        => DateOnly.FromDateTime(emote.LastSyncedAt) < windowFrom
            && (emote.ArchivedAt is not { } archivedAt || DateOnly.FromDateTime(archivedAt) < windowFrom)
            && !ambiguousNames.Contains(emote.Name);

    /// <summary>
    /// Median, p90, Spearman and the count of emotes the log never saw, over one qualified set.
    /// Every entry has <c>Live &gt;= N &gt; 0</c>, so the relative deviation always has a
    /// denominator; an emote the log missed entirely lands at exactly 1.0, i.e. 100 %.
    /// </summary>
    private static SubsetSpread Spread(List<PopulationEntry> entries)
    {
        var deviations = entries
            .Select(e => (double)Math.Abs(e.Log - e.Live) / e.Live)
            .Order()
            .ToList();

        return new SubsetSpread(
            Percentile(deviations, 0.5),
            Percentile(deviations, 0.9),
            SpearmanRho(entries),
            entries.Count(e => e.Log == 0));
    }

    private static double? Deviation(List<PopulationEntry> population)
    {
        var live = population.Sum(e => e.Live);
        return live == 0 ? null : (double?)population.Sum(e => Math.Abs(e.Log - e.Live)) / live;
    }

    /// <summary>
    /// Descending by count, ties broken ordinally by emote id, so both rankings are total orders and
    /// the top and bottom slices are reproducible.
    /// </summary>
    private static List<string> Ranking(List<PopulationEntry> population, Func<PopulationEntry, long> value)
        => population
            .OrderByDescending(value)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => e.Id)
            .ToList();

    private static int Overlap(IEnumerable<string> left, IEnumerable<string> right)
        => left.ToHashSet(StringComparer.Ordinal).Intersect(right, StringComparer.Ordinal).Count();

    /// <summary>
    /// Spearman's rank correlation as Pearson over mid-ranks; <c>null</c> when there are fewer than
    /// two emotes or one side is constant (no variance, hence no correlation to report).
    /// </summary>
    private static double? SpearmanRho(List<PopulationEntry> entries)
    {
        if (entries.Count < 2)
        {
            return null;
        }

        var liveRanks = MidRanks([.. entries.Select(e => e.Live)]);
        var logRanks = MidRanks([.. entries.Select(e => e.Log)]);

        double meanLive = liveRanks.Average();
        double meanLog = logRanks.Average();
        double covariance = 0;
        double liveVariance = 0;
        double logVariance = 0;
        for (var i = 0; i < liveRanks.Length; i++)
        {
            var dx = liveRanks[i] - meanLive;
            var dy = logRanks[i] - meanLog;
            covariance += dx * dy;
            liveVariance += dx * dx;
            logVariance += dy * dy;
        }

        return liveVariance == 0 || logVariance == 0 ? null : covariance / Math.Sqrt(liveVariance * logVariance);
    }

    /// <summary>
    /// Average ranks, with tied values sharing the mean of the ranks they span. Takes the counts as
    /// they are rather than widened to double: the tie test below is an exact comparison, and on
    /// counts that is the point — two emotes tie when they were used the same number of times, never
    /// when they were used a similar number of times.
    /// </summary>
    private static double[] MidRanks(long[] values)
    {
        var order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        var ranks = new double[values.Length];
        var start = 0;
        while (start < order.Length)
        {
            var end = start;
            while (end + 1 < order.Length && values[order[end + 1]] == values[order[start]])
            {
                end++;
            }

            var midRank = ((start + end) / 2.0) + 1;
            for (var i = start; i <= end; i++)
            {
                ranks[order[i]] = midRank;
            }

            start = end + 1;
        }

        return ranks;
    }

    /// <summary>
    /// Nearest-rank percentile over an ascending list: rank ⌈p·n⌉, no interpolation between
    /// neighbours. Interpolating would invent a value that no emote and no day actually had.
    /// </summary>
    private static double? Percentile(List<double> ascending, double p)
    {
        if (ascending.Count == 0)
        {
            return null;
        }

        var rank = Math.Clamp((int)Math.Ceiling(p * ascending.Count), 1, ascending.Count);
        return ascending[rank - 1];
    }

    private static ValueList<ReplayDayRatio> Ratios(List<DayFact> facts, Func<DayFact, bool> predicate)
        => new(facts
            .Where(predicate)
            .OrderBy(f => f.Day)
            .Select(f => new ReplayDayRatio(f.Day, f.LogTotal, f.LiveTotal, Round(f.Ratio))));

    /// <summary>
    /// The shared-chat share of both sides, one entry per day <b>that has a log</b> and ascending by
    /// day — not restricted to the rated days, unlike the two window sums the gate carries, so the
    /// three signatures of D3 stay readable across the cutover. A day without a log has no replay
    /// side to compare against and is left out even when its live rows carry shared chat.
    /// </summary>
    private static ValueList<ReplayDayRatio> SharedChatRatios(List<DayFact> facts)
        => new(facts
            .Where(f => f.HasLog)
            .OrderBy(f => f.Day)
            .Select(f => new ReplayDayRatio(
                f.Day,
                f.SharedChatLogTotal,
                f.SharedChatLiveTotal,
                Round(f.SharedChatLiveTotal == 0 ? null : (double?)f.SharedChatLogTotal / f.SharedChatLiveTotal))));

    private static HashSet<DateOnly> DaySet(List<DayFact> facts, Func<DayFact, bool> predicate)
        => facts.Where(predicate).Select(f => f.Day).ToHashSet();

    private static long Reason(IReadOnlyList<ReplayDayLine> days, UnmatchedReason reason)
    {
        var key = reason.ToString();
        return days.Sum(d => (long)d.UnmatchedByReason.GetValueOrDefault(key));
    }

    private static long Sum(IReadOnlyDictionary<string, int> counts)
    {
        long total = 0;
        foreach (var count in counts.Values)
        {
            total += count;
        }

        return total;
    }

    private static double? Round(double? value)
        => value is null ? null : Math.Round(value.Value, RoundingDigits, MidpointRounding.AwayFromZero);

    private sealed class DayFact
    {
        public DateOnly Day { get; init; }

        public bool HasLog { get; init; }

        public bool HumanOnly { get; init; }

        public long LogTotal { get; init; }

        public long LiveTotal { get; init; }

        public long SharedChatLogTotal { get; init; }

        public long SharedChatLiveTotal { get; init; }

        public double? Ratio { get; init; }

        public bool LiveGapSuspected { get; init; }

        public bool CoverageQuestionable { get; set; }

        public bool Rated { get; set; }
    }

    private sealed record PopulationEntry(string Id, long Live, long Log);

    private sealed record SubsetSpread(double? Median, double? P90, double? Spearman, int ZeroLogCount);
}
