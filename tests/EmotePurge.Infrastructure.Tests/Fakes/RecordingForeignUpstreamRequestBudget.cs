using EmotePurge.Infrastructure.SevenTv;

namespace EmotePurge.Infrastructure.Tests.Fakes;

/// <summary>
/// Counts request permits instead of enforcing them — the seam that makes "one permit per upstream
/// request" (spec E5b) observable, since the real
/// <see cref="ForeignEmoteSetProviderBudget"/> only ever answers yes until a whole minute's budget is
/// gone. Same shape as <see cref="RecordingRateLimitTelemetry"/>: a recorder by default, and a
/// refuser when a test needs one.
/// </summary>
/// <param name="grantCount">
/// How many permits to grant before refusing every further one. Defaults to "always" — a test that
/// never sets it gets a budget that is purely an observer.
/// </param>
public sealed class RecordingForeignUpstreamRequestBudget(int grantCount = int.MaxValue) : IForeignUpstreamRequestBudget
{
    private readonly Lock _gate = new();
    private readonly List<(int Reserve, TimeSpan? Timeout)> _requests = [];
    private int _charges;

    /// <summary>How often a permit was asked for, granted or not.</summary>
    public int Charges => Volatile.Read(ref _charges);

    /// <summary>
    /// The reserve and wait of every permit asked for, in order; <c>Timeout</c> is <c>null</c> for
    /// the no-argument overload (reserve 0, the budget's default wait).
    /// </summary>
    public IReadOnlyList<(int Reserve, TimeSpan? Timeout)> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public Task<bool> TryChargeRequestAsync(CancellationToken cancellationToken = default) => Charge(0, null);

    public Task<bool> TryChargeRequestAsync(int reserve, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Charge(reserve, timeout);

    private Task<bool> Charge(int reserve, TimeSpan? timeout)
    {
        lock (_gate)
        {
            _requests.Add((reserve, timeout));
        }

        return Task.FromResult(Interlocked.Increment(ref _charges) <= grantCount);
    }
}
