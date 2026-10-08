using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The broadcaster re-add lock (#245): a numeric Twitch broadcaster id in this table may not be
/// joined, reactivated or observed again until a global admin lifts it with a join. Separate from
/// <see cref="IExcludedChannelFilter"/> (the operator's GDPR Art. 21 list from configuration):
/// that list is immutable at runtime, this table is written by the self-service purge. The two
/// mechanisms are independent and the env list wins wherever both apply. The table has no retention.
/// </summary>
/// <remarks>
/// The service shares the caller's scoped <c>AppDbContext</c>, so its write methods run inside the
/// caller's transaction. They only stage changes in the change tracker and never call
/// <c>SaveChanges</c>: the caller saves and commits. A new caller that saves in between would
/// build half a transaction. Only <c>PurgeByBroadcasterAsync</c> writes a lock; only a join by a
/// global admin removes one.
/// </remarks>
public interface IBroadcasterChannelLockService
{
    /// <summary>
    /// Read-only query surface for SQL anti-joins (the active-channel roster). Deliberately not an
    /// in-memory set: the table changes at runtime, so a cache in front of it would reopen the gap
    /// the lock closes.
    /// </summary>
    IQueryable<BroadcasterChannelLock> Locks { get; }

    /// <summary>
    /// The moment the id was locked, or <c>null</c> when it is not locked. A <c>null</c> or empty id
    /// is never locked.
    /// </summary>
    Task<DateTime?> GetLockedAtUtcAsync(string? twitchChannelId, CancellationToken cancellationToken = default);

    /// <summary>Upsert: an existing row gets the new date. Stages only; the caller saves.</summary>
    Task LockAsync(string twitchChannelId, DateTime lockedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the removal and returns <c>true</c> when a row existed; idempotent. The caller saves.
    /// </summary>
    Task<bool> UnlockAsync(string twitchChannelId, CancellationToken cancellationToken = default);
}
