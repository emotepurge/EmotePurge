namespace EmotePurge.Core.Services;

/// <summary>
/// The state-based reconcile of wishes against the slots of one channel (E9, spec 6.3): compares every
/// wish that can change with the current slot inventory and moves it accordingly (link, rename, date
/// confirmation, external removal, return). Idempotent; a second run over the same state changes nothing
/// and announces nothing. It works in a scope and <c>AppDbContext</c> of its own, never on the caller's,
/// so a failed run leaves nothing tracked that a caller's later save would write (plan finding 4).
/// </summary>
public interface IWishReconcileService
{
    /// <summary>
    /// Reconciles the wishes of <paramref name="channelId"/> (the internal channel id). A channel without a
    /// wish that could change costs one query and no transaction. A channel deleted meanwhile is no error.
    /// A change that hits one of the wish unique indexes is rolled back for that wish alone; any other
    /// database error rolls the whole run back and propagates.
    /// </summary>
    Task ReconcileChannelAsync(string channelId, CancellationToken cancellationToken = default);
}
