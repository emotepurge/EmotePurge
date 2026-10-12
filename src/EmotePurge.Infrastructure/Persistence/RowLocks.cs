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
/// <c>EmoteSetLeaveObservations</c>.
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
