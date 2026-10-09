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
/// "not in the active set, never was"), so no reader of the active set sees it.
/// <c>LastEnteredSetAtUtc</c> is stamped as well (spec E34): it holds the sync's REST leave detection
/// off for one credibility window. If the emote's set later becomes the active one,
/// <c>SevenTvSyncService.UpsertEmote</c> finds the row by the same key, un-archives it and corrects
/// name, image and <c>FirstSeenAt</c>.
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
    private const string InsertSql = """
        INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "ArchivedAt", "FirstSeenAt", "LastSyncedAt", "LastEnteredSetAtUtc")
        SELECT input."Id", input."SevenTvEmoteId", @channelId, input."Name", input."ImageUrl", true, NULL, input."FirstSeenAt", @now, @now
        FROM UNNEST(@ids, @sevenTvEmoteIds, @names, @imageUrls, @firstSeenAts) AS input("Id", "SevenTvEmoteId", "Name", "ImageUrl", "FirstSeenAt")
        ON CONFLICT ("ChannelId", "SevenTvEmoteId") DO NOTHING;
        """;

    /// <summary>
    /// Ensures a row exists for every entry of <paramref name="rows"/> in channel
    /// <paramref name="channelId"/>, creating the missing ones archived and stamped with
    /// <paramref name="now"/>. Returns how many rows were created. The caller deduplicates
    /// <paramref name="rows"/> on <see cref="ArchivedEmoteRow.SevenTvEmoteId"/> first: Postgres refuses
    /// one statement that inserts the same key twice.
    /// </summary>
    public static async Task<int> EnsureRowsAsync(
        AppDbContext db,
        string channelId,
        IReadOnlyList<ArchivedEmoteRow> rows,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        return await db.Database.ExecuteSqlRawAsync(
            InsertSql,
            [
                new NpgsqlParameter("channelId", NpgsqlDbType.Text) { Value = channelId },
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now },
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
                    Value = rows.Select(r => r.FirstSeenAt).ToArray(),
                },
            ],
            cancellationToken);
    }
}

/// <summary>
/// One emote for <see cref="ArchivedEmoteRowUpsert.EnsureRowsAsync"/>. <paramref name="FirstSeenAt"/> is
/// when the emote was added to its set, where the caller's 7TV read reports it; null means unknown
/// (the ballot's read path carries no date) and is stored as null, never guessed.
/// </summary>
internal sealed record ArchivedEmoteRow(string SevenTvEmoteId, string Name, string ImageUrl, DateTime? FirstSeenAt);
