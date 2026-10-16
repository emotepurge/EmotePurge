using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Tests.Fixtures;

// Seeds the states the imported-data removal (#384) works on — channel, emotes, slots (settled,
// unsettled seeds, CreatedSlot provenance), runs with snapshot rows, coverage days, live and imported
// usage rows — straight through EF, without the backfill service: the tests must not depend on how
// #379 part B reshapes it. Every call saves at once, so ids exist for the next call. Plus the hashes
// that make "byte-identical" checkable: all columns of all rows, in a fixed order.
internal static class ImportedBackfillSeed
{
    public const string ArchiveHost = "logs.cyex.app";

    public static async Task<Channel> AddChannelAsync(
        AppDbContext db, string name, string activeEmoteSetId, bool isBotActive = true, DateTime? createdAt = null)
    {
        var channel = new Channel
        {
            ChannelName = name,
            TwitchChannelId = $"tw_{name}",
            ActiveEmoteSetId = activeEmoteSetId,
            IsBotActive = isBotActive,
            CreatedAt = createdAt ?? new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    public static async Task<Emote> AddEmoteAsync(AppDbContext db, Channel channel, string sevenTvEmoteId, bool isPlaceholder = false)
    {
        var emote = new Emote
        {
            ChannelId = channel.Id,
            SevenTvEmoteId = sevenTvEmoteId,
            Name = sevenTvEmoteId,
            ImageUrl = $"https://cdn.7tv.app/emote/{sevenTvEmoteId}/1x.webp",
            IsArchived = isPlaceholder,
            IsPlaceholder = isPlaceholder,
            LastSyncedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    /// <summary>
    /// A slot of <paramref name="emote"/>. Defaults to a settled, active slot; a flag-off enqueue seed is
    /// <c>seedUnsettled: true, archived: true</c> (archived without archive time).
    /// </summary>
    public static async Task<EmoteSlot> AddSlotAsync(
        AppDbContext db, Emote emote, string name, bool seedUnsettled = false, bool archived = false, DateTime? archivedAt = null)
    {
        var slot = new EmoteSlot
        {
            EmoteId = emote.Id,
            Name = name,
            IsArchived = archived,
            ArchivedAt = archivedAt,
            SeedUnsettled = seedUnsettled,
            LastSyncedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot;
    }

    /// <summary>One usage row per day of <c>[from, toExclusive)</c> on <paramref name="slot"/>.</summary>
    public static async Task AddUsageAsync(
        AppDbContext db, EmoteSlot slot, string emoteSetId, DateOnly from, DateOnly toExclusive, UsageStatSource source, int useCount = 3)
    {
        for (var day = from; day < toExclusive; day = day.AddDays(1))
        {
            db.UsageStats.Add(new UsageStat
            {
                EmoteId = slot.EmoteId,
                SlotId = slot.Id,
                EmoteSetId = emoteSetId,
                Date = day,
                UseCount = useCount,
                BotUseCount = 1,
                SharedChatUseCount = 0,
                Source = source,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>One coverage row per day of <c>[from, toExclusive)</c>.</summary>
    public static async Task AddCoverageAsync(
        AppDbContext db, Channel channel, DateOnly from, DateOnly toExclusive, string emoteSetId, string archiveHost = ArchiveHost, long? runId = null)
    {
        for (var day = from; day < toExclusive; day = day.AddDays(1))
        {
            db.ChatLogBackfillCoverage.Add(new ChatLogBackfillCoverageDay
            {
                ChannelId = channel.Id,
                Day = day,
                EmoteSetId = emoteSetId,
                ArchiveHost = archiveHost,
                RunId = runId,
                CompletedAtUtc = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>A run of <paramref name="channel"/> with one snapshot row per entry.</summary>
    public static async Task<ChatLogBackfillRun> AddRunAsync(
        AppDbContext db, Channel channel, ChatLogBackfillRunStatus status, string emoteSetId, params SnapshotEntry[] snapshot)
    {
        var terminal = status is ChatLogBackfillRunStatus.Completed or ChatLogBackfillRunStatus.Failed or ChatLogBackfillRunStatus.Cancelled;
        var run = new ChatLogBackfillRun
        {
            ChannelId = channel.Id,
            Status = status,
            RequestedMonths = 1,
            WindowFrom = new DateOnly(2026, 4, 1),
            WindowTo = new DateOnly(2026, 5, 1),
            WeeksTotal = 5,
            WeeksDone = terminal ? 5 : 0,
            EmoteSetId = emoteSetId,
            ArchiveBaseUrl = $"https://{ArchiveHost}/",
            RequestedByTwitchUserId = "seed-requester",
            RequestedByLogin = "seedrequester",
            RequestedAtUtc = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            FinishedAtUtc = terminal ? new DateTime(2026, 5, 1, 13, 0, 0, DateTimeKind.Utc) : null,
        };
        foreach (var entry in snapshot)
        {
            run.Emotes.Add(new ChatLogBackfillRunEmote
            {
                EmoteId = entry.Emote.Id,
                SevenTvEmoteId = entry.Emote.SevenTvEmoteId,
                Name = entry.Slot.Name,
                SlotId = entry.Slot.Id,
                CreatedRow = entry.CreatedRow,
                CreatedSlot = entry.CreatedSlot,
            });
        }

        db.ChatLogBackfillRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    /// <summary>md5 over every column of the channel's live rows, ordered by id.</summary>
    public static Task<string> LiveRowsHashAsync(AppDbContext db, string channelId) =>
        HashAsync(db.Database.SqlQuery<string>(
            $"""
             SELECT coalesce(md5(string_agg(u::text, '|' ORDER BY u."Id")), '') AS "Value"
             FROM "UsageStats" u JOIN "Emotes" e ON e."Id" = u."EmoteId"
             WHERE e."ChannelId" = {channelId} AND u."Source" = 0
             """));

    /// <summary>
    /// md5 per table over every row of the channel — usage rows, coverage, emotes, slots, runs and
    /// snapshot rows — each over all columns in a fixed order.
    /// </summary>
    public static async Task<ChannelHashes> ChannelHashesAsync(AppDbContext db, string channelId) =>
        new(
            await HashAsync(db.Database.SqlQuery<string>(
                $"""
                 SELECT coalesce(md5(string_agg(u::text, '|' ORDER BY u."Id")), '') AS "Value"
                 FROM "UsageStats" u JOIN "Emotes" e ON e."Id" = u."EmoteId" WHERE e."ChannelId" = {channelId}
                 """)),
            await HashAsync(db.Database.SqlQuery<string>(
                $"""
                 SELECT coalesce(md5(string_agg(d::text, '|' ORDER BY d."Day")), '') AS "Value"
                 FROM "ChatLogBackfillCoverage" d WHERE d."ChannelId" = {channelId}
                 """)),
            await HashAsync(db.Database.SqlQuery<string>(
                $"""
                 SELECT coalesce(md5(string_agg(e::text, '|' ORDER BY e."Id")), '') AS "Value"
                 FROM "Emotes" e WHERE e."ChannelId" = {channelId}
                 """)),
            await HashAsync(db.Database.SqlQuery<string>(
                $"""
                 SELECT coalesce(md5(string_agg(s::text, '|' ORDER BY s."Id")), '') AS "Value"
                 FROM "EmoteSlots" s JOIN "Emotes" e ON e."Id" = s."EmoteId" WHERE e."ChannelId" = {channelId}
                 """)),
            await HashAsync(db.Database.SqlQuery<string>(
                $"""
                 SELECT coalesce(md5(string_agg(r::text, '|' ORDER BY r."Id")), '') AS "Value"
                 FROM "ChatLogBackfillRuns" r WHERE r."ChannelId" = {channelId}
                 """)),
            await HashAsync(db.Database.SqlQuery<string>(
                $"""
                 SELECT coalesce(md5(string_agg(x::text, '|' ORDER BY x."RunId", x."SlotId")), '') AS "Value"
                 FROM "ChatLogBackfillRunEmotes" x JOIN "ChatLogBackfillRuns" r ON r."Id" = x."RunId" WHERE r."ChannelId" = {channelId}
                 """)));

    /// <summary>The channel's unsettled seeds that no usage row points at (the predicate of AK 7).</summary>
    public static Task<int> RowlessUnsettledSeedCountAsync(AppDbContext db, string channelId) =>
        db.Database.SqlQuery<int>(
            $"""
             SELECT count(*)::int AS "Value"
             FROM "EmoteSlots" s JOIN "Emotes" e ON e."Id" = s."EmoteId"
             WHERE e."ChannelId" = {channelId} AND s."SeedUnsettled"
               AND NOT EXISTS (SELECT 1 FROM "UsageStats" x WHERE x."SlotId" = s."Id")
             """).SingleAsync();

    public static Task<int> ImportedRowCountAsync(AppDbContext db, string channelId) =>
        db.UsageStats.CountAsync(u => u.Emote.ChannelId == channelId && u.Source == UsageStatSource.ChatLogArchive);

    public static Task<int> CoverageDayCountAsync(AppDbContext db, string channelId) =>
        db.ChatLogBackfillCoverage.CountAsync(d => d.ChannelId == channelId);

    private static Task<string> HashAsync(IQueryable<string> query) => query.SingleAsync();
}

/// <summary>One snapshot row of a seeded run: the slot it counts for and its provenance flags.</summary>
internal sealed record SnapshotEntry(Emote Emote, EmoteSlot Slot, bool CreatedRow = false, bool CreatedSlot = false);

/// <summary>Per-table hashes of one channel's rows; equal records mean byte-identical tables.</summary>
internal sealed record ChannelHashes(
    string UsageStats, string Coverage, string Emotes, string EmoteSlots, string Runs, string Snapshots);
