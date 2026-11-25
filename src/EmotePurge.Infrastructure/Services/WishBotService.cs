using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// <see cref="IWishBotService"/> (plan #487 2.9, 2.10): the numbers of the admin page, and the channel part of the bot
/// mode. The admin numbers count over every channel with plain indexed counts; the bot mode there is the stored
/// activation (<c>BotModeEnabled</c>), not the effective channel state.
/// <para>
/// <b>Channel part.</b> Every write locks the channel row first (<see cref="RowLocks.LockChannelsByIdsAsync"/>, the same
/// lock as every settings writer), reads the settings row only behind it and decides on that; <c>DELETE</c> then locks
/// the channel's affected wish rows ascending, scoped to the channel (<see cref="RowLocks.LockChannelWishesByIdsAsync"/>),
/// and reads them only afterwards. A send transaction of the worker holds the channel row <c>FOR SHARE</c>, so a
/// request of these routes waits for it and decides on what it committed (plan 2.5b). Nothing calls 7TV with the bot's
/// token; the activation filter's step (5) asks the caller's editor grants.
/// </para>
/// </summary>
public sealed class WishBotService(
    AppDbContext db,
    IWishBotAvailability availability,
    WishOptions wishOptions,
    ISevenTvEditorService editorService,
    IModRoleCache roleCache,
    IWishChangedPublisher changedPublisher,
    IBotAccountNudge nudge,
    TimeProvider timeProvider) : IWishBotService
{
    /// <summary>How long a lying activation request keeps its requester when <c>activate</c> is called again (plan 2.9 [E]).</summary>
    public static readonly TimeSpan ActivationRequestReuseWindow = TimeSpan.FromMinutes(10);

    private readonly WishPilotList _pilotList = wishOptions.PilotList;

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

    public async Task<WishBotChannelDto?> GetChannelAsync(
        string channelName, WishBotCaller caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(caller);

        var channel = await FindChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return null;
        }

        var row = await db.ChannelWishSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ChannelId == channel.Id, cancellationToken);
        var account = await availability.GetStateAsync(cancellationToken);
        var listed = _pilotList.Covers(channel.TwitchChannelId);
        var stored = row is null ? WishService.EffectiveSettings.Defaults : WishService.ToSettings(row);

        return new WishBotChannelDto(
            Available: account.IsAvailable,
            Listed: listed,
            Enabled: row?.BotModeEnabled ?? false,
            ActivationPending: row is { BotModeEnabled: false, BotActivationRequestedAtUtc: not null },
            ActivationRequestedAtUtc: row?.BotActivationRequestedAtUtc,
            ActivationError: row?.BotActivationError,
            CanActivate: await CanActivateAsync(channel.TwitchChannelId, listed, caller, cancellationToken),
            CanDeactivate: WishService.CanChangeSettings(stored, channel.TwitchChannelId, caller.TwitchUserId, caller.IsGlobalAdmin),
            SuspendedReason: row?.BotSuspendedReason,
            EditorCheckedAtUtc: row?.BotEditorCheckedAtUtc,
            ActivatedByLogin: row?.BotModeActivatedByLogin,
            ActivatedAtUtc: row?.BotModeActivatedAtUtc,
            BotSevenTvUserId: account.BotSevenTvUserId);
    }

    public async Task<WishBotActivationCheck> CheckActivationAccessAsync(
        string channelName, string callerTwitchUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(callerTwitchUserId);

        var channel = await FindChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return new WishBotActivationCheck(WishBotActivationAccess.Allowed, null);
        }

        // (3) the pilot list, cheap and before any 7TV call; (4) the broadcaster by id; (5) the caller's editor grant for
        // exactly this channel's Twitch id. Every uncertainty refuses (E24): no Twitch id on the row, no 7TV account.
        if (!_pilotList.Covers(channel.TwitchChannelId))
        {
            return new WishBotActivationCheck(WishBotActivationAccess.NotListed, channel.TwitchChannelId);
        }

        if (channel.TwitchChannelId is not { } channelTwitchId)
        {
            return new WishBotActivationCheck(WishBotActivationAccess.Forbidden, null);
        }

        if (string.Equals(channelTwitchId, callerTwitchUserId, StringComparison.Ordinal))
        {
            return new WishBotActivationCheck(WishBotActivationAccess.Allowed, channelTwitchId);
        }

        var grants = await editorService.GetEditorGrantsAsync(callerTwitchUserId, cancellationToken);
        var access = grants.Status switch
        {
            SevenTvLookupStatus.Ok => grants.Grants!.TwitchChannelIds.Contains(channelTwitchId)
                ? WishBotActivationAccess.Allowed
                : WishBotActivationAccess.Forbidden,
            SevenTvLookupStatus.Unavailable => WishBotActivationAccess.EditorUnavailable,
            _ => WishBotActivationAccess.Forbidden,
        };
        return new WishBotActivationCheck(access, channelTwitchId);
    }

    public async Task<WishCommandResult> ActivateAsync(
        string channelName, AuditActor actor, string? checkedTwitchChannelId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(actor);

        var channel = await FindChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var locked = await LockChannelAsync(channel.Id, cancellationToken);
        if (locked is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        // E24 bound to the channel the filter checked: the name is resolved once more here, and a rename and a rejoin may
        // have moved it to another channel since. That channel's editors were never asked. The empty string (the filter
        // found no channel) matches no row, also not one without a Twitch id that a join created meanwhile (plan 2.9).
        if (checkedTwitchChannelId is not null
            && (checkedTwitchChannelId.Length == 0
                || !string.Equals(locked.TwitchChannelId, checkedTwitchChannelId, StringComparison.Ordinal)))
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        // The account behind the channel lock: the consent binds the id this read saw (2.5b, S6-2). Available means
        // valid, so the id is set; a row without one counts as unavailable all the same.
        var account = await availability.GetStateAsync(cancellationToken);
        if (!account.IsAvailable || account.BotSevenTvUserId is not { } botId)
        {
            return WishCommandResult.Of(WishOutcome.BotUnavailable);
        }

        var row = await db.ChannelWishSettings.FirstOrDefaultAsync(s => s.ChannelId == channel.Id, cancellationToken);
        if (row is { BotModeEnabled: true })
        {
            db.ChangeTracker.Clear();
            return WishCommandResult.Of(WishOutcome.BotAlreadyActive);
        }

        if (row is null)
        {
            row = new ChannelWishSettings { ChannelId = channel.Id };
            db.ChannelWishSettings.Add(row);
        }

        var now = Now();
        if (row.BotInvitationAttemptAtUtc is { } attemptedAt
            && !string.Equals(row.BotConsentSevenTvUserId, botId, StringComparison.Ordinal))
        {
            // Revision 6d (K6d-4), 6e (K6e-2): an open acceptance for an earlier bot account is given up, the request
            // binds the current one. The earlier id goes into the audit before it is overwritten.
            db.AddAuditEntry(
                actor,
                AuditActions.WishBotInvitationAbandoned,
                channelName: locked.ChannelName,
                details: new { attemptedAtUtc = attemptedAt, abandonedBotSevenTvUserId = row.BotConsentSevenTvUserId });
            row.BotInvitationAttemptAtUtc = null;
            row.BotInvitationRetryAtUtc = null;
            Request(row, actor, botId, now);
        }
        else if (!(row.BotActivationRequestedAtUtc is { } requestedAt
                   && now - requestedAt < ActivationRequestReuseWindow
                   && string.Equals(row.BotConsentSevenTvUserId, botId, StringComparison.Ordinal)))
        {
            // No lying request, an old one, or one bound to another account: a new request. With an open attempt for the
            // same account the binding is that id again.
            Request(row, actor, botId, now);
        }

        row.BotActivationError = null;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await changedPublisher.PublishAsync(locked.ChannelName, cancellationToken);
        await nudge.NudgeAsync(cancellationToken);
        return WishCommandResult.Of(WishOutcome.Success);
    }

    public async Task<WishCommandResult> DeactivateAsync(
        string channelName, AuditActor actor, bool isGlobalAdmin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(actor);

        var channel = await FindChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var locked = await LockChannelAsync(channel.Id, cancellationToken);
        if (locked is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        var row = await db.ChannelWishSettings.FirstOrDefaultAsync(s => s.ChannelId == channel.Id, cancellationToken);
        if (row is null)
        {
            return WishCommandResult.Of(WishOutcome.Success);
        }

        // D7 again behind the lock (Sol-P2-6): a moderator who passed the filter in the manual flow cannot switch off a
        // bot the worker confirmed in between.
        if (!WishService.CanChangeSettings(WishService.ToSettings(row), locked.TwitchChannelId, actor.TwitchUserId, isGlobalAdmin))
        {
            db.ChangeTracker.Clear();
            return WishCommandResult.Of(WishOutcome.SettingsRestricted);
        }

        var wasEnabled = row.BotModeEnabled;
        if (!wasEnabled && row.BotActivationRequestedAtUtc is null)
        {
            db.ChangeTracker.Clear();
            return WishCommandResult.Of(WishOutcome.Success);
        }

        var now = Now();
        var attemptOpen = row.BotInvitationAttemptAtUtc is not null;
        row.BotModeEnabled = false;
        row.BotModeActivatedByTwitchUserId = null;
        row.BotModeActivatedByLogin = null;
        row.BotModeActivatedAtUtc = null;
        row.BotSuspendedReason = null;
        row.BotActivationRequestedAtUtc = null;
        row.BotActivationRequestedByTwitchUserId = null;
        row.BotActivationRequestedByLogin = null;
        if (attemptOpen)
        {
            // S6-1: the acceptance may still take effect at 7TV; only the runner's read resolves it. Attempt, binding and
            // retry stamp (the "ended in Rejected" mark, K6b-1) stay.
            row.BotActivationError = WishBotChannelReasons.InvitationAcceptUnresolved;
        }
        else
        {
            row.BotConsentSevenTvUserId = null;
            row.BotInvitationRetryAtUtc = null;
        }

        if (wasEnabled)
        {
            await EndOwnershipAsync(channel.Id, now, cancellationToken);
        }

        var details = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!wasEnabled)
        {
            details["pendingOnly"] = true;
        }

        if (attemptOpen)
        {
            details["invitationAcceptUnresolved"] = true;
        }

        db.AddAuditEntry(actor, AuditActions.WishBotDeactivate, channelName: locked.ChannelName, details: details.Count == 0 ? null : details);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await changedPublisher.PublishAsync(locked.ChannelName, cancellationToken);
        return WishCommandResult.Of(WishOutcome.Success);
    }

    public async Task<bool> IsModeAvailableAsync(string channelName, bool callerIsAdminById, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);

        if (!callerIsAdminById)
        {
            var channel = await FindChannelAsync(channelName, cancellationToken);
            if (channel is null || !_pilotList.Covers(channel.TwitchChannelId))
            {
                return false;
            }
        }

        return (await availability.GetStateAsync(cancellationToken)).IsAvailable;
    }

    // Every ownership of the bot in the channel ends (2.9): the wishes it owns, queued removals (also on presented), the
    // attempt's set, and open attempts, which resolve without 7TV first (2.5a (e), K6b-11); the evidence of a removal
    // attempt stays as a repair phase with its own set id. Behind the channel lock no runner starts a send transaction (it
    // needs the channel FOR SHARE); a wish row it holds is waited for, and the re-read behind the lock sees its commit.
    private async Task EndOwnershipAsync(string channelId, DateTime now, CancellationToken cancellationToken)
    {
        var running = WishStatus.Running.ToArray();
        var ids = await db.Wishes
            .Where(w => w.ChannelId == channelId && (running.Contains(w.Status) || w.BotRemoveAtUtc != null))
            .Where(w => w.BotOwned || w.RemovalQueuedAtUtc != null || w.BotSetId != null || w.BotRemoveAtUtc != null
                || w.BotWriteAtUtc != null || w.Status == WishStatus.Unconfirmed)
            .Select(w => w.Id)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return;
        }

        await db.LockChannelWishesByIdsAsync(channelId, ids, cancellationToken);
        var wishes = await db.Wishes
            .Where(w => ids.Contains(w.Id) && w.ChannelId == channelId)
            .OrderBy(w => w.Id)
            .ToListAsync(cancellationToken);
        foreach (var wish in wishes)
        {
            var resolved = WishBotAttemptResolution.Apply(wish, now);
            if (!resolved && !wish.BotOwned && wish.RemovalQueuedAtUtc is null && wish.BotSetId is null)
            {
                continue;
            }

            wish.BotOwned = false;
            wish.BotOwnedAddedAtUtc = null;
            wish.RemovalQueuedAtUtc = null;
            wish.RemovalQueuedVersion = null;
            wish.BotSetId = null;
            wish.DecisionVersion++;
        }
    }

    private async Task<bool?> CanActivateAsync(
        string? channelTwitchId, bool listed, WishBotCaller caller, CancellationToken cancellationToken)
    {
        if (caller.IsGlobalAdminById)
        {
            return true;
        }

        if (!listed || channelTwitchId is null)
        {
            return false;
        }

        if (string.Equals(channelTwitchId, caller.TwitchUserId, StringComparison.Ordinal))
        {
            return true;
        }

        // [E]: no 7TV call on a read. A moderator's answer is known only while the grants are cached.
        var cached = await roleCache.TryGetSevenTvEditorGrantsAsync(caller.TwitchUserId, cancellationToken);
        return cached?.TwitchChannelIds.Contains(channelTwitchId);
    }

    private static void Request(ChannelWishSettings row, AuditActor actor, string botId, DateTime now)
    {
        row.BotActivationRequestedAtUtc = now;
        row.BotActivationRequestedByTwitchUserId = actor.TwitchUserId;
        row.BotActivationRequestedByLogin = actor.Login;
        row.BotConsentSevenTvUserId = botId;
    }

    private async Task<ChannelRef?> FindChannelAsync(string channelName, CancellationToken cancellationToken)
    {
        var normalized = ChannelName.Normalize(channelName);
        return await db.Channels.AsNoTracking()
            .Where(c => c.ChannelName == normalized)
            .Select(c => new ChannelRef(c.Id, c.TwitchChannelId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    // The channel row lock and, read behind it, its current name and Twitch id; null when it was merged or purged meanwhile.
    private async Task<LockedChannel?> LockChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        if ((await db.LockChannelsByIdsAsync([channelId], cancellationToken)).Count == 0)
        {
            return null;
        }

        return await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new LockedChannel(c.ChannelName, c.TwitchChannelId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private sealed record ChannelRef(string Id, string? TwitchChannelId);

    private sealed record LockedChannel(string ChannelName, string? TwitchChannelId);
}
