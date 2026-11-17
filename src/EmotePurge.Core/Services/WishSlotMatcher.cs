using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>The part of a wish the matcher reads.</summary>
public sealed record WishMatchInput(
    string Status,
    string SevenTvEmoteId,
    string? Alias,
    DateTime? ApprovedAtUtc,
    DateTime? EndedAtUtc);

/// <summary>An admissible slot of the channel (the caller filters archived, unsettled and undated slots).</summary>
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

/// <summary>
/// The one rule that links a wish without a slot to a slot (spec 6.3, E20), pure. One rule per state;
/// K6 adds <c>pending</c> through its own window functions without touching the two existing rules.
/// The caller passes only admissible slots and has already removed those linked to other running wishes.
/// </summary>
public static class WishSlotMatcher
{
    /// <summary>Start of the window for <c>approved</c>: the approval, no tolerance (AK7).</summary>
    public static DateTime? ApprovedWindowStart(WishMatchInput wish) => wish.ApprovedAtUtc;

    /// <summary>The <c>approved</c> window is unbounded.</summary>
    public static DateTime? ApprovedWindowEnd(WishMatchInput wish) => null;

    /// <summary>Start of the window for <c>removed_externally</c>: the end minus the look-back (plan finding 6).</summary>
    public static DateTime? RemovedExternallyWindowStart(WishMatchInput wish) =>
        wish.EndedAtUtc - WishLimits.ReturnLookBack;

    /// <summary>End of the window for <c>removed_externally</c>: the end plus the adoption window, tied to the replacement's <c>AddedAt</c>.</summary>
    public static DateTime? RemovedExternallyWindowEnd(WishMatchInput wish) =>
        wish.EndedAtUtc + WishLimits.AdoptionWindow;

    /// <summary>Window start per state; null when the state has no rule or its date is missing.</summary>
    public static DateTime? WindowStart(WishMatchInput wish) => wish.Status switch
    {
        WishStatus.Approved => ApprovedWindowStart(wish),
        WishStatus.RemovedExternally => RemovedExternallyWindowStart(wish),
        _ => null,
    };

    /// <summary>Window end per state; null means unbounded (or no rule).</summary>
    public static DateTime? WindowEnd(WishMatchInput wish) => wish.Status switch
    {
        WishStatus.Approved => ApprovedWindowEnd(wish),
        WishStatus.RemovedExternally => RemovedExternallyWindowEnd(wish),
        _ => null,
    };

    public static WishMatchResult Match(WishMatchInput wish, IReadOnlyCollection<WishSlotCandidate> candidates) =>
        wish.Status switch
        {
            WishStatus.Approved => MatchApproved(wish, candidates),
            WishStatus.RemovedExternally => MatchRemovedExternally(wish, candidates),
            _ => WishMatchResult.None(),
        };

    private static WishMatchResult MatchApproved(WishMatchInput wish, IReadOnlyCollection<WishSlotCandidate> candidates)
    {
        var start = ApprovedWindowStart(wish);
        if (start is null)
        {
            return WishMatchResult.None();
        }

        var inWindow = candidates
            .Where(c => string.Equals(c.SevenTvEmoteId, wish.SevenTvEmoteId, StringComparison.Ordinal) && c.AddedAt >= start)
            .ToList();

        if (inWindow.Count == 0)
        {
            return WishMatchResult.None();
        }

        if (inWindow.Count == 1)
        {
            return WishMatchResult.Matched(inWindow[0].SlotId);
        }

        if (string.IsNullOrEmpty(wish.Alias))
        {
            return WishMatchResult.Ambiguous();
        }

        var byAlias = inWindow.Where(c => string.Equals(c.Alias, wish.Alias, StringComparison.Ordinal)).ToList();
        return byAlias.Count == 1 ? WishMatchResult.Matched(byAlias[0].SlotId) : WishMatchResult.Ambiguous();
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
