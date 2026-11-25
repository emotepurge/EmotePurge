namespace EmotePurge.Core.Services;

/// <summary>
/// The wishes' part of one pass of <see cref="IBotAccountRunner"/> (plan #487 2.5, revision 5a). The runner owns lock,
/// epoch, token upkeep, budget and write spacing; this part owns the wish steps. There is deliberately no general
/// mechanism for further users of the account: a later one gets its own call in the same loop.
/// </summary>
public interface IWishBotPass
{
    /// <summary>
    /// Step 0, the deadline step (plan 2.5, R2-7): every pass, before the lock is tried, local only (no token, no 7TV,
    /// no pause, no budget). <c>unconfirmed</c> past its confirmation deadline goes to <c>presented</c>, an open removal
    /// attempt older than 15 minutes is resolved without 7TV (plan 2.5a (e)).
    /// </summary>
    Task RunDeadlineStepAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Steps 2 to 5 under the lock: activation requests, channel states, the fallback run and the derived queue (plan
    /// 2.5). Runs whatever the token state; <paramref name="session"/> says what the account can do in this pass.
    /// </summary>
    Task RunAsync(IBotAccountSession session, CancellationToken cancellationToken = default);
}
