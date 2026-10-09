using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Creates the <c>Emote</c> rows a caller needs for 7TV emotes this channel has never been observed to
/// have in its active set: the set-session ballot today, the chat-log backfill later (spec D27). One
/// race-safe statement, <c>INSERT … ON CONFLICT ("ChannelId", "SevenTvEmoteId") DO NOTHING</c>, so a
/// row that already exists, active or archived, is left exactly as it is, stamps included.
/// <para>
/// A created row is archived without an archive date (<c>IsArchived = true</c>, <c>ArchivedAt = null</c>:
/// "not in the active set, never was"), so no reader of the active set sees it, and it carries
/// <c>IsPlaceholder = true</c> (D37), which keeps it out of the sync's REST leave detection until the
/// sync actually observes it in the active set. <c>LastEnteredSetAtUtc</c> is stamped as well, kept
/// from the ballot's original statement (spec E34) so its rows stay what they were; while the marker
/// is set, the stamp plays no part in the leave detection. If the emote's set later becomes the active
/// one, <c>SevenTvSyncService.UpsertEmote</c> finds the row by the same key, un-archives it, clears
/// the marker and corrects name, image and <c>FirstSeenAt</c>.
/// </para>
/// <para>
/// Runs immediately as a raw statement, not at the caller's next save, and does not join
/// <c>SaveChangesAsync</c>'s implicit transaction: callers that need the rows to commit together with
/// their own writes hold an explicit transaction around both. A concurrent sync inserting the same key
/// first makes this statement a no-op for that row; the reverse order is the sync's own retry
/// (<c>SevenTvSyncService.IsEmoteKeyConflict</c>).
/// </para>
/// </summary>
internal static class ArchivedEmoteRowUpsert
{
    // Inserted in key order (ORDER BY): two callers upserting overlapping sets of new keys at the same
    // time (a ballot and a backfill enqueue of the same channel) then take the keys' index locks in the
    // same order, so one waits for the other instead of each holding a key the other needs (40P01).
    // RETURNING reports exactly the rows this statement created — a key a concurrent writer inserted
    // first is a DO NOTHING and is not reported.
    private const string InsertSql = """
        INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "ArchivedAt", "FirstSeenAt", "LastSyncedAt", "LastEnteredSetAtUtc", "IsPlaceholder")
        SELECT input."Id", input."SevenTvEmoteId", @channelId, input."Name", input."ImageUrl", true, NULL, input."FirstSeenAt", @now, @now, true
        FROM UNNEST(@ids, @sevenTvEmoteIds, @names, @imageUrls, @firstSeenAts) AS input("Id", "SevenTvEmoteId", "Name", "ImageUrl", "FirstSeenAt")
        ORDER BY input."SevenTvEmoteId"
        ON CONFLICT ("ChannelId", "SevenTvEmoteId") DO NOTHING
        RETURNING "SevenTvEmoteId" AS "Value"
        """;

    /// <summary>
    /// Ensures a row exists for every entry of <paramref name="rows"/> in channel
    /// <paramref name="channelId"/>, creating the missing ones as archived placeholders stamped with
    /// <paramref name="now"/>. Returns how many rows were created. The caller deduplicates
    /// <paramref name="rows"/> on <see cref="ArchivedEmoteRow.SevenTvEmoteId"/> first: Postgres refuses
    /// one statement that inserts the same key twice.
    /// </summary>
    public static async Task<int> EnsureRowsAsync(
        AppDbContext db,
        string channelId,
        IReadOnlyList<ArchivedEmoteRow> rows,
        DateTime now,
        CancellationToken cancellationToken) =>
        (await EnsureRowsReportingCreatedAsync(db, channelId, rows, now, cancellationToken)).Count;

    /// <summary>
    /// <see cref="EnsureRowsAsync"/>, returning the 7TV ids of exactly the rows this call created — the
    /// chat-log backfill's creation provenance (spec D41, <c>ChatLogBackfillRunEmote.CreatedRow</c>).
    /// A row a concurrent writer inserted first is not among them, even when it was absent a moment
    /// before.
    /// </summary>
    public static async Task<IReadOnlySet<string>> EnsureRowsReportingCreatedAsync(
        AppDbContext db,
        string channelId,
        IReadOnlyList<ArchivedEmoteRow> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        // Not composed with any LINQ operator, so EF sends the statement as it is (no subquery wrap).
        var created = await db.Database.SqlQueryRaw<string>(
            InsertSql,
            [
                new NpgsqlParameter("channelId", NpgsqlDbType.Text) { Value = channelId },
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = ToUtc(now) },
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = rows.Select(_ => Guid.NewGuid().ToString()).ToArray(),
                },
                new NpgsqlParameter("sevenTvEmoteIds", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = rows.Select(r => r.SevenTvEmoteId).ToArray(),
                },
                new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = rows.Select(r => r.Name).ToArray(),
                },
                new NpgsqlParameter("imageUrls", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = rows.Select(r => r.ImageUrl).ToArray(),
                },
                new NpgsqlParameter("firstSeenAts", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
                {
                    Value = rows.Select(r => r.FirstSeenAt is { } firstSeenAt ? ToUtc(firstSeenAt) : (DateTime?)null).ToArray(),
                },
            ]).ToListAsync(cancellationToken);
        return created.ToHashSet(StringComparer.Ordinal);
    }

    // Npgsql refuses a DateTime whose Kind is not Utc for a timestamptz parameter, and the dates come
    // from callers' 7TV reads. Local is converted; Unspecified is taken as UTC, the convention every
    // 7TV timestamp in this codebase follows.
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

/// <summary>
/// One emote for <see cref="ArchivedEmoteRowUpsert.EnsureRowsAsync"/>. <paramref name="FirstSeenAt"/> is
/// when the emote was added to its set, where the caller's 7TV read reports it; null means unknown
/// (the ballot's read path carries no date) and is stored as null, never guessed. Any
/// <see cref="DateTimeKind"/> is accepted and stored as UTC (Unspecified is read as UTC).
/// </summary>
internal sealed record ArchivedEmoteRow(string SevenTvEmoteId, string Name, string ImageUrl, DateTime? FirstSeenAt);
