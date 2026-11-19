using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker.WishCapture;

/// <summary>
/// Where the chat handler hands a <see cref="RewardObservation"/> over (plan #486 2.1). Called inline from
/// TwitchLib's read loop: it never waits, never throws and never touches the database.
/// </summary>
public interface IRewardObservationSink
{
    /// <summary>
    /// Takes the observation, or refuses it when the queue is full or closed; a refusal is counted in
    /// <see cref="WorkerStats"/>, never thrown.
    /// </summary>
    /// <returns><c>true</c> when the observation is queued.</returns>
    bool TryEnqueue(RewardObservation observation);
}

/// <summary>
/// The bounded in-memory queue between the chat handler (any number of writers: TwitchLib's read loop, and briefly
/// two of them around a rebuild, #114) and the single consumer in the capture worker (plan 2.2, 2.3). A worker
/// singleton, registered both as itself (the read side) and as <see cref="IRewardObservationSink"/> (the write side).
/// <para>
/// Bounded at <see cref="Capacity"/>. The channel runs in <see cref="BoundedChannelFullMode.Wait"/> and is only ever
/// written with <c>TryWrite</c>, which answers <c>false</c> when it is full: the drop is visible and counted.
/// <see cref="BoundedChannelFullMode.DropWrite"/> would be the wrong mode, because its <c>TryWrite</c> reports
/// success for the item it drops. <c>AllowSynchronousContinuations</c> stays off, so a write never runs the waiting
/// consumer's continuation on the read loop's thread.
/// </para>
/// <para>
/// Closable (Sol-R2-P2-8): <see cref="Complete"/> is the first thing the capture worker does when it stops; from
/// then on <see cref="TryEnqueue"/> refuses and counts ("rejected at shutdown"), and what was queued before can
/// still be read.
/// </para>
/// </summary>
public sealed class RewardObservationQueue(WorkerStats stats) : IRewardObservationSink
{
    /// <summary>The most observations the queue holds (spec K6: 1,000, drop and count beyond).</summary>
    public const int Capacity = 1000;

    private readonly Channel<RewardObservation> _channel = Channel.CreateBounded<RewardObservation>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    private int _closed;

    /// <summary>Whether <see cref="Complete"/> has run; the queue accepts nothing any more.</summary>
    public bool IsCompleted => Volatile.Read(ref _closed) == 1;

    /// <summary>Observations queued and not yet read.</summary>
    public int Count => _channel.Reader.Count;

    /// <inheritdoc />
    public bool TryEnqueue(RewardObservation observation)
    {
        if (!IsCompleted && _channel.Writer.TryWrite(observation))
        {
            return true;
        }

        // Read the flag again after the failed write: a close that landed in between is a shutdown refusal, not an
        // overflow.
        if (IsCompleted)
        {
            stats.RecordRewardObservationRejectedAtShutdown();
        }
        else
        {
            stats.RecordRewardObservationOverflow();
        }

        return false;
    }

    /// <summary>
    /// Closes the queue for writers; idempotent. Queued observations stay readable until
    /// <see cref="TryDequeue"/> has taken them; <see cref="WaitToReadAsync"/> then answers <c>false</c>.
    /// </summary>
    public void Complete()
    {
        Volatile.Write(ref _closed, 1);
        _channel.Writer.TryComplete();
    }

    /// <summary>Takes the oldest queued observation, if there is one; never waits.</summary>
    public bool TryDequeue([MaybeNullWhen(false)] out RewardObservation observation) =>
        _channel.Reader.TryRead(out observation);

    /// <summary>
    /// Waits until an observation can be read (<c>true</c>) or the queue is closed and empty (<c>false</c>).
    /// </summary>
    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);
}
