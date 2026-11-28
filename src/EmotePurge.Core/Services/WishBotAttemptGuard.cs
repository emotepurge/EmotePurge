using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>
/// "An open attempt blocks decisions" (plan #487 2.5a (b), [W21], R2-1), pure. Between committing a bot write and its
/// outcome no manager command may change the wish: every mutating path asks here under the wish's row lock and answers
/// 409 <c>wish_state_conflict</c>; the threshold change skips such a wish and the reconcile leaves it out of steps 3 and
/// 4. Only <c>DELETE …/wishes/bot</c>, the fallback run and the deadline step resolve an open attempt (without 7TV). An
/// open repair phase (<c>BotRepairAtUtc</c>) blocks nothing here; only the hard delete checks it on its own.
/// </summary>
public static class WishBotAttemptGuard
{
    /// <summary>True while an add intent is committed (<c>approved</c> with <c>BotWriteAtUtc</c>) or a removal attempt is.</summary>
    public static bool Blocks(Wish wish)
    {
        ArgumentNullException.ThrowIfNull(wish);
        return Blocks(wish.Status, wish.BotWriteAtUtc, wish.BotRemoveAtUtc);
    }

    /// <summary>The same rule on the bare columns.</summary>
    public static bool Blocks(string status, DateTime? botWriteAtUtc, DateTime? botRemoveAtUtc) =>
        (string.Equals(status, WishStatus.Approved, StringComparison.Ordinal) && botWriteAtUtc is not null)
        || botRemoveAtUtc is not null;
}
