using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>The part of a wish the matcher reads.</summary>
/// <param name="CreatedAtUtc">Start of the <c>pending</c> window, and of the <c>approved</c> window of a captured wish never active (K6, W11).</param>
/// <param name="MatchKind">What the parser read from a captured text (<see cref="WishMatchKind"/>); null for other wishes.</param>
/// <param name="RequestedName">The name a <c>name</c> wish asks for; compared with the slot's alias only.</param>
/// <param name="PendingWindowEnd">End of the <c>pending</c> window: the nominal expiry (<see cref="WishSlotMatcher.PendingWindowEndFor"/>).</param>
/// <param name="FirstActivatedAtUtc">Set once the wish was active; a captured wish that never was gets the earlier <c>approved</c> window.</param>
/// <param name="Source">One of <see cref="WishSource"/>.</param>
public sealed record WishMatchInput(
    string Status,
    string SevenTvEmoteId,
    string? Alias,
    DateTime? ApprovedAtUtc,
    DateTime? EndedAtUtc,
    DateTime? CreatedAtUtc = null,
    string? MatchKind = null,
    string? RequestedName = null,
    DateTime? PendingWindowEnd = null,
    DateTime? FirstActivatedAtUtc = null,
    string? Source = null);

/// <summary>
/// An admissible slot of the channel (the caller filters archived, unsettled and undated slots, and the slots
/// excluded for this wish in <c>WishSlotExclusions</c>).
/// </summary>
public sealed record WishSlotCandidate(string SlotId, string SevenTvEmoteId, string Alias, DateTime AddedAt);

/// <summary>Outcome of <see cref="WishSlotMatcher.Match"/>: exactly one slot, or no match with a reason.</summary>
public sealed record WishMatchResult(string? SlotId, string? NoMatchReason)
{
    public const string ReasonNone = "none";
    public const string ReasonAmbiguous = "ambiguous";

    public bool IsMatch => SlotId is not null;

    public static WishMatchResult Matched(string slotId) => new(slotId, null);

    public static WishMatchResult None() => new(null, ReasonNone);

    public static WishMatchResult Ambiguous() => new(null, ReasonAmbiguous);
}

/// <summary>What the reconcile does with an open wish: link it to <see cref="SlotId"/>, or keep it with <see cref="Hint"/>.</summary>
public sealed record WishPendingDecision(string? SlotId, string? Hint)
{
    public bool IsLink => SlotId is not null;
}

/// <summary>
/// The one rule that links a wish without a slot to a slot (spec 6.3, E20), pure. One rule per state;
/// K6 adds <c>pending</c> through its own window functions and a match by name, and moves the start of the
/// <c>approved</c> window for captured wishes (W11); the <c>removed_externally</c> rule is untouched.
/// The caller passes only admissible slots and has already removed those held by other wishes (a running
/// link or a kept one).
/// </summary>
public static class WishSlotMatcher
{
    /// <summary>
    /// Start of the window for <c>approved</c>: the approval, no tolerance (AK7). A captured wish that was never
    /// active starts at the earlier of approval and redemption (plan #486 2.5, W11): the manager adds the emote in
    /// 7TV and approves before the next sync, so the slot lies seconds before the approval.
    /// </summary>
    public static DateTime? ApprovedWindowStart(WishMatchInput wish) =>
        wish is { Source: WishSource.Irc, FirstActivatedAtUtc: null, CreatedAtUtc: { } created, ApprovedAtUtc: { } approved }
            && created < approved
            ? created
            : wish.ApprovedAtUtc;

    /// <summary>The <c>approved</c> window is unbounded.</summary>
    public static DateTime? ApprovedWindowEnd(WishMatchInput wish) => null;

    /// <summary>Start of the window for <c>removed_externally</c>: the end minus the look-back (plan finding 6).</summary>
    public static DateTime? RemovedExternallyWindowStart(WishMatchInput wish) =>
        wish.EndedAtUtc - WishLimits.ReturnLookBack;

    /// <summary>End of the window for <c>removed_externally</c>: the end plus the adoption window, tied to the replacement's <c>AddedAt</c>.</summary>
    public static DateTime? RemovedExternallyWindowEnd(WishMatchInput wish) =>
        wish.EndedAtUtc + WishLimits.AdoptionWindow;

    /// <summary>Start of the window for <c>pending</c>: the redemption, no tolerance (AK57).</summary>
    public static DateTime? PendingWindowStart(WishMatchInput wish) => wish.CreatedAtUtc;

    /// <summary>
    /// End of the window for <c>pending</c>: the nominal expiry, so a slot added after it is no candidate even
    /// while the housekeeping has not expired the wish yet (AK57).
    /// </summary>
    public static DateTime? PendingWindowEnd(WishMatchInput wish) => wish.PendingWindowEnd;

