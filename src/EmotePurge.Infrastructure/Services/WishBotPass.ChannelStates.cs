using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

// Step 3 of the bot pass (#487 plan 2.5, 2.8, 2.5b; T8b): every activated channel, suspended ones included, is reviewed
// with each editor read (at most one per minute, IBotAccountEditorCheck): not covered → not_listed, consent to another
// bot account → bot_account_changed, then the read: editor → resume and book the proof, anything else → not_editor. A read
// that is not available changes nothing (no suspension because of a 7TV outage; after 15 minutes the proof is stale and
// the channel falls back by WishBotChannelState alone). Nothing here needs the token, so it runs also while
// TokenUsable is false.
//
// Every booking is one short transaction in the order of the bot's send transactions: the channel row (FOR NO KEY UPDATE,
// like activate and DELETE, so they stand in line), then the credential row FOR SHARE with the runner epoch (the fence),
// conditional on the identity and bot id the read was made for, Status valid (plan 2.3, R2-5); the settings are re-read
// and the verdict decided again behind both locks. One wish.botSuspend per change of the reason (details reason, or
// resumed), one wish.changed when the channel's effective state changed.
public sealed partial class WishBotPass
{
    // False: the lock or the epoch was lost; the pass books nothing more.
    private async Task<bool> ReviewChannelsAsync(
        IBotAccountSession session, BotAccountAvailabilityState account, CancellationToken cancellationToken)
    {
        // An unavailable account puts every activated channel into the fallback anyway; nothing to review per channel.
        if (!account.IsAvailable || account.BotSevenTvUserId is not { } botId)
        {
            return true;
        }

        var channels = await db.ChannelWishSettings.AsNoTracking()
            .Where(s => s.BotModeEnabled)
            .OrderBy(s => s.ChannelId)
            .Select(s => new { Settings = s, s.Channel!.TwitchChannelId })
            .ToListAsync(cancellationToken);
        if (channels.Count == 0)
        {
            return true;
        }

        var pilotList = wishOptions.PilotList;
        bool Covered(ChannelWishSettings settings, string? twitchChannelId) =>
            pilotList.Covers(twitchChannelId) || WishBotChannelStateReader.ActivatorIsAdminById(admins, settings);

        // The read only when a channel needs it; it is a 7TV call, so the lock is probed before and after (plan 2.5).
        BotAccountEditorRead? read = null;
        if (channels.Any(c => Covered(c.Settings, c.TwitchChannelId) && WishBotPreflight.ConsentApplies(c.Settings, botId)))
        {
            if (!await session.HoldsLockAsync(cancellationToken))
            {
                return false;
            }

            read = await editorCheck.ReadAsync(botId, account.IdentityVersion, cancellationToken: cancellationToken);
            if (!await session.HoldsLockAsync(cancellationToken))
            {
                return false;
            }

            if (!read.IsAvailable)
            {
                logger.LogInformation("Bot account: the editor read is unavailable; no channel state changes in this pass.");
            }
        }

        foreach (var channel in channels)
        {
            var verdict = WishBotChannelReview.Decide(
                Covered(channel.Settings, channel.TwitchChannelId), channel.Settings, botId, read?.For(channel.TwitchChannelId));
            if (verdict is not { } decided || !NeedsBooking(channel.Settings, decided, read, account))
            {
                continue;
            }

            if (await BookChannelAsync(session, account, channel.Settings.ChannelId, read, cancellationToken) == Booking.Fenced)
            {
                session.ReportLockLost();
                return false;
            }
        }

        return true;
    }

