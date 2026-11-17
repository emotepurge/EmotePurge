using System.Text.RegularExpressions;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The manager-facing wish service: manual entry, "take over from the set", settings and the reads (plan T4),
/// and the transition commands on an existing wish (plan T5). See <see cref="IWishService"/> for the contract.
/// <para>
/// <b>Locks (plan 2.3).</b> Every write opens a transaction and takes the channel row first
/// (<see cref="RowLocks.LockChannelsByIdsAsync"/>, <c>FOR NO KEY UPDATE</c>), because it inserts a row with
/// a channel foreign key or writes a <c>SlotId</c>: without that lock the insert reserves its unique key and
/// then waits for <c>KEY SHARE</c> on the channel while the identity merge, which holds the channel
/// <c>FOR UPDATE</c>, waits for the reserved key. An adoption with a wish id then locks that wish row
/// (<see cref="RowLocks.LockWishesByIdsAsync"/>) and only afterwards reads it, so the state it decides on is
/// the committed one and stays so. Because of that lock the state is stable within the transaction; the
/// unique indexes remain as a belt and map to the conflict outcomes, never to a 500. The transition commands
/// change an existing wish only and therefore take just its row lock (<see cref="RunTransitionAsync"/>).
/// </para>
/// </summary>
public sealed class WishService(AppDbContext db, IWishChangedPublisher changedPublisher, TimeProvider timeProvider) : IWishService
{
    private const string RunningEmoteIndex = "IX_Wishes_ChannelId_SevenTvEmoteId_Running";
    private const string LiveSlotIndex = "IX_Wishes_SlotId_Live";

    private const int MaxPageSize = 200;
    private const decimal ThresholdMin = 0.01m;
    private const decimal ThresholdMax = 1_000_000m;

    private static readonly Regex EmoteIdPattern = new("^[0-9A-Za-z]{1,32}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex LoginPattern = new("^[a-z0-9_]{4,25}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public async Task<WishSettingsDto?> GetSettingsAsync(string channelName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);

        var channelId = await ResolveChannelIdAsync(channelName, cancellationToken);
        return channelId is null ? null : (await LoadSettingsAsync(channelId, cancellationToken)).ToDto();
    }

