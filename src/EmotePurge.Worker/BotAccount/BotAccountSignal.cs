namespace EmotePurge.Worker.BotAccount;

/// <summary>
/// A latching, capacity-one wake-up for the bot account loop (plan #487 2.5): <see cref="EmotePurge.Worker.Worker"/> sets
/// it when a <c>BOTACCOUNT:</c> command arrives (token set or deleted, kill switch changed), <see cref="BotAccountWorker"/>
/// waits on it or on its 30-second cadence, whichever comes first. Same shape as
/// <see cref="ChatLogBackfill.ChatLogBackfillSignal"/>: the signal carries nothing, any number of nudges before the next
/// wait collapse into one, and a nudge that arrives while a pass runs is still there when the loop next waits.
/// <para>
/// An accelerator only. The command carries no payload and Pub/Sub may drop it; the credential row is the truth and the
/// cadence finds every change anyway.
/// </para>
/// </summary>
public sealed class BotAccountSignal
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _available = new(0, 1);

    /// <summary>Deposits the wake-up unless one is already waiting to be taken.</summary>
    public void Set()
    {
        lock (_gate)
        {
            if (_available.CurrentCount == 0)
            {
                _available.Release();
            }
        }
    }

    /// <summary>
    /// Waits for a deposited wake-up and takes it (<c>true</c>), or gives up after <paramref name="timeout"/> on
    /// <paramref name="timeProvider"/>'s clock (<c>false</c>). Cancelling <paramref name="cancellationToken"/> throws.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A wake-up already deposited is taken without looking at the clock.
        if (_available.Wait(0, CancellationToken.None))
        {
            return true;
        }

        // The timeout is a token, not a racing Task.Delay: a semaphore wait that lost a WhenAny would stay registered and
        // swallow the next nudge.
        using var timeoutSource = new CancellationTokenSource(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await _available.WaitAsync(linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
