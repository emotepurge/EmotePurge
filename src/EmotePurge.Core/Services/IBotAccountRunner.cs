namespace EmotePurge.Core.Services;

/// <summary>
/// The account-wide part of the bot account's loop (plan #487 2.5, revision 5a): what every user of the account shares.
/// One instance per loop, resolved once by the worker's <c>BotAccountWorker</c> and used by it alone; it is not
/// thread-safe and owns the dedicated connection that holds the advisory lock, which its disposal closes.
/// <para>
/// One pass, in this order: (0) the wishes' deadline step (<see cref="IWishBotPass.RunDeadlineStepAsync"/>), before and
/// without the lock; then the lock (taken, or probed when already held; every new acquisition raises
/// <c>RunnerEpoch</c> on the lock's own connection); (1) the token upkeep (plan 2.8); (2 to 5) the wishes' part
/// (<see cref="IWishBotPass.RunAsync"/>) with an <see cref="IBotAccountSession"/>. Without the lock the pass ends after
/// step 0: no 7TV call, no write.
/// </para>
/// </summary>
public interface IBotAccountRunner : IAsyncDisposable
{
    /// <summary>
    /// Runs one pass. Never throws for what 7TV answers; a database failure or the caller's cancellation does.
    /// </summary>
    Task<BotAccountPassResult> RunPassAsync(CancellationToken cancellationToken = default);
}

/// <summary>How one pass of <see cref="IBotAccountRunner"/> ended.</summary>
public enum BotAccountPassResult
{
    /// <summary>The pass ran under the lock to its end (whatever there was to do).</summary>
    Completed,

    /// <summary>Another instance holds the lock: only the deadline step ran.</summary>
    NoLock,

    /// <summary>The lock or the runner epoch was lost during the pass; it stopped without booking anything after that.</summary>
    LockLost,
}