    public async Task<WishCommandResult> UpdateSettingsAsync(
        string channelName, UpdateWishSettingsRequest request, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        if (!IsDurationInRange(request.DefaultDurationDays)
            || request.ProbationLeadDays is < WishLimits.ProbationLeadDaysMin or > WishLimits.ProbationLeadDaysMax
            || (request.ProtectionThresholdPerDay is { } raw && !IsThresholdInRange(raw)))
        {
            return WishCommandResult.Of(WishOutcome.SettingsOutOfRange);
        }

        var channelId = await ResolveChannelIdAsync(channelName, cancellationToken);
        if (channelId is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var locked = await LockChannelAsync(channelId, cancellationToken);
        if (locked is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        var row = await db.ChannelWishSettings.FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
        if (row is null)
        {
            row = new ChannelWishSettings { ChannelId = channelId };
            db.ChannelWishSettings.Add(row);
        }

        row.DefaultDurationDays = request.DefaultDurationDays;
        row.ProbationLeadDays = request.ProbationLeadDays;
        row.ProtectionThresholdPerDay = request.ProtectionThresholdPerDay is { } threshold ? Math.Round(threshold, 2) : null;

        db.AddAuditEntry(
            actor,
            AuditActions.WishSettings,
            channelName: locked.ChannelName,
            details: new
            {
                defaultDurationDays = row.DefaultDurationDays,
                probationLeadDays = row.ProbationLeadDays,
                protectionThresholdPerDay = row.ProtectionThresholdPerDay
            });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await changedPublisher.PublishAsync(locked.ChannelName, cancellationToken);
        return new WishCommandResult(WishOutcome.Success, Settings: ToSettings(row).ToDto());
    }

    public async Task<bool> CanChangeSettingsAsync(
        string channelName, string callerTwitchUserId, bool isGlobalAdmin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(callerTwitchUserId);

        var normalized = ChannelName.Normalize(channelName);
        var channel = await db.Channels.AsNoTracking()
            .Where(c => c.ChannelName == normalized)
            .Select(c => new { c.Id, c.TwitchChannelId })
            .FirstOrDefaultAsync(cancellationToken);
        if (channel is null)
        {
            return true;
        }

        var settings = await LoadSettingsAsync(channel.Id, cancellationToken);
        if (!settings.IsBotMode || isGlobalAdmin)
        {
            return true;
        }

        return (channel.TwitchChannelId is not null && string.Equals(channel.TwitchChannelId, callerTwitchUserId, StringComparison.Ordinal))
            || (settings.ActivatedByTwitchUserId is not null && string.Equals(settings.ActivatedByTwitchUserId, callerTwitchUserId, StringComparison.Ordinal));
    }

    public async Task<WishCommandResult> CreateAsync(
        string channelName, CreateWishRequest request, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        // The pure input checks, before any lock or query.
        var emoteId = request.SevenTvEmoteId?.Trim() ?? string.Empty;
        if (!EmoteIdPattern.IsMatch(emoteId))
        {
            return WishCommandResult.Of(WishOutcome.EmoteIdInvalid);
        }

        if (request.DurationDays is { } requested && !IsDurationInRange(requested))
        {
            return WishCommandResult.Of(WishOutcome.DurationOutOfRange);
        }

        var alias = request.Alias?.Trim() ?? string.Empty;
        var emoteName = request.EmoteName?.Trim() ?? string.Empty;
        if (alias.Length == 0)
        {
            alias = emoteName;
        }

        if (emoteName.Length == 0)
        {
            emoteName = alias;
        }

        if (!IsValidAlias(alias) || emoteName.EnumerateRunes().Count() > WishLimits.EmoteNameMaxLength || emoteName.Any(char.IsControl))
        {
            return WishCommandResult.Of(WishOutcome.AliasInvalid);
        }

        string? login = null;
        if (!string.IsNullOrWhiteSpace(request.RequesterLogin))
        {
            login = request.RequesterLogin.Trim().ToLowerInvariant();
            if (!LoginPattern.IsMatch(login))
            {
                return WishCommandResult.Of(WishOutcome.RequesterInvalid);
            }
        }

        var channelId = await ResolveChannelIdAsync(channelName, cancellationToken);
        if (channelId is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var channel = await LockChannelAsync(channelId, cancellationToken);
        if (channel is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        // "Already in the set" first, "already running" second: the client offers a different way out of each.
        var slotIds = await ActiveSlotIdsAsync(channelId, emoteId, cancellationToken);
        if (slotIds.Count > 0)
        {
            return new WishCommandResult(WishOutcome.EmoteAlreadyInSet, SlotIds: slotIds);
        }

        var runningId = await FindRunningWishIdAsync(channelId, emoteId, cancellationToken);
        if (runningId is not null)
        {
            return new WishCommandResult(WishOutcome.AlreadyRunning, ConflictingWishId: runningId);
        }

        var settings = await LoadSettingsAsync(channelId, cancellationToken);
        var durationDays = request.DurationDays ?? settings.DefaultDurationDays;
        var now = Now();
        var wish = new Wish
        {
            ChannelId = channelId,
            SevenTvEmoteId = emoteId,
            EmoteName = emoteName,
            Alias = alias,
            Source = WishSource.Manual,
            Status = WishTransitions.TargetForNew(WishAction.Create)!,
            StatusChangedAtUtc = now,
            RequesterLogin = login,
            DurationDays = durationDays,
            CreatedAtUtc = now,
            ApprovedAtUtc = now
        };
        db.Wishes.Add(wish);

        var conflict = await SaveOrConflictAsync(transaction, channelId, emoteId, cancellationToken);
        if (conflict is not null)
        {
            return conflict;
        }

        await AuditAndCommitAsync(transaction, actor, AuditActions.WishCreate, channel.ChannelName, wish, cancellationToken);
        await changedPublisher.PublishAsync(channel.ChannelName, cancellationToken);

        var setFull = channel.ActiveEmoteSetCapacity is { } capacity
            && await db.EmoteSlots.CountAsync(s => !s.IsArchived && !s.SeedUnsettled && s.Emote.ChannelId == channelId, cancellationToken) >= capacity;
        return new WishCommandResult(WishOutcome.Success, Wish: ToDto(wish, settings), SetFull: setFull);
    }

    public async Task<WishCommandResult> AdoptAsync(
        string channelName, AdoptWishRequest request, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        if (request.DurationDays is { } requested && !IsDurationInRange(requested))
        {
            return WishCommandResult.Of(WishOutcome.DurationOutOfRange);
        }

        var channelId = await ResolveChannelIdAsync(channelName, cancellationToken);
        if (channelId is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var channel = await LockChannelAsync(channelId, cancellationToken);
        if (channel is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        Wish? wish = null;
        if (request.WishId is { } wishId)
        {
            // The wish row after the channel row (plan 2.3), and only then read.
            await db.LockWishesByIdsAsync([wishId], cancellationToken);
            wish = await db.Wishes.FirstOrDefaultAsync(w => w.Id == wishId && w.ChannelId == channelId, cancellationToken);
            if (wish is null)
            {
                return WishCommandResult.Of(WishOutcome.WishNotFound);
            }
        }

        var slot = await db.EmoteSlots.Include(s => s.Emote)
            .FirstOrDefaultAsync(s => s.Id == request.SlotId && s.Emote.ChannelId == channelId, cancellationToken);
        if (slot is null || slot.IsArchived || slot.SeedUnsettled
            || (wish is not null && wish.SevenTvEmoteId.Length > 0
                && !string.Equals(wish.SevenTvEmoteId, slot.Emote.SevenTvEmoteId, StringComparison.Ordinal)))
        {
            return WishCommandResult.Of(WishOutcome.SlotNotFound);
        }

        string target;
        if (wish is not null)
        {
            var transition = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Adopt, new WishTransitionContext(Permanent: wish.Permanent));
            if (transition is null)
            {
                return WishCommandResult.Of(WishOutcome.StateConflict);
            }

            target = transition;
        }
        else
        {
            target = WishTransitions.TargetForNew(WishAction.AdoptNew)!;
        }

        var ownId = wish?.Id;
        var live = new[] { WishStatus.Active, WishStatus.Presented };
        var linked = await db.Wishes.AnyAsync(w => w.SlotId == slot.Id && live.Contains(w.Status) && w.Id != ownId, cancellationToken);
        if (linked)
        {
            return WishCommandResult.Of(WishOutcome.SlotAlreadyLinked);
        }

        var running = await FindRunningWishIdAsync(channelId, slot.Emote.SevenTvEmoteId, cancellationToken);
        if (running is not null && running != ownId)
        {
            return new WishCommandResult(WishOutcome.AlreadyRunning, ConflictingWishId: running);
        }

        var now = Now();
        var start = ResolveStart(slot, request.StartsAtUtc, now, out var authoritative);
        if (start is null)
        {
            return WishCommandResult.Of(WishOutcome.StartsAtInvalid);
        }

        var settings = await LoadSettingsAsync(channelId, cancellationToken);
        var durationDays = request.DurationDays ?? wish?.DurationDays ?? settings.DefaultDurationDays;
        if (wish is null)
        {
            wish = new Wish
            {
                ChannelId = channelId,
                SevenTvEmoteId = slot.Emote.SevenTvEmoteId,
                EmoteName = slot.Emote.Name,
                Source = WishSource.Adopted,
                CreatedAtUtc = now
            };
            db.Wishes.Add(wish);
        }
        else
        {
            // A command: E25, every manager action counts and drops a queued bot removal.
            wish.DecisionVersion++;
            wish.RemovalQueuedAtUtc = null;
            wish.RemovalQueuedVersion = null;
        }

        if (wish.SevenTvEmoteId.Length == 0)
        {
            wish.SevenTvEmoteId = slot.Emote.SevenTvEmoteId;
        }

        if (wish.EmoteName.Length == 0)
        {
            wish.EmoteName = slot.Emote.Name;
        }

        wish.Alias = slot.Name;
        wish.Status = target;
        wish.StatusReason = null;
        wish.StatusChangedAtUtc = now;
        wish.EndedAtUtc = WishStatus.IsEnded(target) ? now : null;
        wish.DurationDays = durationDays;
        wish.SlotId = slot.Id;
        wish.LinkOrigin = WishLinkOrigin.Adopted;
        wish.SlotAddedAtUtc = slot.AddedAt;
        wish.SlotConfirmedAtUtc = now;
        wish.StartsAtUtc = start;
        wish.StartsAtAuthoritative = authoritative;
        wish.ExpiresAtUtc = WishSchedule.ExpiresAt(start.Value, durationDays);
        wish.BotOwned = false;
        wish.BotOwnedAddedAtUtc = null;
        if (target == WishStatus.Active)
        {
            wish.FirstActivatedAtUtc ??= now;
        }

        var conflict = await SaveOrConflictAsync(transaction, channelId, wish.SevenTvEmoteId, cancellationToken);
        if (conflict is not null)
        {
            return conflict;
        }

        await AuditAndCommitAsync(transaction, actor, AuditActions.WishAdopt, channel.ChannelName, wish, cancellationToken);
        await changedPublisher.PublishAsync(channel.ChannelName, cancellationToken);
        return new WishCommandResult(WishOutcome.Success, Wish: ToDto(wish, settings));
    }

    public Task<WishCommandResult> UpdateAsync(
        string channelName, long wishId, UpdateWishRequest request, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.DurationDays is { } days && !IsDurationInRange(days))
        {
            return Task.FromResult(WishCommandResult.Of(WishOutcome.DurationOutOfRange));
        }

        var alias = request.Alias?.Trim();
        if (alias is not null && !IsValidAlias(alias))
        {
            return Task.FromResult(WishCommandResult.Of(WishOutcome.AliasInvalid));
        }

        return RunTransitionAsync(channelName, wishId, actor, AuditActions.WishUpdate, (wish, _, _, _) =>
        {
            var target = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Edit);
            if (target is null)
            {
                return CommandStep.ConflictTask;
            }

            wish.Status = target;
            wish.Alias = alias ?? wish.Alias;
            wish.DurationDays = request.DurationDays ?? wish.DurationDays;
            return CommandStep.DoneTask;
        }, cancellationToken);
    }

    public Task<WishCommandResult> ExtendAsync(string channelName, long wishId, AuditActor actor, CancellationToken cancellationToken = default) =>
        RunTransitionAsync(channelName, wishId, actor, AuditActions.WishExtend, (wish, settings, now, _) =>
        {
            // Only a wish with an expiry can be extended; one without changes its duration through PATCH.
            var target = WishTransitions.Target(
                wish.Status, wish.StatusReason, WishAction.Extend, new WishTransitionContext(HasExpiry: wish.ExpiresAtUtc is not null));
            if (target is null || wish.ExpiresAtUtc is not { } expiresAt)
            {
                return CommandStep.ConflictTask;
            }

            // E12: by the channel's default, from the current expiry or from now when that has passed.
            var extensionDays = settings.DefaultDurationDays;
            wish.Status = target;
            wish.ExpiresAtUtc = WishSchedule.ExtendedExpiresAt(expiresAt, now, extensionDays);
            wish.ExtensionCount++;
            return Task.FromResult(new CommandStep(null, AuditDurationDays: extensionDays));
        }, cancellationToken);

    public Task<WishCommandResult> KeepAsync(string channelName, long wishId, AuditActor actor, CancellationToken cancellationToken = default) =>
        RunTransitionAsync(channelName, wishId, actor, AuditActions.WishKeep, (wish, _, _, _) =>
        {
            var target = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Keep);
            if (target is null)
            {
                return CommandStep.ConflictTask;
            }

            // The entry becomes an ordinary one: bot ownership ends.
            wish.Status = target;
            wish.BotOwned = false;
            wish.BotOwnedAddedAtUtc = null;
            return CommandStep.DoneTask;
        }, cancellationToken);

    public Task<WishCommandResult> CloseAsync(string channelName, long wishId, AuditActor actor, CancellationToken cancellationToken = default) =>
        RunTransitionAsync(channelName, wishId, actor, AuditActions.WishClose, (wish, _, _, _) =>
        {
            var target = WishTransitions.Target(
                wish.Status, wish.StatusReason, WishAction.Close, new WishTransitionContext(HasSlot: wish.SlotId is not null));
            if (target is null)
            {
                return CommandStep.ConflictTask;
            }

            wish.Status = target;
            return CommandStep.DoneTask;
        }, cancellationToken);

    public Task<WishCommandResult> RetryAsync(string channelName, long wishId, AuditActor actor, CancellationToken cancellationToken = default) =>
        RunTransitionAsync(channelName, wishId, actor, AuditActions.WishRetry, (wish, _, now, _) =>
        {
            var target = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Retry);
            if (target is null)
            {
                return CommandStep.ConflictTask;
            }

            // The manager's click is the approval (R3-12). The bot's attempt fields are K4's to reset.
            wish.Status = target;
            wish.ApprovedAtUtc = now;
            return CommandStep.DoneTask;
        }, cancellationToken);

    public Task<WishCommandResult> ReAddAsync(
        string channelName, long wishId, ReAddWishRequest request, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var permanent = string.Equals(request.Mode, WishReAddMode.Permanent, StringComparison.Ordinal);
        if (!permanent && !string.Equals(request.Mode, WishReAddMode.Timed, StringComparison.Ordinal))
        {
            return Task.FromResult(WishCommandResult.Of(WishOutcome.ModeInvalid));
        }

        if (request.DurationDays is { } days && !IsDurationInRange(days))
        {
            return Task.FromResult(WishCommandResult.Of(WishOutcome.DurationOutOfRange));
        }

        return RunTransitionAsync(channelName, wishId, actor, AuditActions.WishReAdd, async (wish, settings, now, ct) =>
        {
            var target = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.ReAdd);
            if (target is null)
            {
                return CommandStep.Conflict;
            }

            // E13 under the row lock; without the channel lock a concurrent manual entry can still slip in, and
            // the running index catches that on save (SaveOrConflictAsync).
            var running = await FindRunningWishIdAsync(wish.ChannelId, wish.SevenTvEmoteId, ct);
            if (running is not null && running != wish.Id)
            {
                return new CommandStep(new WishCommandResult(WishOutcome.AlreadyRunning, ConflictingWishId: running));
            }

            // A new run: fresh approval, duration or permanent, no start, expiry or slot generation until the
            // reconcile links a slot again. SlotId and FirstActivatedAtUtc are history and stay (plan T5, finding 20).
            wish.Status = target;
            wish.Permanent = permanent;
            wish.DurationDays = request.DurationDays ?? (permanent ? wish.DurationDays : settings.DefaultDurationDays);
            wish.ApprovedAtUtc = now;
            wish.StartsAtUtc = null;
            wish.StartsAtAuthoritative = false;
            wish.ExpiresAtUtc = null;
            wish.ExtensionCount = 0;
            wish.SlotAddedAtUtc = null;
            wish.SlotConfirmedAtUtc = null;
            wish.BotOwned = false;
            wish.BotOwnedAddedAtUtc = null;
            return CommandStep.Done;
        }, cancellationToken);
    }

