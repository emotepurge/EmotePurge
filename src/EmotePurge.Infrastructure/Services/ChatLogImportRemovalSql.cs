namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The three deletes of <see cref="ChatLogImportRemovalService"/> (#384, spec 5.3 steps 7–9), in the
/// order they run. Each is one statement with named parameters; the caller owns the transaction and
/// holds the channel row and the affected emote rows locked before the first one runs.
/// <para>
/// Steps 7 and 8 must stay two statements. Every part of one statement reads the same snapshot, so a
/// seed check written as a second CTE of the row delete would still see the rows that delete removes
/// and never find a seed that has just become rowless.
/// </para>
/// </summary>
internal static class ChatLogImportRemovalSql
{
    /// <summary>
    /// Step 7: every imported row (<c>@importedSource</c>, the value of
    /// <c>UsageStatSource.ChatLogArchive</c>) of every emote of <c>@channelId</c>, whatever its set or
    /// day and whether or not a coverage day stands for it. Returns one row per slot id with the number
    /// of rows deleted on it: the sum is the removed row count, the slot ids are the seed candidates of
    /// step 8. The channel filter decides what goes, not the set of emotes the caller locked.
    /// </summary>
    public const string DeleteImportedUsageRowsSql =
        """
        WITH deleted AS (
            DELETE FROM "UsageStats" u
            USING "Emotes" e
            WHERE u."EmoteId" = e."Id"
              AND e."ChannelId" = @channelId
              AND u."Source" = @importedSource
            RETURNING u."SlotId"
        )
        SELECT "SlotId", count(*)::int AS "Rows"
        FROM deleted
        GROUP BY "SlotId"
        """;

    /// <summary>
    /// Step 8: of the candidate slots (<c>@candidates</c>, the slot ids step 7 deleted rows from), every
    /// unsettled seed that no usage row points at any more. Deliberately no predicate on
    /// <c>CreatedSlot</c>, <c>IsArchived</c>, <c>ArchivedAt</c>, set or channel: a seed a flag-off enqueue
    /// created goes as well (D7), a settled slot stays even when rowless (D8), and a seed that was
    /// rowless before is never a candidate. Returns the number of seeds deleted.
    /// </summary>
    public const string DeleteRowlessCandidateSeedsSql =
        """
        DELETE FROM "EmoteSlots" s
        WHERE s."Id" = ANY(@candidates)
          AND s."SeedUnsettled"
          AND NOT EXISTS (SELECT 1 FROM "UsageStats" x WHERE x."SlotId" = s."Id")
        """;

    /// <summary>Step 9: the channel's whole import coverage. Returns the number of days deleted.</summary>
    public const string DeleteCoverageSql =
        """
        DELETE FROM "ChatLogBackfillCoverage"
        WHERE "ChannelId" = @channelId
        """;
}
