namespace EmotePurge.Core.Services;

/// <summary>What one item of the bot's derived queue asks for (#487 plan 2.5 "Warteschlange").</summary>
public enum WishBotQueueKind
{
    /// <summary>An open repair check phase (<c>BotRepairAtUtc</c> with evidence, 2.5a (h)), read only, in any channel state. T10.</summary>
    RepairCheck,

    /// <summary>An open removal attempt (<c>BotRemoveAtUtc</c>, 2.5a (e)) in a bot channel. T10.</summary>
    RemovalAttempt,

    /// <summary><c>approved</c> in a bot channel, a crash remnant with <c>BotWriteAtUtc</c> included (2.6). T9.</summary>
    Add,

    /// <summary><c>unconfirmed</c> with <c>BotNextAttemptAtUtc</c> due (2.6 point 5). T9.</summary>
    Confirm,

    /// <summary><c>active</c> or <c>presented</c> with <c>RemovalQueuedAtUtc</c> in a bot channel (2.7). T10.</summary>
    Removal,
}

/// <summary>
/// One item of the derived queue: the wish, its channel, and the instant it is ordered by (the approval, the last write,
/// or the queueing). Derived without a lock; the handler locks and re-reads, and a row that is gone or changed by then
/// is "nothing to do" (K6b-17).
/// </summary>
public sealed record WishBotQueueItem(WishBotQueueKind Kind, long WishId, string ChannelId, DateTime OrderAtUtc);

/// <summary>
/// The order of the bot's derived queue (#487 plan 2.5, step 5), pure: first the open repair check phases, then the open
/// removal attempts, then the rest (adds, due confirmations, queued removals) together; within each tier the oldest
/// first by <see cref="WishBotQueueItem.OrderAtUtc"/>, then by wish id. The pass works it off one action at a time within
/// the action budget (<see cref="IBotAccountSession.TryTakeAction"/>, 50); the rest waits for the next pass.
/// </summary>
public static class WishBotQueue
{
    /// <summary>The items in the order the pass works them off.</summary>
    public static IReadOnlyList<WishBotQueueItem> Order(IEnumerable<WishBotQueueItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items
            .OrderBy(i => TierOf(i.Kind))
            .ThenBy(i => i.OrderAtUtc)
            .ThenBy(i => i.WishId)
            .ToList();
    }

    /// <summary>0 for repair checks, 1 for open removal attempts, 2 for everything else.</summary>
    public static int TierOf(WishBotQueueKind kind) => kind switch
    {
        WishBotQueueKind.RepairCheck => 0,
        WishBotQueueKind.RemovalAttempt => 1,
        _ => 2,
    };
}