    public Task<WishCommandResult> CompleteRemovalAsync(
        string channelName, long wishId, AuditActor actor, CancellationToken cancellationToken = default) =>
        RunTransitionAsync(channelName, wishId, actor, AuditActions.WishRemove, (wish, _, now, _) =>
        {
            var target = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Remove);
            if (target is null)
            {
                return CommandStep.ConflictTask;
            }

            // The race between sync and success report (AK11): an external removal the reconcile saw first turns
            // into our removal only within the adoption window after it was observed.
            if (wish.Status == WishStatus.RemovedExternally
                && (wish.EndedAtUtc is not { } endedAt || now > endedAt + WishLimits.AdoptionWindow))
            {
                return CommandStep.ConflictTask;
            }

            wish.Status = target;
            return CommandStep.DoneTask;
        }, cancellationToken);

    public async Task<WishCommandResult> RecheckAsync(
        string channelName, long wishId, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(actor);

        var channelId = await ResolveChannelIdAsync(channelName, cancellationToken);
        if (channelId is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        var exists = await db.Wishes.AsNoTracking().AnyAsync(w => w.Id == wishId && w.ChannelId == channelId, cancellationToken);
        if (!exists)
        {
            return WishCommandResult.Of(WishOutcome.WishNotFound);
        }

        // Plan finding 8: the only transition (presented/unconfirmed -> unconfirmed) needs an available bot, which
        // K1 does not have, so every state is a conflict and nothing is locked or written. K4 replaces this with
        // the WishTransitions lookup and its own wish_bot_unavailable.
        return WishCommandResult.Of(WishOutcome.StateConflict);
    }

    public async Task<PagedResult<WishDto>?> ListAsync(
        string channelName, WishListView view, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);

        var channelId = await ResolveChannelIdAsync(channelName, cancellationToken);
        if (channelId is null)
        {
            return null;
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var settings = await LoadSettingsAsync(channelId, cancellationToken);
        var statuses = StatusesOf(view, settings.IsBotMode);
        var query = db.Wishes.AsNoTracking().Where(w => w.ChannelId == channelId && statuses.Contains(w.Status));
        var ordered = view == WishListView.Ended
            ? query.OrderByDescending(w => w.EndedAtUtc).ThenByDescending(w => w.Id)
            : query.OrderBy(w => w.ExpiresAtUtc).ThenByDescending(w => w.CreatedAtUtc).ThenBy(w => w.Id);

        var total = await query.CountAsync(cancellationToken);
        var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<WishDto>(rows.Select(w => ToDto(w, settings)).ToList(), page, pageSize, total);
    }

    public async Task<WishDto?> GetAsync(string channelName, long wishId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);

        var channelId = await ResolveChannelIdAsync(channelName, cancellationToken);
        if (channelId is null)
        {
            return null;
        }

        var wish = await db.Wishes.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wishId && w.ChannelId == channelId, cancellationToken);
        return wish is null ? null : ToDto(wish, await LoadSettingsAsync(channelId, cancellationToken));
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private Task<string?> ResolveChannelIdAsync(string channelName, CancellationToken cancellationToken)
    {
        var normalized = ChannelName.Normalize(channelName);
        return db.Channels.AsNoTracking()
            .Where(c => c.ChannelName == normalized)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // The channel row lock (plan 2.3) and, read behind it, what the write needs of the channel. Null when the
    // channel was merged or purged while this waited.
    private async Task<LockedChannel?> LockChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        var locked = await db.LockChannelsByIdsAsync([channelId], cancellationToken);
        if (locked.Count == 0)
        {
            return null;
        }

        return await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new LockedChannel(c.ChannelName, c.ActiveEmoteSetCapacity))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<EffectiveSettings> LoadSettingsAsync(string channelId, CancellationToken cancellationToken)
    {
        var row = await db.ChannelWishSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
        return row is null ? EffectiveSettings.Defaults : ToSettings(row);
    }

    // Slots of this emote that are in the set right now: not archived and not an unsettled migration seed.
    private async Task<IReadOnlyList<string>> ActiveSlotIdsAsync(string channelId, string sevenTvEmoteId, CancellationToken cancellationToken)
    {
        var ids = await db.EmoteSlots
            .Where(s => !s.IsArchived && !s.SeedUnsettled && s.Emote.ChannelId == channelId && s.Emote.SevenTvEmoteId == sevenTvEmoteId)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    private Task<long?> FindRunningWishIdAsync(string channelId, string sevenTvEmoteId, CancellationToken cancellationToken)
    {
        var running = WishStatus.Running.ToArray();
        return db.Wishes
            .Where(w => w.ChannelId == channelId && w.SevenTvEmoteId == sevenTvEmoteId && running.Contains(w.Status))
            .Select(w => (long?)w.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Saves the staged wish. A hit on one of the two partial unique indexes is a conflict, not an error: the
    // channel lock makes it unreachable in practice, the index is the belt. Rolls the transaction back then.
    private async Task<WishCommandResult?> SaveOrConflictAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        string channelId,
        string sevenTvEmoteId,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException ex) when (UniqueViolationOutcome(ex) is { } outcome)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            long? runningId = outcome == WishOutcome.AlreadyRunning
                ? await FindRunningWishIdAsync(channelId, sevenTvEmoteId, cancellationToken)
                : null;
            return new WishCommandResult(outcome, ConflictingWishId: runningId);
        }
    }

    // The transition commands (plan T5, 2.3). They change an existing wish only, so they take only its row lock,
    // no channel lock: lock FOR UPDATE, read only then (scoped to the channel), let the command decide on that
    // committed state and change the tracked row. The lock replaces a state predicate in the UPDATE: a command
    // that waited sees the state the other one committed and decides on it. On success every command counts the
    // decision and drops a queued bot removal (E25), stamps a state change, and saves, audits and commits in one
    // transaction; one wish.changed follows the commit. A refusal leaves the transaction to roll back.
    private async Task<WishCommandResult> RunTransitionAsync(
        string channelName,
        long wishId,
        AuditActor actor,
        string auditAction,
        Func<Wish, EffectiveSettings, DateTime, CancellationToken, Task<CommandStep>> decide,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(actor);

        var channelId = await ResolveChannelIdAsync(channelName, cancellationToken);
        if (channelId is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.LockWishesByIdsAsync([wishId], cancellationToken);
        var wish = await db.Wishes.FirstOrDefaultAsync(w => w.Id == wishId && w.ChannelId == channelId, cancellationToken);
        if (wish is null)
        {
            return WishCommandResult.Of(WishOutcome.WishNotFound);
        }

        var settings = await LoadSettingsAsync(channelId, cancellationToken);
        var now = Now();
        var previousStatus = wish.Status;
        var step = await decide(wish, settings, now, cancellationToken);
        if (step.Refusal is not null)
        {
            db.ChangeTracker.Clear();
            return step.Refusal;
        }

        wish.DecisionVersion++;
        wish.RemovalQueuedAtUtc = null;
        wish.RemovalQueuedVersion = null;
        if (!string.Equals(previousStatus, wish.Status, StringComparison.Ordinal))
        {
            // No target of a manager command carries a reason; an end state starts the retention clock, the way
            // back out of one clears it.
            wish.StatusChangedAtUtc = now;
            wish.StatusReason = null;
            wish.EndedAtUtc = WishStatus.IsEnded(wish.Status) ? now : null;
        }

        var conflict = await SaveOrConflictAsync(transaction, channelId, wish.SevenTvEmoteId, cancellationToken);
        if (conflict is not null)
        {
            return conflict;
        }

        var channel = ChannelName.Normalize(channelName);
        await AuditAndCommitAsync(transaction, actor, auditAction, channel, wish, cancellationToken, step.AuditDurationDays);
        await changedPublisher.PublishAsync(channel, cancellationToken);
        return new WishCommandResult(WishOutcome.Success, Wish: ToDto(wish, settings));
    }

    // The audit entry needs the wish id, which an insert only has after its first save; both saves are in
    // the one transaction, so the entry exists if and only if the command committed. durationDays is the wish's
    // duration, or what the command says instead (an extension records its length).
    private async Task AuditAndCommitAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        AuditActor actor,
        string action,
        string channelName,
        Wish wish,
        CancellationToken cancellationToken,
        int? auditDurationDays = null)
    {
        db.AddAuditEntry(
            actor,
            action,
            channelName: channelName,
            targetType: "wish",
            targetId: wish.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            details: new { wishId = wish.Id, sevenTvEmoteId = wish.SevenTvEmoteId, durationDays = auditDurationDays ?? wish.DurationDays });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static WishOutcome? UniqueViolationOutcome(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
            ? postgres.ConstraintName switch
            {
                RunningEmoteIndex => WishOutcome.AlreadyRunning,
                LiveSlotIndex => WishOutcome.SlotAlreadyLinked,
                _ => null
            }
            : null;

    // The start of an adoption (plan finding 7): the explicit date if it lies between the slot's AddedAt and
    // now; else the slot's AddedAt; else, for an undated slot, now. Authoritative only when the slot's own date
    // is, so the reconcile can still correct a provisional start. Null = the explicit date is not acceptable.
    private static DateTime? ResolveStart(EmoteSlot slot, DateTime? requested, DateTime now, out bool authoritative)
    {
        authoritative = slot.AddedAtFromRest && slot.AddedAt is not null;
        if (requested is { } explicitStart)
        {
            var utc = explicitStart.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(explicitStart, DateTimeKind.Utc)
                : explicitStart.ToUniversalTime();
            return utc > now || (slot.AddedAt is { } addedAt && utc < addedAt) ? null : utc;
        }

        if (slot.AddedAt is { } added)
        {
            return added;
        }

        return now;
    }

    private static string[] StatusesOf(WishListView view, bool botMode)
    {
        // An approved wish needs a human while the channel runs manually; in bot mode it is the bot's turn.
        var approvedNeedsAction = !botMode;
        string[] statuses = view switch
        {
            WishListView.Open => approvedNeedsAction
                ? [WishStatus.Pending, WishStatus.Failed, WishStatus.Presented, WishStatus.Approved]
                : [WishStatus.Pending, WishStatus.Failed, WishStatus.Presented],
            WishListView.Running => approvedNeedsAction
                ? [WishStatus.Active, WishStatus.Unconfirmed]
                : [WishStatus.Active, WishStatus.Unconfirmed, WishStatus.Approved],
            _ => [.. WishStatus.Ended]
        };
        return statuses;
    }

    private static bool IsDurationInRange(int days) => days is >= WishLimits.DurationDaysMin and <= WishLimits.DurationDaysMax;

    private static bool IsThresholdInRange(decimal value) => value >= ThresholdMin && value <= ThresholdMax;

    // Length counts Unicode code points like 7TV and the frontend's alias check, not UTF-16 units.
    private static bool IsValidAlias(string alias) =>
        alias.EnumerateRunes().Count() is >= 1 and <= WishLimits.AliasMaxLength
        && !alias.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));

    private static EffectiveSettings ToSettings(ChannelWishSettings row) => new(
        row.DefaultDurationDays,
        row.ProbationLeadDays,
        row.ProtectionThresholdPerDay,
        row.BotModeEnabled,
        row.BotSuspendedReason,
        row.BotModeActivatedByTwitchUserId);

    private static WishDto ToDto(Wish w, EffectiveSettings settings) => new(
        w.Id,
        w.SevenTvEmoteId,
        w.EmoteName,
        w.Alias,
        w.Source,
        w.Status,
        w.StatusReason,
        w.RequesterLogin,
        w.DurationDays,
        w.CreatedAtUtc,
        w.ApprovedAtUtc,
        w.StartsAtUtc,
        w.StartsAtAuthoritative,
        w.StartsAtUtc is { } start && w.ExpiresAtUtc is { } expires
            ? WishSchedule.ProbationStartsAt(start, expires, settings.ProbationLeadDays)
            : null,
        w.ExpiresAtUtc,
        w.EndedAtUtc,
        w.SlotId,
        w.LinkOrigin,
        w.ExtensionCount,
        w.Permanent,
        w.BotOwned,
        w.BotWriteAtUtc is not null && w.Status != WishStatus.Active,
        w.BotNextAttemptAtUtc,
        w.RemovalQueuedAtUtc,
        settings.IsBotMode ? "bot" : "manual");

    private sealed record LockedChannel(string ChannelName, int? ActiveEmoteSetCapacity);

    // What a transition command decided: a refusal, or success with an optional durationDays for the audit entry.
    private sealed record CommandStep(WishCommandResult? Refusal, int? AuditDurationDays = null)
    {
        public static readonly CommandStep Done = new((WishCommandResult?)null);
        public static readonly CommandStep Conflict = new(WishCommandResult.Of(WishOutcome.StateConflict));
        public static readonly Task<CommandStep> DoneTask = Task.FromResult(Done);
        public static readonly Task<CommandStep> ConflictTask = Task.FromResult(Conflict);
    }

    private sealed record EffectiveSettings(
        int DefaultDurationDays,
        int ProbationLeadDays,
        decimal? ProtectionThresholdPerDay,
        bool BotModeEnabled,
        string? BotSuspendedReason,
        string? ActivatedByTwitchUserId)
    {
        public static readonly EffectiveSettings Defaults = new(
            WishLimits.DefaultDurationDays, WishLimits.DefaultProbationLeadDays, null, false, null, null);

        // In K1 nothing sets BotModeEnabled, so every channel is manual.
        public bool IsBotMode => BotModeEnabled && BotSuspendedReason is null;

        public WishSettingsDto ToDto() => new(
            DefaultDurationDays, ProbationLeadDays, ProtectionThresholdPerDay, BotModeEnabled, BotSuspendedReason);
    }
}
