namespace EmotePurge.Core.Services;

/// <summary>
/// One pass of the expiry run: reconciles the channels that have a wish due, then decides every due
/// <c>active</c> wish (present it with a reason, or queue its bot removal). Writes no 7TV action.
/// </summary>
public interface IWishExpiryService
{
    /// <summary>
    /// Runs once. A failure on one wish or channel rolls back only that unit, is counted and logged, and the
    /// run goes on; cancellation at <paramref name="cancellationToken"/> propagates.
    /// </summary>
    Task<WishExpiryRunResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>Plain counts of one expiry run (no identifiers, no names).</summary>
/// <param name="PresentedByReason">Presented wishes per <c>WishStatusReason</c> value.</param>
public sealed record WishExpiryRunResult(
    int ReconciledChannels,
    int FailedReconciles,
    IReadOnlyDictionary<string, int> PresentedByReason,
    int Queued,
    int HeldBack,
    int Skipped,
    int Failed)
{
    public int Presented => PresentedByReason.Values.Sum();

    public static WishExpiryRunResult Empty { get; } =
        new(0, 0, new Dictionary<string, int>(), 0, 0, 0, 0);
}