    /// <summary>The nominal expiry of an open wish: its last state change plus the channel's <c>PendingExpiryDays</c>.</summary>
    public static DateTime PendingWindowEndFor(DateTime statusChangedAtUtc, int pendingExpiryDays) =>
        statusChangedAtUtc.AddDays(pendingExpiryDays);

    /// <summary>Window start per state; null when the state has no rule or its date is missing.</summary>
    public static DateTime? WindowStart(WishMatchInput wish) => wish.Status switch
    {
        WishStatus.Pending => PendingWindowStart(wish),
        WishStatus.Approved => ApprovedWindowStart(wish),
        WishStatus.RemovedExternally => RemovedExternallyWindowStart(wish),
        _ => null,
    };

    /// <summary>Window end per state; null means unbounded (or no rule).</summary>
    public static DateTime? WindowEnd(WishMatchInput wish) => wish.Status switch
    {
        WishStatus.Pending => PendingWindowEnd(wish),
        WishStatus.Approved => ApprovedWindowEnd(wish),
        WishStatus.RemovedExternally => RemovedExternallyWindowEnd(wish),
        _ => null,
    };

    public static WishMatchResult Match(WishMatchInput wish, IReadOnlyCollection<WishSlotCandidate> candidates) =>
        wish.Status switch
        {
            WishStatus.Pending => MatchPending(wish, candidates),
            WishStatus.Approved => MatchApproved(wish, candidates),
            WishStatus.RemovedExternally => MatchRemovedExternally(wish, candidates),
            _ => WishMatchResult.None(),
        };

