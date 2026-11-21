using EmotePurge.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// What the wish usage window reads of one emote in one channel: the window, the sum and mean over all slots
/// of the emote (flagged as withheld for a window under 24 hours, E4), and the coverage-gap flag. <see cref="OwnSlotIds"/> is handed back for the caller's rank.
/// <see cref="Daily"/> is the sum per UTC day of the window, every day from the window's first to the evaluation
/// day in order, zero-filled; <see cref="Total"/> is its sum (plan #485 2.1a).
/// </summary>
public sealed record WishUsageRead(
    DateTime WindowStartUtc,
    DateTime EvaluatedAtUtc,
    long Total,
    decimal MeanPerDay,
    bool MeanWithheld,
    bool HasCoverageGap,
    IReadOnlyList<string> OwnSlotIds,
    IReadOnlyList<WishUsageDayDto> Daily);

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
        // One grouped read per window (at most eight days); the total is the sum of the series, so the two
        // can never disagree.
        var perDay = ownSlotIds.Count == 0
            ? new Dictionary<DateOnly, long>()
            : await db.UsageStats.AsNoTracking()
                .Where(u => ownSlotIds.Contains(u.SlotId) && u.Date >= from && u.Date <= to)
                .GroupBy(u => u.Date)
                .Select(g => new { Date = g.Key, UseCount = g.Sum(u => (long)u.UseCount) })
                .ToDictionaryAsync(d => d.Date, d => d.UseCount, cancellationToken);
        var daily = new List<WishUsageDayDto>(to.DayNumber - from.DayNumber + 1);
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            daily.Add(new WishUsageDayDto(day, perDay.GetValueOrDefault(day)));
        }

        var total = daily.Sum(d => d.UseCount);

        var facts = new WishCoverageFacts(
            TrackingCoverage.TrackedSince(channel.TrackingResumedAt, channel.CreatedAt), channel.DeactivatedAtUtc, channel.IsBotActive);
        var mean = WishUsageWindow.MeanPerDay(total, WishUsageWindow.LengthInDays(windowStart, evaluatedAtUtc));
        return new WishUsageRead(
            windowStart, evaluatedAtUtc, total, mean, WishUsageWindow.IsMeanWithheld(windowStart, evaluatedAtUtc),
            WishUsageWindow.HasCoverageGap(facts, windowStart, evaluatedAtUtc), ownSlotIds, daily);
    }
}
