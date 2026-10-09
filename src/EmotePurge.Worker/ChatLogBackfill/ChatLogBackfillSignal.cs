namespace EmotePurge.Worker.ChatLogBackfill;

/// <summary>
/// A latching, capacity-one wake-up for the backfill loop (spec 4.8, D8): <see cref="EmotePurge.Worker.Worker"/> sets it
/// when a <c>BACKFILL:</c> command arrives, <see cref="ChatLogBackfillWorker"/> waits on it or on its
/// idle poll, whichever comes first. Same shape as <see cref="TwitchReconnectSignalSlot"/> without the
/// generations: the signal carries nothing, so any number of nudges before the next wait collapse into
/// one, and a nudge that arrives while the loop is busy is still there when it next waits.
/// <para>
/// An accelerator only. The command carries no trusted payload and Pub/Sub may drop it; the idle
/// poll is what guarantees a queued run is claimed eventually.
/// </para>
/// </summary>
public sealed class ChatLogBackfillSignal
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
    /// Waits for a deposited wake-up and takes it (<c>true</c>), or gives up after
    /// <paramref name="timeout"/> on <paramref name="timeProvider"/>'s clock (<c>false</c>).
    /// Cancelling <paramref name="cancellationToken"/> throws.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A wake-up already deposited is taken without looking at the clock: a zero timeout must not
        // report "nothing" while one is waiting.
        if (_available.Wait(0, CancellationToken.None))
        {
            return true;
        }

        // The timeout is a token, not a racing Task.Delay: a semaphore wait that lost a WhenAny would
        // stay registered and swallow the next nudge.
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
