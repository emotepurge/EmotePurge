using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>
/// What triggers a transition. The strings are internal vocabulary of <see cref="WishTransitions"/>
/// (not persisted, not on the wire); services pass one of these and ask for the target state.
/// Some actions have no caller in K1: they belong to K2 (<see cref="Present"/>), K4 (the Bot* actions,
/// <see cref="Recheck"/>) and K6 (<see cref="Capture"/>, <see cref="Approve"/>, <see cref="Reject"/>,
/// <see cref="Expire"/>, <see cref="MergeIntoRunning"/>, <see cref="Unlink"/>); the table is complete
/// from the start so no child has to touch it again.
/// </summary>
public static class WishAction
{
    // Creation (no source state).
    public const string Create = "create";
    public const string AdoptNew = "adopt_new";
    public const string Capture = "capture";

    // Manager commands. Edit is the PATCH of alias and duration: a self-transition that only names the
    // states in which the wish may still be edited.
    public const string Edit = "edit";
    public const string Adopt = "adopt";
    public const string Extend = "extend";
    public const string Keep = "keep";
    public const string Close = "close";
    public const string Retry = "retry";
    public const string ReAdd = "readd";
    public const string Remove = "remove";
    public const string Recheck = "recheck";
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Unlink = "unlink";

    // Reconcile and workers.
    public const string Link = "link";
    public const string Return = "return";
    public const string ExternalRemoval = "external_removal";
    public const string Present = "present";
    public const string Expire = "expire";
    public const string MergeIntoRunning = "merge_into_running";
    public const string MergeChannels = "merge_channels";
    public const string BotWrite = "bot_write";
    public const string BotReject = "bot_reject";
    public const string BotAlreadyInSet = "bot_already_in_set";
    public const string BotConfirm = "bot_confirm";
    public const string BotUnconfirmed = "bot_unconfirmed";

    public static readonly IReadOnlyList<string> All =
    [
        Create, AdoptNew, Capture, Edit, Adopt, Extend, Keep, Close, Retry, ReAdd, Remove, Recheck, Approve, Reject, Unlink,
        Link, Return, ExternalRemoval, Present, Expire, MergeIntoRunning, MergeChannels,
        BotWrite, BotReject, BotAlreadyInSet, BotConfirm, BotUnconfirmed,
    ];
}

/// <summary>
/// The facts beyond state and reason that a few rows depend on. They are parameters of the query, not a
/// second table (plan 2.2).
/// </summary>
/// <param name="HasSlot">The wish is linked to a slot; <c>close</c> from <c>presented</c> needs this false.</param>
/// <param name="Permanent">The wish is permanent; linking and the bot's confirmation then end in <c>kept</c>.</param>
/// <param name="HadPresentedReason">For <c>return</c>: a reason stood when the wish was removed externally, so it comes back as <c>presented</c>.</param>
/// <param name="IrcAutoLinked">For <c>unlink</c>: source <c>irc</c> and link origin <c>auto</c>.</param>
/// <param name="HasExpiry">
/// The wish has an expiry; <c>extend</c> needs it. A wish without one (for example <c>presented</c> with reason
/// <c>unconfirmed</c> or <c>already_in_set</c>) changes its duration through <c>PATCH</c>, not by extending (spec 5).
/// </param>
public readonly record struct WishTransitionContext(
    bool HasSlot = false,
    bool Permanent = false,
    bool HadPresentedReason = false,
    bool IrcAutoLinked = false,
    bool HasExpiry = false);

/// <summary>
/// The one place that knows which wish transitions exist: (state, reason, action) to target state, or
/// "not allowed". Services ask it, handlers never decide. Pure, no I/O. The rows follow the lifecycle
/// table of the wish emotes spec, section 5, including the rows only later children trigger.
/// </summary>
/// <remarks>
/// Window and ownership checks that depend on time or on other rows (the 6-hour adoption window of
/// <c>removed_externally</c>, bot ownership) are the callers' business; the table answers only what the
/// state machine allows. The reason only matters for <c>presented</c> (<c>recheck</c> needs
/// <c>unconfirmed</c>, <c>adopt</c> needs <c>unconfirmed</c> or <c>already_in_set</c>); for every other
/// state it is ignored.
/// </remarks>
public static class WishTransitions
{
    private static readonly string[] AdoptablePresentedReasons =
        [WishStatusReason.Unconfirmed, WishStatusReason.AlreadyInSet];

    private static readonly string[] RecheckablePresentedReasons = [WishStatusReason.Unconfirmed];

    // The reconcile's date correction (K6, plan #486 2.5) releases an automatic capture link also after the
    // expiry run presented it for its provisional start.
    private static readonly string[] UnlinkablePresentedReasons = [WishStatusReason.UnconfirmedStart];

    private static readonly Row[] Rows = BuildRows();

    /// <summary>
    /// Target state for a wish that does not exist yet (<see cref="WishAction.Create"/>,
    /// <see cref="WishAction.AdoptNew"/>, <see cref="WishAction.Capture"/>), or null.
    /// </summary>
    public static string? TargetForNew(string action) => action switch
    {
        WishAction.Create => WishStatus.Approved,
        WishAction.AdoptNew => WishStatus.Active,
        WishAction.Capture => WishStatus.Pending,
        _ => null,
    };

    /// <summary>The target state of <paramref name="action"/> on an existing wish, or null when it is not allowed.</summary>
    public static string? Target(string status, string? reason, string action, WishTransitionContext context = default)
    {
        foreach (var row in Rows)
        {
            if (row.Matches(status, reason, action, context))
            {
                return row.To(context);
            }
        }

        return null;
    }

    public static bool IsAllowed(string status, string? reason, string action, WishTransitionContext context = default) =>
        Target(status, reason, action, context) is not null;