    private async Task<Booking> BookChannelAsync(
        IBotAccountSession session,
        BotAccountAvailabilityState account,
        string channelId,
        BotAccountEditorRead? read,
        CancellationToken cancellationToken)
    {
        string? announce = null;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if ((await db.LockChannelsByIdsAsync([channelId], cancellationToken)).Count == 0)
            {
                return Booking.Missed;
            }

            var row = await db.LockBotAccountForShareAsync(session.RunnerEpoch, cancellationToken);
            if (row is null)
            {
                logger.LogWarning("Bot account: runner epoch {Epoch} was superseded; this pass books nothing more.", session.RunnerEpoch);
                return Booking.Fenced;
            }

            // The verdict belongs to the identity and bot id it was made for (plan 2.3): after a hand PUT it is discarded,
            // after a renewal swap (same identity, same bot id) it still counts.
            if (row.IdentityVersion != account.IdentityVersion
                || !string.Equals(row.Status, BotAccountStatus.Valid, StringComparison.Ordinal)
                || !string.Equals(row.BotSevenTvUserId, account.BotSevenTvUserId, StringComparison.Ordinal))
            {
                return Booking.Missed;
            }

            var settings = await db.ChannelWishSettings.FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
            var channel = await db.Channels.AsNoTracking()
                .Where(c => c.Id == channelId)
                .Select(c => new { c.ChannelName, c.TwitchChannelId })
                .FirstOrDefaultAsync(cancellationToken);
            if (settings is not { BotModeEnabled: true } || channel is null)
            {
                return Booking.Missed;
            }

            var now = Now();
            var listed = wishOptions.PilotList.Covers(channel.TwitchChannelId);
            var activatorIsAdmin = WishBotChannelStateReader.ActivatorIsAdminById(admins, settings);
            var verdict = WishBotChannelReview.Decide(
                listed || activatorIsAdmin, settings, account.BotSevenTvUserId, read?.For(channel.TwitchChannelId));
            if (verdict is not { } decided || !NeedsBooking(settings, decided, read, account))
            {
                return Booking.Missed;
            }

            var modeBefore = WishBotChannelState.Of(settings, account, listed, activatorIsAdmin, now);
            var previousReason = settings.BotSuspendedReason;
            settings.BotSuspendedReason = decided.SuspendedReason;
            if (decided.EditorProven && read is not null)
            {
                settings.BotEditorCheckedAtUtc = read.StartedAtUtc;
                settings.BotEditorCheckedIdentityVersion = read.IdentityVersion;
            }

            if (!string.Equals(previousReason, decided.SuspendedReason, StringComparison.Ordinal))
            {
                db.AddAuditEntry(
                    AuditActor.System,
                    AuditActions.WishBotSuspend,
                    channelName: channel.ChannelName,
                    details: decided.SuspendedReason is { } reason ? new { reason } : (object)new { resumed = true },
                    occurredAtUtc: now);
                logger.LogInformation(
                    "Bot account: channel {ChannelId} {Change}.",
                    channelId,
                    decided.SuspendedReason is { } suspended ? $"suspended ({suspended})" : $"resumed (was {previousReason})");
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (WishBotChannelState.Of(settings, account, listed, activatorIsAdmin, now) != modeBefore)
            {
                announce = channel.ChannelName;
            }
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        if (announce is not null)
        {
            await changedPublisher.PublishAsync(announce, cancellationToken);
        }

        return Booking.Hit;
    }

    // A change of the reason, or a positive read newer than the booked proof (or of another identity): at most one
    // booking per channel and read, so at most one per minute.
    private static bool NeedsBooking(
        ChannelWishSettings settings, WishBotChannelVerdict verdict, BotAccountEditorRead? read, BotAccountAvailabilityState account)
    {
        if (!string.Equals(settings.BotSuspendedReason, verdict.SuspendedReason, StringComparison.Ordinal))
        {
            return true;
        }

        return verdict.EditorProven
            && read is not null
            && (settings.BotEditorCheckedAtUtc is not { } checkedAt
                || checkedAt < read.StartedAtUtc
                || settings.BotEditorCheckedIdentityVersion != account.IdentityVersion);
    }

    private enum Booking
    {
        Hit,
        Missed,
        Fenced,
    }
}
