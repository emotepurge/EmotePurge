using EmotePurge.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// The channel-scoped lookups the services need, in one place. Deliberately not a generic
/// repository — just the exact shapes that were being copied by hand.
/// <para>
/// The reason this matters beyond duplication: <c>s.ChannelId == channel.Id</c> is the *only* thing
/// stopping channel A from ending, deleting or voting on a session belonging to channel B. It was
/// written out six times, and only one of those six was covered by a test.
/// </para>
/// <para>
/// The second reason is Regel 9: every lookup has to filter on the *normalized* name. Nine call
/// sites outside the vote-session services were still normalizing by hand — which worked, but meant
/// nine chances for the next one to forget.
/// </para>
/// </summary>
internal static class ChannelQueries
{
    /// <summary>
    /// Loads a channel by its (un-normalized) name. Tracked — callers that mutate what they load
    /// depend on it. Use <see cref="LoadChannelReadOnlyAsync"/> for pure reads.
    /// </summary>
    public static Task<Channel?> LoadChannelAsync(this AppDbContext db, string channelName, CancellationToken cancellationToken)
    {
        var normalized = ChannelName.Normalize(channelName);
        return db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
    }

    /// <summary>
    /// Untracked counterpart for read-only paths. Separate method rather than a bool parameter, so
    /// the call site says which one it is without the reader checking an argument.
    /// </summary>
    public static Task<Channel?> LoadChannelReadOnlyAsync(this AppDbContext db, string channelName, CancellationToken cancellationToken)
    {
        var normalized = ChannelName.Normalize(channelName);
        return db.Channels.AsNoTracking().SingleOrDefaultAsync(c => c.ChannelName == normalized, cancellationToken);
    }

    /// <summary>
    /// Loads a channel and one of *its* vote sessions. Returns <c>(null, null)</c> for an unknown
    /// channel and <c>(channel, null)</c> when the session exists but belongs to a different channel —
    /// callers must treat the latter as "not found" and never as "found but foreign".
    /// </summary>
    public static async Task<(Channel? Channel, VoteSession? Session)> LoadChannelSessionAsync(
        this AppDbContext db, string channelName, long sessionId, CancellationToken cancellationToken)
    {
        var channel = await db.LoadChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return (null, null);
        }

        var session = await db.VoteSessions.SingleOrDefaultAsync(
            s => s.Id == sessionId && s.ChannelId == channel.Id, cancellationToken);
        return (channel, session);
    }

    /// <summary>
    /// Loads a channel by its Twitch id: once a caller has a Twitch id in hand, it is looking for
    /// the *channel*, not for whatever name it currently answers to, and a rename can leave a
    /// second row under a new name sharing that same id. Tracked, since the callers that need this
    /// (rename reconciliation) mutate what they find. Twitch ids are opaque digit strings, never
    /// <see cref="ChannelName.Normalize"/>d.
    /// </summary>
    public static Task<Channel?> LoadChannelByTwitchIdAsync(
        this AppDbContext db, string twitchChannelId, CancellationToken cancellationToken) =>
        db.Channels.SingleOrDefaultAsync(c => c.TwitchChannelId == twitchChannelId, cancellationToken);

    /// <summary>
    /// Untracked counterpart of <see cref="LoadChannelByTwitchIdAsync"/>, for a caller that only
    /// decides from what it reads and locks the row later if it acts (the identity merge).
    /// </summary>
    public static Task<Channel?> LoadChannelByTwitchIdReadOnlyAsync(
        this AppDbContext db, string twitchChannelId, CancellationToken cancellationToken) =>
        db.Channels.AsNoTracking().SingleOrDefaultAsync(c => c.TwitchChannelId == twitchChannelId, cancellationToken);

    /// <summary>
    /// Loads a channel by its (un-normalized) name and locks the row with <c>SELECT … FOR UPDATE</c>
    /// until the surrounding transaction ends — for every path where activating a row can race the
    /// retention purge deleting it (data-retention plan, "Zeilensperren statt Hoffnung"): the join,
    /// the purge itself and the identity merge. Returns <c>null</c> when no row holds the name,
    /// including when a purge that held the lock committed while this call waited: under READ
    /// COMMITTED Postgres re-checks a row it had to wait for and skips it when it is gone or no longer
    /// matches. Tracked, like <see cref="LoadChannelAsync"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No transaction is open, or the row was already tracked with pending changes (see
    /// <see cref="LockAsync"/>).
    /// </exception>
    public static Task<Channel?> LoadChannelForUpdateAsync(
        this AppDbContext db, string channelName, CancellationToken cancellationToken)
    {
        var normalized = ChannelName.Normalize(channelName);
        return db.LockAsync(
            db.Channels.FromSql($"""SELECT * FROM "Channels" WHERE "ChannelName" = {normalized} FOR UPDATE"""),
            cancellationToken);
    }

    /// <summary>
    /// <see cref="LoadChannelForUpdateAsync"/> by Twitch id. Where a caller locks two rows, it locks the
    /// row holding the Twitch id first and the row holding the name second — the join's rename path
    /// and the identity merge both do, so the two can never wait on each other in a cycle.
    /// </summary>
    public static Task<Channel?> LoadChannelByTwitchIdForUpdateAsync(
        this AppDbContext db, string twitchChannelId, CancellationToken cancellationToken) =>
        db.LockAsync(
            db.Channels.FromSql($"""SELECT * FROM "Channels" WHERE "TwitchChannelId" = {twitchChannelId} FOR UPDATE"""),
            cancellationToken);

    /// <summary>
    /// Runs a locking query and hands back its single row, with two guards the lock is worthless
    /// without.
    /// <list type="bullet">
    /// <item>An open transaction: outside one, the autocommit statement would release the lock the
    /// moment it returns — it would look like a guard and guard nothing (same rule as
    /// <see cref="UserQueries.LockUserAsync"/>).</item>
    /// <item>A fresh read: a tracking query hands back an instance the context already tracks
    /// <em>without</em> overwriting its values, so a row read before the lock would come back with
    /// exactly the stale state the lock exists to rule out. Such an instance is reloaded — the lock is
    /// held now, so the reload reads the current version — and one carrying unsaved changes is
    /// refused, since reloading would silently throw those away.</item>
    /// </list>
    /// </summary>
    private static async Task<Channel?> LockAsync(
        this AppDbContext db, IQueryable<Channel> lockingQuery, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A channel row lock needs an open transaction; it would be released immediately otherwise.");
        }

        var trackedBefore = db.ChangeTracker.Entries<Channel>()
            .Select(entry => entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        // Materialised with ToListAsync and nothing composed on top: EF would otherwise wrap the
        // statement in a subquery, and a locking clause is best kept on the outermost SELECT.
        var rows = await lockingQuery.ToListAsync(cancellationToken);
        var channel = rows.SingleOrDefault();
        if (channel is null || !trackedBefore.Contains(channel))
        {
            return channel;
        }

        var entry = db.Entry(channel);
        if (entry.State != EntityState.Unchanged)
        {
            throw new InvalidOperationException("A channel row was changed before it was locked; lock it first, then change it.");
        }

        await entry.ReloadAsync(cancellationToken);
        return channel;
    }
}
