using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// <see cref="IWishBotPass"/>, the wishes' steps of the bot account's pass (plan #487 2.5). A frame for now: the deadline
/// step (step 0) and steps 2 to 5 are filled by later tasks of child 4 (T8: activation, channel states, fallback run;
/// T9 and T10: the deadline step, adding, confirming and removing). Registered scoped by the worker's
/// <c>AddBotAccountWriter</c> next to the runner, so it shares the runner's loop-long scope.
/// </summary>
public sealed class WishBotPass : IWishBotPass
{
    public Task RunDeadlineStepAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RunAsync(IBotAccountSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Task.CompletedTask;
    }
}
