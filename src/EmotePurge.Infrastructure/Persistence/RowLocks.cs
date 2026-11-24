using EmotePurge.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// The row-lock convention of the slot work (spec §6.5, D28/D32, plan #372 E1/E3/E6): every writer that
/// resolves emotes of a channel takes its locks through these entries, so two of them always meet in
/// the same order and wait instead of deadlocking.
/// <list type="bullet">
/// <item><b>Mode <c>FOR NO KEY UPDATE</c></b>, a documented deviation from the spec's <c>FOR UPDATE</c>
/// (operator decision 2026-10-12): it excludes itself and <c>FOR UPDATE</c>, so every authoritative
/// resolution of one emote still serializes, but it does not block the <c>FOR KEY SHARE</c> that a
/// foreign-key insert (usage flush, backfill block replace, ballot rows) takes on the parent. Same
/// precedent as <see cref="ChannelQueries.LockChannelByIdAsync"/>.</item>
/// <item><b>One statement per entry, sorted by <c>Id</c></b> with <c>COLLATE "C"</c>, so the order is
/// the ordinal one whatever the database collation is. Postgres locks the rows in the order the
/// sorted plan returns them.</item>
/// <item><b>Ids out, no entities.</b> Nothing is tracked or reloaded: a caller that holds a tracked
/// <see cref="Channel"/> or <see cref="Emote"/> keeps it exactly as it is (the counterpart of
/// <c>ChannelQueries.LockAsync</c>, which reloads a tracked instance and refuses a changed one). The
/// caller reads the rows itself afterwards, behind the lock.</item>
/// <item><b>An open transaction</b> is required, as for every row lock: outside one the statement's
/// autocommit would release the lock the moment it returns.</item>
/// </list>
/// Across tables the order is always <c>Channels</c>, then <c>Emotes</c>, then
/// <c>EmoteSetLeaveObservations</c>; the wish emotes work (<see cref="LockWishesByIdsAsync"/>) takes the channel row
/// before the <c>Wishes</c> rows, and the wish rows by <c>Id</c> ascending; deleting a wish (K7a) takes its row only within
/// the channel it locked (<see cref="LockChannelWishesByIdsAsync"/>), and the wishes merged into it after that row without
/// waiting (<see cref="LockChannelWishesByIdsNoWaitAsync"/>). The one shared entry, <see cref="LockEmotesForShareByIdsAsync"/>
/// (plan #373 E11a), sits in the <c>Emotes</c> position of that order like every other emote entry.
/// </summary>
internal static class RowLocks
{
    /// <summary>
    /// Locks the <c>Emotes</c> rows with the given ids (any channels) and returns the ids that were
    /// found and locked, in lock order. An id missing from the result does not exist (any more).
    /// </summary>
    public static async Task<IReadOnlyList<string>> LockEmotesByIdsAsync(
        this AppDbContext db, IReadOnlyCollection<string> emoteIds, CancellationToken cancellationToken)
    {
        RequireTransaction(db);
        if (emoteIds.Count == 0)
        {
            return [];
        }

        var ids = emoteIds.Distinct(StringComparer.Ordinal).ToArray();
        return await db.Database
            .SqlQuery<string>($"""SELECT "Id" AS "Value" FROM "Emotes" WHERE "Id" = ANY({ids}) ORDER BY "Id" COLLATE "C" FOR NO KEY UPDATE""")
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Locks the <c>Emotes</c> rows with the given ids <c>FOR SHARE</c> and returns the ids found and
    /// locked, in lock order (plan #373 E11a). The reader's entry: the backfill block replace takes it
    /// after its channel <c>KEY SHARE</c> and before it reads the snapshot's slot ids, so it serializes
    /// with every writer that moves rows or snapshot entries of those emotes under
    /// <c>FOR NO KEY UPDATE</c> (the settle, the enqueue): whichever comes first, the
    /// other waits. Two share lockers do not wait for each other, and the <c>FOR KEY SHARE</c> of a
    /// foreign-key insert is not blocked. Same order rules as the exclusive entries (sorted with
    /// <c>COLLATE "C"</c>, one statement, open transaction required).
    /// </summary>
    public static async Task<IReadOnlyList<string>> LockEmotesForShareByIdsAsync(
        this AppDbContext db, IReadOnlyCollection<string> emoteIds, CancellationToken cancellationToken)
    {
        RequireTransaction(db);
        if (emoteIds.Count == 0)
        {
            return [];
        }

        var ids = emoteIds.Distinct(StringComparer.Ordinal).ToArray();
        return await db.Database
            .SqlQuery<string>($"""SELECT "Id" AS "Value" FROM "Emotes" WHERE "Id" = ANY({ids}) ORDER BY "Id" COLLATE "C" FOR SHARE""")
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Locks, for the REST reconcile of one channel, every <c>Emotes</c> row the reconcile can write
    /// (E3): the rows whose 7TV id is in <paramref name="liveSevenTvEmoteIds"/>, the rows that are
    /// active, and the rows that own an active slot of their own (not an unsettled seed). Returns the
    /// locked ids in lock order. <see cref="IsReconcileTarget"/> is the same predicate for the
    /// coverage check on the re-read state.
    /// </summary>
    public static async Task<IReadOnlyList<string>> LockEmotesForReconcileAsync(
        this AppDbContext db,
        string channelId,
        IReadOnlyCollection<string> liveSevenTvEmoteIds,
        CancellationToken cancellationToken)
    {
        RequireTransaction(db);
        var live = liveSevenTvEmoteIds.Distinct(StringComparer.Ordinal).ToArray();
        return await db.Database
            .SqlQuery<string>($"""
                SELECT e."Id" AS "Value" FROM "Emotes" e
                WHERE e."ChannelId" = {channelId}
                  AND (e."SevenTvEmoteId" = ANY({live})
                       OR NOT e."IsArchived"
                       OR EXISTS (SELECT 1 FROM "EmoteSlots" s
                                  WHERE s."EmoteId" = e."Id" AND NOT s."IsArchived" AND NOT s."SeedUnsettled"))
                ORDER BY e."Id" COLLATE "C"
                FOR NO KEY UPDATE OF e
                """)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Locks the <c>Channels</c> rows with the given ids and returns the ids found, in lock order. A
    /// requested id that is not returned has been deleted (a purge committed meanwhile). Loads and
    /// reloads no <see cref="Channel"/>, so a tracked, already changed instance stays as it is.
    /// </summary>
    public static async Task<IReadOnlyList<string>> LockChannelsByIdsAsync(
        this AppDbContext db, IReadOnlyCollection<string> channelIds, CancellationToken cancellationToken)
    {
        RequireTransaction(db);
        if (channelIds.Count == 0)
        {
            return [];
        }

        var ids = channelIds.Distinct(StringComparer.Ordinal).ToArray();
        return await db.Database
            .SqlQuery<string>($"""SELECT "Id" AS "Value" FROM "Channels" WHERE "Id" = ANY({ids}) ORDER BY "Id" COLLATE "C" FOR NO KEY UPDATE""")
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Locks the <c>Wishes</c> rows with the given ids <c>FOR UPDATE</c> and returns the ids found, in lock
    /// order (ascending by id; wish ids are numeric, so no collation is involved). Wish emotes, plan 2.3: a
    /// command or the reconcile locks the wish row before it reads and decides, so two of them never both
    /// compute from the same old state. The order across tables is the channel row first
    /// (<see cref="LockChannelsByIdsAsync"/>), then the wish rows. A requested id that is not returned does
    /// not exist (any more).
    /// </summary>
    public static async Task<IReadOnlyList<long>> LockWishesByIdsAsync(
        this AppDbContext db, IReadOnlyCollection<long> wishIds, CancellationToken cancellationToken)
    {
        RequireTransaction(db);
        if (wishIds.Count == 0)
        {
            return [];
        }

        var ids = wishIds.Distinct().ToArray();
        return await db.Database
            .SqlQuery<long>($"""SELECT "Id" AS "Value" FROM "Wishes" WHERE "Id" = ANY({ids}) ORDER BY "Id" FOR UPDATE""")
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Locks the <c>Wishes</c> rows with the given ids <b>of one channel</b> <c>FOR UPDATE</c>, waiting, and returns the
    /// ids found and locked, ascending (K7a, spec E32, plan #500 2.2a). The channel id is part of the statement's
    /// condition, so a row of another channel is never locked or waited for, not even briefly: Postgres checks the
    /// condition before it takes a row lock. That is the point of this entry next to the global
    /// <see cref="LockWishesByIdsAsync"/>: a lock must never touch a row of a channel the caller has not locked, or a
    /// manager of one channel could hold up the writers of another before the service has checked anything. The caller
    /// holds that channel row already (<see cref="LockChannelsByIdsAsync"/>). An id that is not returned does not exist
    /// (any more) or belongs to another channel. Same rules as the other entries: open transaction required, ids out,
    /// no entities.
    /// </summary>
    public static async Task<IReadOnlyList<long>> LockChannelWishesByIdsAsync(
        this AppDbContext db, string channelId, IReadOnlyCollection<long> wishIds, CancellationToken cancellationToken)
    {
        RequireTransaction(db);
        if (wishIds.Count == 0)
        {
            return [];
        }

        var ids = wishIds.Distinct().ToArray();
        return await db.Database
            .SqlQuery<long>($"""SELECT "Id" AS "Value" FROM "Wishes" WHERE "Id" = ANY({ids}) AND "ChannelId" = {channelId} ORDER BY "Id" FOR UPDATE""")
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The non-waiting twin of <see cref="LockChannelWishesByIdsAsync"/>: locks the <c>Wishes</c> rows with the given ids
    /// <b>of one channel</b> <c>FOR UPDATE NOWAIT</c>, ascending, and returns the ids found and locked (K7a, spec E32, plan
    /// #500 2.2a). If any matching row is locked by another transaction, Postgres does not wait but fails the statement
    /// at once with SQLSTATE <c>55P03</c> (<see cref="Npgsql.PostgresErrorCodes.LockNotAvailable"/>), a
    /// <see cref="Npgsql.PostgresException"/> that leaves the transaction aborted; the caller rolls it back and gives way.
    /// <c>NOWAIT</c> is only for rows taken <i>after</i> a wish row the caller already holds (the wishes merged into the
    /// one being deleted): a command that never waits for a second wish row while it holds one cannot be part of a
    /// deadlock with a writer that takes wish rows in another order, such as the retention's unordered batches. An id
    /// that is not returned does not exist (any more) or belongs to another channel. Same rules as the other entries.
    /// </summary>
    public static async Task<IReadOnlyList<long>> LockChannelWishesByIdsNoWaitAsync(
        this AppDbContext db, string channelId, IReadOnlyCollection<long> wishIds, CancellationToken cancellationToken)
    {
        RequireTransaction(db);
        if (wishIds.Count == 0)
        {
            return [];
        }

        var ids = wishIds.Distinct().ToArray();
        return await db.Database
            .SqlQuery<long>($"""SELECT "Id" AS "Value" FROM "Wishes" WHERE "Id" = ANY({ids}) AND "ChannelId" = {channelId} ORDER BY "Id" FOR UPDATE NOWAIT""")
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// In-memory twin of the predicate of <see cref="LockEmotesForReconcileAsync"/>, for the coverage
    /// check after the re-read: <paramref name="emote"/> must have its <see cref="Emote.Slots"/> loaded.
    /// </summary>
    public static bool IsReconcileTarget(Emote emote, IReadOnlySet<string> liveSevenTvEmoteIds) =>
        liveSevenTvEmoteIds.Contains(emote.SevenTvEmoteId)
        || !emote.IsArchived
        || emote.Slots.Any(s => !s.IsArchived && !s.SeedUnsettled);

    private static void RequireTransaction(AppDbContext db)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A row lock needs an open transaction; it would be released immediately otherwise.");
        }
    }
}
