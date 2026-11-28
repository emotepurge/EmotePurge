namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// One generation of a removal order (#487 plan 2.7 "Festsitzender Auftrag", Revision 6f K6f-2): the wish, and the
/// queue's version and instant as last read. A new <c>bot-remove</c>, a new queueing by the expiry run or a cleared and
/// re-queued order is another generation.
/// </summary>
internal readonly record struct WishBotRemovalGeneration(long WishId, int? QueuedVersion, DateTime? QueuedAtUtc);

/// <summary>
/// The in-memory count of removal orders that are stuck (#487 plan 2.7, Revision 6e K6e-5, 6f K6f-2, K6f-3): aborts
/// without a send, at a read or at a deadline, in passes in a row, per order generation. After
/// <see cref="AbortsToPresent"/> of them, the first at least <see cref="FirstAbortAge"/> ago, the order is due to be
/// presented. Unauthorized, RateLimited and a "no" of the preflight are no aborts; every other result of an executed
/// attempt ends the count (<see cref="Reset"/>). No column: every count belongs to the runner epoch it was made under and
/// is dropped as soon as a session carries another one (a restart, a change of runner, the same instance taking the lock
/// again). Pure apart from its own state; one instance per wish part, so per loop.
/// </summary>
internal sealed class WishBotStuckRemovals
{
    /// <summary>Aborts in a row before an order is presented.</summary>
    public const int AbortsToPresent = 3;

    /// <summary>How long ago the first abort of the row must be at least.</summary>
    public static readonly TimeSpan FirstAbortAge = TimeSpan.FromMinutes(15);

    private readonly Dictionary<long, Count> _counts = [];
    private long? _epoch;

    /// <summary>The number of aborts counted for this generation (0 for another one).</summary>
    public int AbortsOf(WishBotRemovalGeneration generation) =>
        _counts.TryGetValue(generation.WishId, out var count) && count.Generation == generation ? count.Aborts : 0;

    /// <summary>
    /// Binds the counts to <paramref name="runnerEpoch"/> (another epoch drops every count) and keeps only the wishes still
    /// queued (<paramref name="queuedWishIds"/>).
    /// </summary>
    public void Retain(long runnerEpoch, IEnumerable<long> queuedWishIds)
    {
        ArgumentNullException.ThrowIfNull(queuedWishIds);
        Bind(runnerEpoch);
        var keep = queuedWishIds.ToHashSet();
        foreach (var wishId in _counts.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            _counts.Remove(wishId);
        }
    }

    /// <summary>
    /// Counts one abort of <paramref name="generation"/> under <paramref name="runnerEpoch"/> at <paramref name="nowUtc"/>;
    /// a count of another generation of the wish starts again at this one. True when the order is now due to be presented.
    /// </summary>
    public bool Abort(long runnerEpoch, WishBotRemovalGeneration generation, DateTime nowUtc)
    {
        Bind(runnerEpoch);
        var count = _counts.TryGetValue(generation.WishId, out var known) && known.Generation == generation
            ? known with { Aborts = known.Aborts + 1 }
            : new Count(generation, 1, nowUtc);
        _counts[generation.WishId] = count;
        return count.Aborts >= AbortsToPresent && nowUtc - count.FirstAbortAtUtc >= FirstAbortAge;
    }

    /// <summary>Ends the count of the wish (any other result, a presentation, or one that hit nothing).</summary>
    public void Reset(long wishId) => _counts.Remove(wishId);

    private void Bind(long runnerEpoch)
    {
        if (_epoch != runnerEpoch)
        {
            _counts.Clear();
            _epoch = runnerEpoch;
        }
    }

    private sealed record Count(WishBotRemovalGeneration Generation, int Aborts, DateTime FirstAbortAtUtc);
}
