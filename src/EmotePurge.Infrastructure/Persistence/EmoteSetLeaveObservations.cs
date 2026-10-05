using EmotePurge.Core.SevenTv;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// The one way an <see cref="Core.Entities.EmoteSetLeaveObservation"/> gets written, and the read the
/// sync's post-check and the tag read-time rule share. Used by the 7TV sync (worker) and by the
/// set-centric delete report (Api), two processes that write the same rows from separate contexts.
/// <para>
/// <b>Why one atomic statement.</b> A load-then-update upsert could commit an <i>older</i> observation
/// after a newer one and turn <c>LastObservedAtUtc</c> back, which would make a placement registered in
/// between count again; and two first inserts of the same key would collide on the primary key, which
/// the sync's single retry (it only knows the emote index) does not handle. <c>INSERT … ON CONFLICT DO
/// UPDATE</c> takes the collision away, and <c>GREATEST</c> makes the order of the two commits
/// irrelevant: whoever commits last, the row keeps the later timestamp.
/// </para>
/// <para>
/// <b>Why not through the change tracker.</b> A tracked entity would be an insert or an update decided
/// by an earlier read — exactly the load-then-update shape above — and it would put a new entity type
/// into the sync's failed-save bookkeeping (<c>SevenTvSyncService.IsRowVanishedFor</c>). The entity
/// stays for the migration and for reading. A raw statement does not join <c>SaveChangesAsync</c>'s
/// implicit transaction, so every caller wraps it and its save in one explicit transaction.
/// </para>
/// </summary>
internal static class EmoteSetLeaveObservations
{
    private const string UpsertSql = """
        INSERT INTO "EmoteSetLeaveObservations" ("ChannelId", "SevenTvEmoteId", "SevenTvEmoteSetId", "LastObservedAtUtc")
        SELECT @channelId, input."SevenTvEmoteId", @emoteSetId, @observedAtUtc
        FROM UNNEST(@sevenTvEmoteIds) WITH ORDINALITY AS input("SevenTvEmoteId", "Position")
        ORDER BY input."Position"
        ON CONFLICT ("ChannelId", "SevenTvEmoteId", "SevenTvEmoteSetId")
        DO UPDATE SET "LastObservedAtUtc" = GREATEST("EmoteSetLeaveObservations"."LastObservedAtUtc", EXCLUDED."LastObservedAtUtc");
        """;

    /// <summary>
    /// Records that each of <paramref name="sevenTvEmoteIds"/> was credibly seen leaving set
    /// <paramref name="emoteSetId"/> of the channel at <paramref name="observedAtUtc"/> (the
    /// application's clock, never Postgres' <c>now()</c>: it is compared against the Api's
    /// registration stamps). Runs immediately, not at the caller's next save.
    /// <para>
    /// The ids are filtered, deduplicated and sorted first. Filtered: an id or set id that fails the
    /// 1–32 alphanumeric rule (<see cref="SevenTvEmoteIdValidation"/>) is dropped silently — the values
    /// come unvalidated from 7TV, and a too-long one would fail the varchar(32) column with 22001 and
    /// with it the whole sync of the channel; such an id can never match a placement anyway, since
    /// placements only hold validated ids. Deduplicated: a delta can name the same id twice (a #74
    /// duplicate cell), and a multi-row <c>ON CONFLICT DO UPDATE</c> refuses to touch a row twice.
    /// Sorted (ordinal): the Api and the worker then lock shared rows in the same order. Nothing
    /// left → no statement.
    /// </para>
    /// <para>
    /// A channel that vanished since the caller read it surfaces as a <see cref="PostgresException"/>
    /// with SQLSTATE 23503 (the foreign key to <c>Channels</c>) straight from this call — not as the
    /// <see cref="DbUpdateException"/> a save would throw.
    /// </para>
    /// </summary>
    public static async Task RecordAsync(
        AppDbContext db,
        string channelId,
        string emoteSetId,
        IEnumerable<string> sevenTvEmoteIds,
        DateTime observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!SevenTvEmoteIdValidation.IsValid(emoteSetId))
        {
            return;
        }

        var ids = sevenTvEmoteIds
            .Where(SevenTvEmoteIdValidation.IsValid)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            UpsertSql,
            [
                new NpgsqlParameter("channelId", NpgsqlDbType.Text) { Value = channelId },
                new NpgsqlParameter("emoteSetId", NpgsqlDbType.Text) { Value = emoteSetId },
                new NpgsqlParameter("observedAtUtc", NpgsqlDbType.TimestampTz) { Value = observedAtUtc },
                new NpgsqlParameter("sevenTvEmoteIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ids },
            ],
            cancellationToken);
    }

    /// <summary>
    /// The latest observed leave of each of <paramref name="sevenTvEmoteIds"/> from set
    /// <paramref name="emoteSetId"/> of the channel, keyed by 7TV emote id; an id without an observation
    /// is absent. One query over the scalar id list (rule 10). An empty list asks nothing.
    /// </summary>
    public static async Task<Dictionary<string, DateTime>> LoadLatestAsync(
        AppDbContext db,
        string channelId,
        string emoteSetId,
        IReadOnlyCollection<string> sevenTvEmoteIds,
        CancellationToken cancellationToken)
    {
        if (sevenTvEmoteIds.Count == 0)
        {
            return new Dictionary<string, DateTime>(StringComparer.Ordinal);
        }

        var ids = sevenTvEmoteIds.Distinct(StringComparer.Ordinal).ToArray();
        return await db.EmoteSetLeaveObservations
            .AsNoTracking()
            .Where(o => o.ChannelId == channelId && o.SevenTvEmoteSetId == emoteSetId && ids.Contains(o.SevenTvEmoteId))
            .ToDictionaryAsync(o => o.SevenTvEmoteId, o => o.LastObservedAtUtc, StringComparer.Ordinal, cancellationToken);
    }
}
