using EmotePurge.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// What the wish usage window reads of one emote in one channel: the window, the sum and mean over all slots
/// of the emote, and the coverage-gap flag. <see cref="OwnSlotIds"/> is handed back for the caller's rank.
/// </summary>
public sealed record WishUsageRead(
    DateTime WindowStartUtc,
    DateTime EvaluatedAtUtc,
    long Total,
    decimal MeanPerDay,
    bool HasCoverageGap,
    IReadOnlyList<string> OwnSlotIds);

/// <summary>
/// The one read behind the wish usage numbers, shared by the detail response and the expiry run (plan 2.2, T2).
/// Rule 10: the slot ids are reduced to scalar lists first and the sums run over UsageStats alone, on UseCount
/// only like every product read of usage (a bot-only or shared-chat-only row is no use). Slots of the emote
/// count whether archived or not (a re-added emote keeps its earlier stay's usage), a migration seed that no
/// sync has settled does not. Reads only; nothing is tracked.
/// </summary>
public static class WishUsageQueries
{
    /// <summary>Null when the channel row is gone (a purge or merge at the same moment).</summary>
    public static async Task<WishUsageRead?> ReadAsync(
        this AppDbContext db,
        string channelId,
        string sevenTvEmoteId,
        DateTime startsAtUtc,
        DateTime evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        var channel = await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new { c.CreatedAt, c.TrackingResumedAt, c.DeactivatedAtUtc, c.IsBotActive })
            .FirstOrDefaultAsync(cancellationToken);
        if (channel is null)
        {
            return null;
        }

        var windowStart = WishUsageWindow.WindowStart(startsAtUtc, evaluatedAtUtc);
        var from = DateOnly.FromDateTime(windowStart);
        var to = DateOnly.FromDateTime(evaluatedAtUtc);

        var ownSlotIds = await db.EmoteSlots.AsNoTracking()
            .Where(s => !s.SeedUnsettled && s.Emote.ChannelId == channelId && s.Emote.SevenTvEmoteId == sevenTvEmoteId)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        var total = ownSlotIds.Count == 0
            ? 0L
            : await db.UsageStats.AsNoTracking()
                .Where(u => ownSlotIds.Contains(u.SlotId) && u.Date >= from && u.Date <= to)
                .SumAsync(u => (long)u.UseCount, cancellationToken);

        var facts = new WishCoverageFacts(
            TrackingCoverage.TrackedSince(channel.TrackingResumedAt, channel.CreatedAt), channel.DeactivatedAtUtc, channel.IsBotActive);
        var mean = WishUsageWindow.MeanPerDay(total, WishUsageWindow.LengthInDays(windowStart, evaluatedAtUtc));
        return new WishUsageRead(
            windowStart, evaluatedAtUtc, total, mean, WishUsageWindow.HasCoverageGap(facts, windowStart, evaluatedAtUtc), ownSlotIds);
    }
}
