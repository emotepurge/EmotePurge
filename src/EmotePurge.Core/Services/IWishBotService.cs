namespace EmotePurge.Core.Services;

/// <summary>
/// The wish side of the bot (plan #487 2.9, 2.10). For now only the numbers of the admin read; the channel part (the
/// routes under <c>…/wishes/bot</c>) joins it in a later task.
/// </summary>
public interface IWishBotService
{
    /// <summary>The <c>wishes</c> sub-object of <c>GET /api/admin/bot-account</c>, counted over every channel.</summary>
    Task<WishBotAdminSummary> GetAdminSummaryAsync(CancellationToken cancellationToken = default);
}

/// <summary>The wish numbers on the admin page (spec K4, plan 2.10).</summary>
/// <param name="Queue">The bot's pending work.</param>
/// <param name="BotChannels">Channels with the bot mode activated (<c>BotModeEnabled</c>), suspended or not.</param>
/// <param name="SuspendedChannels">Of those, the ones with a <c>BotSuspendedReason</c>.</param>
/// <param name="OpenRepairChecks">Wishes in a repair check phase (<c>BotRepairAtUtc</c> set, plan 2.5a (h)).</param>
public sealed record WishBotAdminSummary(WishBotQueueCounts Queue, int BotChannels, int SuspendedChannels, int OpenRepairChecks);

/// <summary>The bot's queue.</summary>
/// <param name="Adds"><c>approved</c> wishes in channels with the bot mode activated.</param>
/// <param name="Confirms"><c>unconfirmed</c> wishes (a write whose result is still being confirmed).</param>
/// <param name="Removals">Queued removals (<c>RemovalQueuedAtUtc</c> set).</param>
public sealed record WishBotQueueCounts(int Adds, int Confirms, int Removals);
