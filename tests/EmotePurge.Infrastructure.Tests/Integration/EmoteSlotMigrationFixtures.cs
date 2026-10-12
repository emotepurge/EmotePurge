namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// Raw-SQL fixtures shared by the emote-slot migration tests (M1 <c>AddEmoteSlotsMigrationTests</c>,
/// M2 <c>SwitchUsageStatsToSlotKeyMigrationTests</c>): the same input runs once against M1 with the M2
/// state simulated and once against the real M2 Up/Down (hand-off from #371). Written against the
/// schema before M1, so every insert omits the slot columns, as the old images wrote them.
/// </summary>
internal static class EmoteSlotMigrationFixtures
{
    /// <summary>
    /// 6,000 emotes (every tenth archived), each in set A and every third also in set B, four days each —
    /// 8,000 pairs, 32,000 rows. ANALYZE as on a live database, where the old columns have statistics
    /// and only the new one has none.
    /// </summary>
    public const string ThousandsOfPairsSql =
        """
        INSERT INTO "Channels" ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
        VALUES ('c1', '1', 'chan', 'SETA', true, now(), NULL);
        INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "ArchivedAt", "FirstSeenAt", "LastSyncedAt")
        SELECT 'e' || i, '7' || i, 'c1', 'Emote' || i, '', i % 10 = 0,
               CASE WHEN i % 10 = 0 THEN timestamptz '2026-09-01' END, NULL, now()
        FROM generate_series(1, 6000) i;
        INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source")
        SELECT 'e' || i, s.set_id, date '2026-08-01' + d, (i * 7 + d) % 50 + 1, d % 3, (i + d) % 2, 0
        FROM generate_series(1, 6000) i
        JOIN (VALUES ('SETA', 1), ('SETB', 3)) s(set_id, every) ON i % s.every = 0
        CROSS JOIN generate_series(0, 3) d;
        ANALYZE;
        """;

    /// <summary>
    /// On top of <see cref="ThousandsOfPairsSql"/> after M1: rows as the unchanged writers add them with a
    /// NULL slot — for existing pairs (new days) and for a new set C. 8,000 old pairs and 3,000 set-C
    /// pairs, four new days each: 44,000 rows.
    /// </summary>
    public const string LateNullRowsForThousandsOfPairsSql =
        """
        INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount")
        SELECT 'e' || i, s.set_id, date '2026-09-01' + d, (i + d) % 9 + 1, 0, 0
        FROM generate_series(1, 6000) i
        JOIN (VALUES ('SETA', 1), ('SETB', 3), ('SETC', 2)) s(set_id, every) ON i % s.every = 0
        CROSS JOIN generate_series(0, 3) d;
        """;

    /// <summary>The collapse fixture, before M1: emotes ex and ey of channel c1, four rows in set A.</summary>
    public const string CollapseBaseSql =
        """
        INSERT INTO "Channels" ("Id", "TwitchChannelId", "ChannelName", "ActiveEmoteSetId", "IsBotActive", "CreatedAt", "TrackingResumedAt")
        VALUES ('c1', '1', 'chan', 'SETA', true, now(), NULL);
        INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "LastSyncedAt")
        VALUES ('ex', '7x', 'c1', 'EmoteX', '', false, now()),
               ('ey', '7y', 'c1', 'EmoteY', '', false, now());
        INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source")
        VALUES ('ex', 'SETA', '2026-09-01', 5, 1, 0, 0),
               ('ex', 'SETA', '2026-09-02', 2, 0, 0, 0),
               ('ex', 'SETA', '2026-09-04', 8, 0, 0, 0),
               ('ey', 'SETA', '2026-09-01', 9, 0, 0, 0);
        """;

    /// <summary>
    /// The collapse fixture's M2 state, after M1 (the seed of ex carries its four days): four more slots of
    /// ex and a second row on three of its cells. Expected after the collapse: 2026-09-01 →
    /// <c>12|3|3|0|x-dated</c> (dated beats the NULL-dated seed; Source 0 + 1 → 0), 2026-09-02 →
    /// <c>6|1|1|0|x-null</c> (two NULL-dated slots: the ordinal smaller name 'Alpha' beats the seed
    /// 'EmoteX'), 2026-09-03 → <c>30|3|6|1|x-upper</c> ('Bravo' before 'alpha' under "C"; Source 1 + 1 →
    /// 1); three rows removed.
    /// </summary>
    public const string CollapseExtraSlotsSql =
        """
        INSERT INTO "EmoteSlots" ("Id", "EmoteId", "Name", "AddedAt", "AddedAtFromRest", "LastSyncedAt")
        VALUES ('x-dated', 'ex', 'Zeta',  '2026-03-01', true,  now()),
               ('x-null',  'ex', 'Alpha', NULL,         false, now()),
               ('x-upper', 'ex', 'Bravo', NULL,         false, now()),
               ('x-lower', 'ex', 'alpha', NULL,         false, now());
        INSERT INTO "UsageStats" ("EmoteId", "EmoteSetId", "Date", "UseCount", "BotUseCount", "SharedChatUseCount", "Source", "SlotId")
        VALUES
          ('ex', 'SETA', '2026-09-01', 7, 2, 3, 1, 'x-dated'),
          ('ex', 'SETA', '2026-09-02', 4, 1, 1, 0, 'x-null'),
          ('ex', 'SETA', '2026-09-03', 10, 1, 2, 1, 'x-lower'),
          ('ex', 'SETA', '2026-09-03', 20, 2, 4, 1, 'x-upper');
        """;

    /// <summary>The Testcontainers image (alpine, musl) compares bytewise by default; an ICU collation orders 'alpha' before 'Bravo'.</summary>
    public const string LinguisticNameCollationSql =
        """ALTER TABLE "EmoteSlots" ALTER COLUMN "Name" TYPE text COLLATE "und-x-icu";""";
}
