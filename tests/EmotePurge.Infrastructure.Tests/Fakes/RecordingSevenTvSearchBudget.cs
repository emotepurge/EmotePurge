using System.Collections.Concurrent;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Tests.Fakes;

/// <summary>
/// An <see cref="ISevenTvSearchBudget"/> that grants every permit and records what it was asked and
/// told — unless a test sets <see cref="NextRefusal"/>, in which case every charge is refused with it.
/// Same shape as <see cref="RecordingForeignUpstreamRequestBudget"/>: an observer by default, a
/// refuser on demand. The real budget's window and block logic is tested against Redis itself.
/// </summary>
public sealed class RecordingSevenTvSearchBudget : ISevenTvSearchBudget
{
    private readonly ConcurrentQueue<SevenTvSearchConsumer> _charges = new();
    private readonly ConcurrentQueue<SevenTvSearchObservation> _observations = new();

    /// <summary>When not <see cref="SevenTvSearchRefusal.None"/>, every charge is refused with this.</summary>
    public SevenTvSearchRefusal NextRefusal { get; set; } = SevenTvSearchRefusal.None;

    /// <summary>The remaining block a <see cref="SevenTvSearchRefusal.Blocked"/> refusal reports.</summary>
    public TimeSpan? BlockedFor { get; set; }

    /// <summary>The cause a <see cref="SevenTvSearchRefusal.Blocked"/> refusal reports.</summary>
    public SevenTvSearchBlockCause BlockCause { get; set; } = SevenTvSearchBlockCause.RateLimited;

    /// <summary>Every consumer that asked for a permit, granted or not, in order.</summary>
    public IReadOnlyList<SevenTvSearchConsumer> Charges => [.. _charges];

    /// <summary>Every observation the client reported, in order.</summary>
    public IReadOnlyList<SevenTvSearchObservation> Observations => [.. _observations];

    public Task<SevenTvSearchPermit> TryChargeAsync(SevenTvSearchConsumer consumer, CancellationToken cancellationToken = default)
    {
        _charges.Enqueue(consumer);
        return Task.FromResult(NextRefusal == SevenTvSearchRefusal.None
            ? new SevenTvSearchPermit(SevenTvSearchRefusal.None, _charges.Count)
            : NextRefusal == SevenTvSearchRefusal.Blocked
                ? new SevenTvSearchPermit(NextRefusal, 0, BlockedFor, BlockCause)
                : new SevenTvSearchPermit(NextRefusal, 0));
    }

    public Task ObserveResponseAsync(SevenTvSearchObservation observation, CancellationToken cancellationToken = default)
    {
        _observations.Enqueue(observation);
        return Task.CompletedTask;
    }
}