    private static Row[] BuildRows()
    {
        var rows = new List<Row>();

        void Add(string from, string action, string to, string[]? reasons = null, Func<WishTransitionContext, bool>? when = null) =>
            rows.Add(new Row(from, reasons, action, _ => to, when));

        // "Take over from the set", with or without an existing wish: ends in kept for a permanent wish.
        void AddActiveOrKept(string from, string action, string[]? reasons = null)
        {
            rows.Add(new Row(from, reasons, action, _ => WishStatus.Kept, c => c.Permanent));
            rows.Add(new Row(from, reasons, action, _ => WishStatus.Active, c => !c.Permanent));
        }

        // Adopt with wishId and the reconcile's link.
        AddActiveOrKept(WishStatus.Pending, WishAction.Adopt);
        AddActiveOrKept(WishStatus.Approved, WishAction.Adopt);
        AddActiveOrKept(WishStatus.Failed, WishAction.Adopt);
        AddActiveOrKept(WishStatus.Presented, WishAction.Adopt, AdoptablePresentedReasons);
        AddActiveOrKept(WishStatus.Pending, WishAction.Link);
        AddActiveOrKept(WishStatus.Approved, WishAction.Link);

        // Back from an external removal: the state before, with the old deadline.
        rows.Add(new Row(WishStatus.RemovedExternally, null, WishAction.Return,
            c => c.HadPresentedReason ? WishStatus.Presented : WishStatus.Active, null));

        // PATCH of alias and duration: only before anything was written to 7TV (approved, failed; pending from
        // K6 on). Not in unconfirmed, because the bot has written already.
        Add(WishStatus.Pending, WishAction.Edit, WishStatus.Pending);
        Add(WishStatus.Approved, WishAction.Edit, WishStatus.Approved);
        Add(WishStatus.Failed, WishAction.Edit, WishStatus.Failed);

        // Pending (K6).
        Add(WishStatus.Pending, WishAction.Approve, WishStatus.Approved);
        Add(WishStatus.Pending, WishAction.Reject, WishStatus.Rejected);
        Add(WishStatus.Pending, WishAction.Expire, WishStatus.Expired);
        Add(WishStatus.Pending, WishAction.MergeIntoRunning, WishStatus.Merged);

        // Channel merge: the younger of two running wishes.
        foreach (var running in WishStatus.Running)
        {
            Add(running, WishAction.MergeChannels, WishStatus.Merged);
        }

        // Approved (bot, K4) and close.
        Add(WishStatus.Approved, WishAction.BotWrite, WishStatus.Unconfirmed);
        Add(WishStatus.Approved, WishAction.BotReject, WishStatus.Failed);
        Add(WishStatus.Approved, WishAction.BotAlreadyInSet, WishStatus.Presented);
        Add(WishStatus.Approved, WishAction.Close, WishStatus.Closed);

        // Unconfirmed (bot, K4). A confirmed add ends in kept for a permanent wish, like the reconcile's link (plan #487
        // 2.1 point 1): kept carries no ownership and no expiry.
        AddActiveOrKept(WishStatus.Unconfirmed, WishAction.BotConfirm);
        Add(WishStatus.Unconfirmed, WishAction.BotUnconfirmed, WishStatus.Presented);

        // Failed.
        Add(WishStatus.Failed, WishAction.Retry, WishStatus.Approved);
        Add(WishStatus.Failed, WishAction.Close, WishStatus.Closed);

        // Active.
        Add(WishStatus.Active, WishAction.Extend, WishStatus.Active, when: c => c.HasExpiry);
        Add(WishStatus.Active, WishAction.Unlink, WishStatus.Pending, when: c => c.IrcAutoLinked);
        Add(WishStatus.Active, WishAction.Keep, WishStatus.Kept);
        Add(WishStatus.Active, WishAction.Remove, WishStatus.Removed);
        Add(WishStatus.Active, WishAction.Present, WishStatus.Presented);
        Add(WishStatus.Active, WishAction.ExternalRemoval, WishStatus.RemovedExternally);

        // Presented.
        Add(WishStatus.Presented, WishAction.Extend, WishStatus.Active, when: c => c.HasExpiry);
        Add(WishStatus.Presented, WishAction.Keep, WishStatus.Kept);
        Add(WishStatus.Presented, WishAction.Remove, WishStatus.Removed);
        Add(WishStatus.Presented, WishAction.Close, WishStatus.Closed, when: c => !c.HasSlot);
        Add(WishStatus.Presented, WishAction.Recheck, WishStatus.Unconfirmed, RecheckablePresentedReasons);
        Add(WishStatus.Presented, WishAction.Unlink, WishStatus.Pending, UnlinkablePresentedReasons, c => c.IrcAutoLinked);
        Add(WishStatus.Presented, WishAction.ExternalRemoval, WishStatus.RemovedExternally);

        // Removed states. The 6-hour window of removed_externally -> removed is the caller's check.
        Add(WishStatus.RemovedExternally, WishAction.Remove, WishStatus.Removed);
        Add(WishStatus.Removed, WishAction.ReAdd, WishStatus.Approved);
        Add(WishStatus.RemovedExternally, WishAction.ReAdd, WishStatus.Approved);

        return [.. rows];
    }

    private sealed record Row(
        string From,
        string[]? Reasons,
        string Action,
        Func<WishTransitionContext, string> To,
        Func<WishTransitionContext, bool>? When)
    {
        public bool Matches(string status, string? reason, string action, WishTransitionContext context) =>
            string.Equals(From, status, StringComparison.Ordinal)
            && string.Equals(Action, action, StringComparison.Ordinal)
            && (Reasons is null || (reason is not null && Array.IndexOf(Reasons, reason) >= 0))
            && (When is null || When(context));
    }
}
