using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// <see cref="IWishBotService"/> (plan #487 2.10): for now the numbers of the admin page. Counted over every channel
/// with plain indexed counts; the bot mode is the stored activation (<c>BotModeEnabled</c>), not the effective channel
/// state, which needs the account availability and the pilot list and is the channel part's business.
/// </summary>
public sealed class WishBotService(AppDbContext db) : IWishBotService
{
    public async Task<WishBotAdminSummary> GetAdminSummaryAsync(CancellationToken cancellationToken = default)
    {
        var botChannelIds = db.ChannelWishSettings.AsNoTracking()
            .Where(s => s.BotModeEnabled)
            .Select(s => s.ChannelId);

        var adds = await db.Wishes.AsNoTracking()
            .CountAsync(w => w.Status == WishStatus.Approved && botChannelIds.Contains(w.ChannelId), cancellationToken);
        var confirms = await db.Wishes.AsNoTracking()
            .CountAsync(w => w.Status == WishStatus.Unconfirmed, cancellationToken);
        var removals = await db.Wishes.AsNoTracking()
            .CountAsync(w => w.RemovalQueuedAtUtc != null, cancellationToken);
        var repairs = await db.Wishes.AsNoTracking()
            .CountAsync(w => w.BotRepairAtUtc != null, cancellationToken);

        var botChannels = await db.ChannelWishSettings.AsNoTracking()
            .CountAsync(s => s.BotModeEnabled, cancellationToken);
        var suspended = await db.ChannelWishSettings.AsNoTracking()
            .CountAsync(s => s.BotModeEnabled && s.BotSuspendedReason != null, cancellationToken);

        return new WishBotAdminSummary(new WishBotQueueCounts(adds, confirms, removals), botChannels, suspended, repairs);
    }
}
