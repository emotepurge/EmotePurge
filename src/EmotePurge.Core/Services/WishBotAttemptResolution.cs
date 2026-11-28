using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>
/// "Resolution without 7TV" of an open bot attempt (plan #487 2.5a (e), Revision 6b K6b-11), pure: the one place that
/// says what an open attempt becomes when nobody may read 7TV for it. <c>DELETE …/wishes/bot</c>, the fallback run and
/// the deadline step call it on a wish row they hold locked; the lock, the commit, the decision version and
/// <c>wish.changed</c> stay with the caller.
/// <list type="bullet">
/// <item>An add whose outcome is unknown (<c>approved</c> with <c>BotWriteAtUtc</c>, or <c>unconfirmed</c>) becomes
/// <c>presented</c> (<c>unconfirmed</c>). <c>BotSetId</c>, <c>BotWriteAtUtc</c> and <c>BotAddAckAtUtc</c> stay, so
/// <c>recheck</c> can read the attempt's generation in the attempt's set; the confirmation schedule ends.</item>
/// <item>An open removal attempt (<c>BotRemoveAtUtc</c>) makes a running wish <c>presented</c> (<c>bot_unavailable</c>);
/// attempt and queue are cleared. Its evidence stays and becomes a repair check phase (<c>BotRepairAtUtc</c>, 2.5a (h)),
/// resolved later by a read of the evidence's own set only; without evidence there is nothing to check.</item>
/// <item>Anything else changes nothing; an open repair phase on its own is left as it is.</item>
/// </list>
/// </summary>
public static class WishBotAttemptResolution
{
    /// <summary>Applies the resolution to <paramref name="wish"/>; true when a field changed.</summary>
    public static bool Apply(Wish wish, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(wish);

        if (HasOpenAdd(wish))
        {
            Present(wish, WishStatusReason.Unconfirmed, nowUtc);
            wish.BotNextAttemptAtUtc = null;
            wish.BotConfirmDeadlineAtUtc = null;
            return true;
        }

        if (wish.BotRemoveAtUtc is null)
        {
            return false;
        }

        if (wish.Status is WishStatus.Active or WishStatus.Presented)
        {
            Present(wish, WishStatusReason.BotUnavailable, nowUtc);
        }

        wish.BotRemoveAtUtc = null;
        wish.BotAttemptVersion = null;
        wish.RemovalQueuedAtUtc = null;
        wish.RemovalQueuedVersion = null;
        if (wish.BotRemoveEvidence is not null)
        {
            wish.BotRepairAtUtc ??= nowUtc;
        }

        return true;
    }

    /// <summary>An add whose outcome is not known: the intent is committed, or the write is being confirmed.</summary>
    public static bool HasOpenAdd(Wish wish)
    {
        ArgumentNullException.ThrowIfNull(wish);
        return (string.Equals(wish.Status, WishStatus.Approved, StringComparison.Ordinal) && wish.BotWriteAtUtc is not null)
            || string.Equals(wish.Status, WishStatus.Unconfirmed, StringComparison.Ordinal);
    }

    private static void Present(Wish wish, string reason, DateTime nowUtc)
    {
        if (!string.Equals(wish.Status, WishStatus.Presented, StringComparison.Ordinal)
            || !string.Equals(wish.StatusReason, reason, StringComparison.Ordinal))
        {
            wish.StatusChangedAtUtc = nowUtc;
        }

        wish.Status = WishStatus.Presented;
        wish.StatusReason = reason;
    }
}
