using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// Shared seeding and wiring for the slot-aware sync tests (plan #372, 6.3–6.5): a service with the
/// flag set explicitly, a 7TV client that answers one set, and rows written straight into the tables
/// so a test states the before-state of slots, seeds and usage rows exactly.
/// </summary>
internal static class SlotSyncKit
{
    public const string SetA = "01SLOTSETA00000000000000AA";
    public const string SetB = "01SLOTSETB00000000000000BB";

    public static readonly DateTime T0 = new(2026, 6, 1, 8, 0, 0, 111, DateTimeKind.Utc);
    public static readonly DateTime T1 = new(2026, 7, 1, 12, 0, 0, 123, DateTimeKind.Utc);
    public static readonly DateTime T2 = new(2026, 9, 1, 12, 0, 0, 456, DateTimeKind.Utc);
    public static readonly DateTime T3 = new(2026, 10, 1, 9, 30, 0, 789, DateTimeKind.Utc);
    public static readonly DateTime T5 = new(2026, 10, 5, 18, 15, 0, 5, DateTimeKind.Utc);

    public static string Url(string sevenTvId) => $"https://cdn.7tv.app/emote/{sevenTvId}/2x.webp";

    public static SevenTvEmote Live(string sevenTvId, string name, DateTime? addedAt) => new(sevenTvId, name, Url(sevenTvId), addedAt);

