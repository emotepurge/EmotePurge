using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>
/// The fields of a released automatic link (K6 <c>unlink</c>, spec K6 and plan #486 2.6), in one place for the
/// manager's command and the reconcile's date correction (plan 2.5). The caller has asked
/// <see cref="WishTransitions"/> already; this only writes the target state's fields.
/// </summary>
public static class WishUnlinkReset
{
    /// <summary>
    /// Back to <c>pending</c>: state, reason and stamp; slot, start, expiry, origin, ownership and queue cleared;
    /// the generation fields too (W9); extensions to 0, the decision counted. Kept: duration, creation,
    /// <c>EndedAtUtc</c> (null), requester data, <c>FirstActivatedAtUtc</c>. The emote fields go back to the
    /// placeholder of plan 2.8a: all three for a name wish, alias and name for a link wish, whose id stays.
    /// </summary>
    public static void ToPending(Wish wish, DateTime now, string? reason)
    {
        ArgumentNullException.ThrowIfNull(wish);

        wish.Status = WishStatus.Pending;
        wish.StatusReason = reason;
        wish.StatusChangedAtUtc = now;
        wish.SlotId = null;
        wish.StartsAtUtc = null;
        wish.StartsAtAuthoritative = false;
        wish.ExpiresAtUtc = null;
        wish.LinkOrigin = null;
        wish.BotOwned = false;
        wish.BotOwnedAddedAtUtc = null;
        wish.RemovalQueuedAtUtc = null;
        wish.RemovalQueuedVersion = null;
        wish.SlotAddedAtUtc = null;
        wish.SlotConfirmedAtUtc = null;
        wish.ExtensionCount = 0;
        wish.DecisionVersion++;

        if (wish.MatchKind == WishMatchKind.Name)
        {
            wish.SevenTvEmoteId = string.Empty;
        }

        wish.EmoteName = string.Empty;
        wish.Alias = string.Empty;
    }
}
