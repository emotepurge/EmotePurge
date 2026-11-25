using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// <see cref="IWishBotFallback"/> (#487 plan 2.8). The selection runs without a lock (one query over the wishes with an
/// open bot action in activated channels); each wish is then decided in its own short transaction behind its row lock,
/// re-read there, so a command or the runner that changed it meanwhile wins and a row gone by then (deleted, K7a) is
/// skipped. Only the wish row is locked, never the channel row, like the expiry run: the runner's send transactions take
/// channel then wish, so no cycle. The decision per wish is <see cref="ApplyTo"/>; open attempts go through
/// <see cref="WishBotAttemptResolution"/> (K6b-11). The decision version stays (2.5a (e)): clearing an attempt or a queue
/// is no manager decision, and every bot booking is conditional on its own fields anyway.
/// </summary>
public sealed class WishBotFallback(
    AppDbContext db,
    WishOptions wishOptions,
    BotAccountOptions botAccountOptions,
    IGlobalAdminAllowlist admins,
    IWishChangedPublisher changedPublisher,
    TimeProvider timeProvider,
    ILogger<WishBotFallback> logger) : IWishBotFallback
{
    public async Task<int> ApplyAsync(BotAccountAvailabilityState account, CancellationToken cancellationToken = default)
    {
        var channelIds = await CandidateChannelIdsAsync(cancellationToken);
        if (channelIds.Count == 0)
        {
            return 0;
        }

        var now = Now();
        var pilotList = wishOptions.PilotList;
        var channels = await db.ChannelWishSettings.AsNoTracking()
            .Where(s => channelIds.Contains(s.ChannelId))
            .Select(s => new { Settings = s, s.Channel!.TwitchChannelId })
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var channel in channels.OrderBy(c => c.Settings.ChannelId, StringComparer.Ordinal))
        {
            var state = WishBotChannelState.Of(
                channel.Settings,
                account,
                pilotList.Covers(channel.TwitchChannelId),
                WishBotChannelStateReader.ActivatorIsAdminById(admins, channel.Settings),
                now);
            if (state == WishBotChannelMode.Fallback)
            {
                changed += await ApplyToChannelAsync(channel.Settings.ChannelId, cancellationToken);
            }
        }

        return changed;
    }

    public async Task<int> ApplyAsync(CancellationToken cancellationToken = default)
    {
        var channelIds = await CandidateChannelIdsAsync(cancellationToken);
        var switchesOn = wishOptions.Enabled && botAccountOptions.Enabled;
        var pilotList = wishOptions.PilotList;

        var changed = 0;
        foreach (var channelId in channelIds)
        {
            // One statement per channel, never a memo of the run (K6d-7, K6e-9): the state it books from is one snapshot.
            var facts = await WishBotChannelFacts.ReadAsync(db, channelId, cancellationToken);
            if (WishBotChannelFacts.StateOf(facts, switchesOn, pilotList, admins, Now()) == WishBotChannelMode.Fallback)
            {
                changed += await ApplyToChannelAsync(channelId, cancellationToken);
            }
        }

        return changed;
    }

    /// <summary>
    /// The fallback of one wish (plan 2.8), pure on a row the caller holds locked: an open add or an open removal attempt
    /// is resolved without 7TV (<see cref="WishBotAttemptResolution"/>); a queued removal without an attempt is cleared, and
    /// an <c>active</c> wish under it becomes <c>presented</c> (<c>bot_unavailable</c>); a <c>presented</c> wish keeps its
    /// state and reason. Everything else stays, <c>approved</c> without an intent and an open repair phase included.
    /// True when a field changed.
    /// </summary>
    internal static bool ApplyTo(Wish wish, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(wish);

        if (WishBotAttemptResolution.HasOpenAdd(wish) || wish.BotRemoveAtUtc is not null)
        {
            return WishBotAttemptResolution.Apply(wish, nowUtc);
        }

        if (wish.RemovalQueuedAtUtc is null)
        {
            return false;
        }

        if (string.Equals(wish.Status, WishStatus.Active, StringComparison.Ordinal))
        {
            wish.Status = WishStatus.Presented;
            wish.StatusReason = WishStatusReason.BotUnavailable;
            wish.StatusChangedAtUtc = nowUtc;
        }

        wish.RemovalQueuedAtUtc = null;
        wish.RemovalQueuedVersion = null;
        return true;
    }

    // One query: the channels with an activated bot mode that hold a wish with an open bot action. Without such a
    // channel this is all the run costs.
    private Task<List<string>> CandidateChannelIdsAsync(CancellationToken cancellationToken) =>
        OpenBotActions(db.Wishes.AsNoTracking())
            .Where(w => db.ChannelWishSettings.Any(s => s.ChannelId == w.ChannelId && s.BotModeEnabled))
            .Select(w => w.ChannelId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);

    // Each wish of the channel in its own transaction behind its row lock; one wish.changed after the channel's last
    // commit. A failure skips that wish (logged); the next run retries it.
    private async Task<int> ApplyToChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        var ids = await OpenBotActions(db.Wishes.AsNoTracking())
            .Where(w => w.ChannelId == channelId)
            .OrderBy(w => w.Id)
            .Select(w => w.Id)
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var id in ids)
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                // K6b-17: a row deleted between the selection and the lock is "nothing to do", never an error.
                if ((await db.LockWishesByIdsAsync([id], cancellationToken)).Count == 0)
                {
                    continue;
                }

                var wish = await db.Wishes.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
                if (wish is null
                    || !string.Equals(wish.ChannelId, channelId, StringComparison.Ordinal)
                    || !ApplyTo(wish, Now()))
                {
                    continue;
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                changed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Wish bot fallback: wish {WishId} failed ({ExceptionType}, SQLSTATE {SqlState}); the next run retries it.",
                    id, ex.GetType().Name, (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        if (changed > 0)
        {
            logger.LogInformation(
                "Wish bot fallback: {Count} wish(es) of channel {ChannelId} handed to the managers.", changed, channelId);
            var channelName = await db.Channels.AsNoTracking()
                .Where(c => c.Id == channelId)
                .Select(c => c.ChannelName)
                .FirstOrDefaultAsync(cancellationToken);
            if (channelName is not null)
            {
                await changedPublisher.PublishAsync(channelName, cancellationToken);
            }
        }

        return changed;
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    // The wishes with an open bot action (plan 2.8): an add whose outcome is open, a queued removal, an open removal
    // attempt. approved without an intent and an open repair phase alone are no such action.
    private static IQueryable<Wish> OpenBotActions(IQueryable<Wish> wishes) =>
        wishes.Where(w => w.Status == WishStatus.Unconfirmed
            || (w.Status == WishStatus.Approved && w.BotWriteAtUtc != null)
            || w.RemovalQueuedAtUtc != null
            || w.BotRemoveAtUtc != null);
}