    public static ISevenTvApiClient ApiReturning(Channel channel, string setId, params SevenTvEmote[] emotes)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(setId, emotes))));
        return apiClient;
    }

    public static SevenTvSyncService CreateService(
        AppDbContext db,
        ISevenTvApiClient apiClient,
        bool slotsEnabled,
        EmoteMatchCache? cache = null,
        ILogger<SevenTvSyncService>? logger = null) =>
        new(
            db, apiClient, cache ?? new EmoteMatchCache(), new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db),
            new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db),
            new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System),
            new EmptySetConfirmationTracker(new EmptySetConfirmationOptions(), TimeProvider.System),
            logger ?? new RecordingLogger<SevenTvSyncService>(),
            new SlotReconcileOptions { Enabled = slotsEnabled });

    /// <summary>One sync of <paramref name="channel"/> against <paramref name="emotes"/> as set <paramref name="setId"/>.</summary>
    public static Task<SevenTvSyncResult?> SyncAsync(
        AppDbContext db,
        Channel channel,
        bool slotsEnabled,
        string setId,
        RecordingLogger<SevenTvSyncService>? logger,
        params SevenTvEmote[] emotes) =>
        CreateService(db, ApiReturning(channel, setId, emotes), slotsEnabled, logger: logger).SyncChannelAsync(channel.ChannelName);

    public static Task<SevenTvSyncResult?> SyncOnAsync(AppDbContext db, Channel channel, params SevenTvEmote[] emotes) =>
        SyncAsync(db, channel, slotsEnabled: true, SetA, logger: null, emotes);

    public static async Task<Channel> SeedChannelAsync(AppDbContext db, string name, string activeSetId = SetA)
    {
        var channel = new Channel
        {
            ChannelName = name,
            TwitchChannelId = $"tw_{name}",
            ActiveEmoteSetId = activeSetId,
            IsBotActive = true
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    public static async Task<Emote> SeedEmoteAsync(
        AppDbContext db,
        Channel channel,
        string sevenTvId,
        string name,
        bool archived = false,
        DateTime? firstSeenAt = null,
        DateTime? archivedAt = null)
    {
        var emote = new Emote
        {
            ChannelId = channel.Id,
            SevenTvEmoteId = sevenTvId,
            Name = name,
            ImageUrl = Url(sevenTvId),
            IsArchived = archived,
            ArchivedAt = archived ? archivedAt ?? DateTime.UtcNow.AddDays(-1) : null,
            FirstSeenAt = firstSeenAt,
            LastSyncedAt = DateTime.UtcNow.AddHours(-1)
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    public static async Task<EmoteSlot> SeedSlotAsync(
        AppDbContext db,
        Emote emote,
        string name,
        DateTime? addedAt,
        bool fromRest = true,
        bool archived = false,
        DateTime? archivedAt = null,
        bool seedUnsettled = false)
    {
        var slot = new EmoteSlot
        {
            EmoteId = emote.Id,
            Name = name,
            AddedAt = addedAt,
            AddedAtFromRest = fromRest,
            IsArchived = archived,
            ArchivedAt = archivedAt,
            SeedUnsettled = seedUnsettled,
            LastSyncedAt = new DateTime(2026, 10, 11, 21, 30, 0, DateTimeKind.Utc)
        };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot;
    }

    /// <summary>An unsettled M1 seed: name and archive state as the emote's, no date.</summary>
    public static Task<EmoteSlot> SeedSeedAsync(AppDbContext db, Emote emote) =>
        SeedSlotAsync(db, emote, emote.Name, addedAt: null, fromRest: false, archived: emote.IsArchived, archivedAt: emote.ArchivedAt, seedUnsettled: true);

    /// <summary>Usage rows of <paramref name="emote"/> in <paramref name="setId"/> on consecutive past days, all on <paramref name="slot"/>.</summary>
    public static async Task SeedUsageAsync(AppDbContext db, Emote emote, EmoteSlot? slot, string setId, params int[] dailyCounts)
    {
        var day = new DateOnly(2026, 8, 1);
        foreach (var count in dailyCounts)
        {
            db.UsageStats.Add(new UsageStat
            {
                EmoteId = emote.Id,
                SlotId = slot?.Id,
                EmoteSetId = setId,
                Date = day,
                UseCount = count,
                BotUseCount = 1,
                SharedChatUseCount = 2
            });
            day = day.AddDays(1);
        }

        await db.SaveChangesAsync();
    }

    public static async Task<List<EmoteSlot>> SlotsAsync(AppDbContext db, string emoteId)
    {
        var slots = await db.EmoteSlots.AsNoTracking().Where(s => s.EmoteId == emoteId).ToListAsync();
        return [.. slots.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal)];
    }

    public static Task<EmoteSlot> SlotAsync(AppDbContext db, string slotId) =>
        db.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == slotId);

    public static Task<Emote> EmoteAsync(AppDbContext db, Channel channel, string sevenTvId) =>
        db.Emotes.AsNoTracking().SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == sevenTvId);

    /// <summary>Usage rows per slot id (null for unslotted): row count and summed use count.</summary>
    public static async Task<Dictionary<string, (int Rows, int Uses)>> UsageBySlotAsync(AppDbContext db, string emoteId, string setId)
    {
        var rows = await db.UsageStats.AsNoTracking()
            .Where(u => u.EmoteId == emoteId && u.EmoteSetId == setId)
            .Select(u => new { u.SlotId, u.UseCount })
            .ToListAsync();
        return rows
            .GroupBy(r => r.SlotId ?? "<null>")
            .ToDictionary(g => g.Key, g => (g.Count(), g.Sum(r => r.UseCount)));
    }

    public static async Task<(int Rows, int Uses, int Bot, int Shared)> UsageTotalsAsync(AppDbContext db, string emoteId, string setId)
    {
        var rows = await db.UsageStats.AsNoTracking()
            .Where(u => u.EmoteId == emoteId && u.EmoteSetId == setId)
            .Select(u => new { u.UseCount, u.BotUseCount, u.SharedChatUseCount })
            .ToListAsync();
        return (rows.Count, rows.Sum(r => r.UseCount), rows.Sum(r => r.BotUseCount), rows.Sum(r => r.SharedChatUseCount));
    }

    /// <summary>Every column of a slot, for byte-equality checks.</summary>
    public static string Columns(EmoteSlot s) =>
        string.Join(
            "|",
            s.Id, s.EmoteId, s.Name, s.AddedAt?.Ticks, s.AddedAtFromRest, s.IsArchived, s.ArchivedAt?.Ticks, s.LastSyncedAt.Ticks, s.SeedUnsettled);

    public static IReadOnlyList<string> Lines(RecordingLogger<SevenTvSyncService> logger, string fragment) =>
        [.. logger.Entries.Where(e => e.Message.Contains(fragment, StringComparison.Ordinal)).Select(e => e.Message)];
}