    /// <summary>
    /// The reconcile's decision for an open wish (plan #486 2.5): a link, or the hint that stands in its
    /// <c>StatusReason</c>, by rank: <c>unparsed</c>, <c>ambiguous_match</c>, <c>already_running</c>,
    /// <c>already_in_set</c>, none.
    /// </summary>
    /// <param name="wish">A <c>pending</c> wish.</param>
    /// <param name="free">Admissible slots held by no other wish: the candidates.</param>
    /// <param name="visible">
    /// Admissible slots including those held by other wishes (never the excluded ones): a slot of a name wish in the
    /// window, or the one older slot its name points at unambiguously, shows its emote running when a wish of that
    /// emote runs; any other slot before the window start shows the emote already in the set.
    /// </param>
    /// <param name="isEmoteRunning">Whether another wish of the channel runs for this 7TV emote id.</param>
    public static WishPendingDecision DecidePending(
        WishMatchInput wish,
        IReadOnlyCollection<WishSlotCandidate> free,
        IReadOnlyCollection<WishSlotCandidate> visible,
        Func<string, bool> isEmoteRunning)
    {
        var byId = wish.SevenTvEmoteId.Length > 0;
        var byName = !byId && wish.MatchKind == WishMatchKind.Name && !string.IsNullOrEmpty(wish.RequestedName);
        if (wish.MatchKind == WishMatchKind.None || (!byId && !byName))
        {
            return new WishPendingDecision(null, WishStatusReason.Unparsed);
        }

        var result = MatchPending(wish, free);
        if (result.NoMatchReason == WishMatchResult.ReasonAmbiguous)
        {
            return new WishPendingDecision(null, WishStatusReason.AmbiguousMatch);
        }

        if (result.SlotId is { } slotId)
        {
            // A link next to a running wish of the emote would hit the running index: no link, the hint instead.
            var hit = free.First(c => string.Equals(c.SlotId, slotId, StringComparison.Ordinal));
            return isEmoteRunning(hit.SevenTvEmoteId)
                ? new WishPendingDecision(null, WishStatusReason.AlreadyRunning)
                : new WishPendingDecision(slotId, null);
        }

        var start = PendingWindowStart(wish);
        var end = PendingWindowEnd(wish);
        var matching = visible.Where(c => byId
                ? string.Equals(c.SevenTvEmoteId, wish.SevenTvEmoteId, StringComparison.Ordinal)
                : string.Equals(c.Alias, wish.RequestedName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if ((byId && isEmoteRunning(wish.SevenTvEmoteId))
            || (byName && matching.Any(c => InWindow(c, start, end) && isEmoteRunning(c.SevenTvEmoteId)))
            || (byName && OlderNameHit(matching, start, wish.RequestedName!) is { } older && isEmoteRunning(older.SevenTvEmoteId)))
        {
            return new WishPendingDecision(null, WishStatusReason.AlreadyRunning);
        }

        return start is { } from && matching.Any(c => c.AddedAt < from)
            ? new WishPendingDecision(null, WishStatusReason.AlreadyInSet)
            : new WishPendingDecision(null, null);
    }

    // By emote id like the approved rule; by name the slot's alias, ordinal first, then case-insensitive, and
    // any ambiguity is no match (plan #486 2.5). The name is never compared with any wish's EmoteName.
    private static WishMatchResult MatchPending(WishMatchInput wish, IReadOnlyCollection<WishSlotCandidate> candidates)
    {
        var start = PendingWindowStart(wish);
        var end = PendingWindowEnd(wish);
        if (start is null || end is null || wish.MatchKind == WishMatchKind.None)
        {
            return WishMatchResult.None();
        }

        var inWindow = candidates.Where(c => InWindow(c, start, end)).ToList();
        if (wish.SevenTvEmoteId.Length > 0)
        {
            return ById(inWindow, wish.SevenTvEmoteId, wish.Alias);
        }

        if (wish.MatchKind != WishMatchKind.Name || string.IsNullOrEmpty(wish.RequestedName))
        {
            return WishMatchResult.None();
        }

        var hits = NameHits(inWindow, wish.RequestedName);
        return hits.Count switch
        {
            0 => WishMatchResult.None(),
            1 => WishMatchResult.Matched(hits[0].SlotId),
            _ => WishMatchResult.Ambiguous(),
        };
    }

    // The slots whose alias is the name: the ordinal hits, and only without one the case-insensitive ones.
    private static List<WishSlotCandidate> NameHits(IEnumerable<WishSlotCandidate> slots, string name)
    {
        var all = slots.Where(c => string.Equals(c.Alias, name, StringComparison.OrdinalIgnoreCase)).ToList();
        var ordinal = all.Where(c => string.Equals(c.Alias, name, StringComparison.Ordinal)).ToList();
        return ordinal.Count > 0 ? ordinal : all;
    }

    // The one slot older than the redemption the name points at, by the same rule as a match; null when none or
    // ambiguous. Only for the hint (Sol R2): a running wish of its emote makes it "already running", so approve books
    // the extension. Such a slot is never a candidate, so the hint links nothing (Sol-P1-3, live step B5a).
    private static WishSlotCandidate? OlderNameHit(IEnumerable<WishSlotCandidate> slots, DateTime? start, string name)
    {
        if (start is not { } from)
        {
            return null;
        }

        var hits = NameHits(slots.Where(c => c.AddedAt < from), name);
        return hits.Count == 1 ? hits[0] : null;
    }

    private static bool InWindow(WishSlotCandidate candidate, DateTime? start, DateTime? end) =>
        candidate.AddedAt >= start && (end is null || candidate.AddedAt <= end);

    // One candidate of the emote; of several the one carrying the wish's alias; else ambiguous.
    private static WishMatchResult ById(List<WishSlotCandidate> inWindow, string sevenTvEmoteId, string? alias)
    {
        var ofEmote = inWindow.Where(c => string.Equals(c.SevenTvEmoteId, sevenTvEmoteId, StringComparison.Ordinal)).ToList();
        if (ofEmote.Count == 0)
        {
            return WishMatchResult.None();
        }

        if (ofEmote.Count == 1)
        {
            return WishMatchResult.Matched(ofEmote[0].SlotId);
        }

        if (string.IsNullOrEmpty(alias))
        {
            return WishMatchResult.Ambiguous();
        }

        var byAlias = ofEmote.Where(c => string.Equals(c.Alias, alias, StringComparison.Ordinal)).ToList();
        return byAlias.Count == 1 ? WishMatchResult.Matched(byAlias[0].SlotId) : WishMatchResult.Ambiguous();
    }

    private static WishMatchResult MatchApproved(WishMatchInput wish, IReadOnlyCollection<WishSlotCandidate> candidates)
    {
        var start = ApprovedWindowStart(wish);
        if (start is null)
        {
            return WishMatchResult.None();
        }

        return ById([.. candidates.Where(c => c.AddedAt >= start)], wish.SevenTvEmoteId, wish.Alias);
    }

    private static WishMatchResult MatchRemovedExternally(WishMatchInput wish, IReadOnlyCollection<WishSlotCandidate> candidates)
    {
        var start = RemovedExternallyWindowStart(wish);
        var end = RemovedExternallyWindowEnd(wish);
        if (start is null || end is null || string.IsNullOrEmpty(wish.Alias))
        {
            return WishMatchResult.None();
        }

        var hits = candidates
            .Where(c => string.Equals(c.SevenTvEmoteId, wish.SevenTvEmoteId, StringComparison.Ordinal)
                && string.Equals(c.Alias, wish.Alias, StringComparison.Ordinal)
                && c.AddedAt >= start
                && c.AddedAt <= end)
            .ToList();

        return hits.Count switch
        {
            0 => WishMatchResult.None(),
            1 => WishMatchResult.Matched(hits[0].SlotId),
            _ => WishMatchResult.Ambiguous(),
        };
    }
}
