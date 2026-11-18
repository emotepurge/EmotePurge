using System.Linq.Expressions;
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
/// change an existing wish only and therefore take just its row lock (<see cref="RunTransitionAsync"/>); the
/// removal report is the exception, it reads the slot inventory and so takes the channel row first like every
/// slot writer (<see cref="CompleteRemovalAsync"/>, plan #485 2.1a). A
/// settings change that changes the protection threshold locks, after the channel row, the channel's active
/// wish rows as well (<see cref="SupersedeActiveDecisionsAsync"/>, plan #484 2.6).
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

    // The states of IX_Wishes_SlotId_Live: a kept wish holds its entry like a live one.
    private static readonly string[] HoldingStatuses = [WishStatus.Active, WishStatus.Presented, WishStatus.Kept];

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
            || (!request.KeepProtectionThreshold && request.ProtectionThresholdPerDay is { } requested && !IsThresholdInRange(requested)))
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

        // A missing row is "protection off", which is also what the fresh row above carries. A threshold that was
        // not sent keeps the stored one (plan #485 2.1a): unchanged, so no decision is superseded.
        var previousThreshold = row.ProtectionThresholdPerDay;
        var threshold = request.KeepProtectionThreshold
            ? previousThreshold
            : request.ProtectionThresholdPerDay is { } raw ? Math.Round(raw, 2) : (decimal?)null;
        if (threshold != previousThreshold)
        {
            await SupersedeActiveDecisionsAsync(channelId, cancellationToken);
        }

        row.DefaultDurationDays = request.DefaultDurationDays;
        row.ProbationLeadDays = request.ProbationLeadDays;
        row.ProtectionThresholdPerDay = threshold;

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
            // Plan #485 2.1a: the running wish of the emote (or null), so the client reaches it off the loaded page.
            var running = await FindRunningWishIdAsync(channelId, emoteId, cancellationToken);
            return new WishCommandResult(WishOutcome.EmoteAlreadyInSet, SlotIds: slotIds, ConflictingWishId: running);
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
        var imageUrl = await ImageUrlOfAsync(channelId, emoteId, cancellationToken);
        return new WishCommandResult(WishOutcome.Success, Wish: ToDto(wish, settings, imageUrl), SetFull: setFull);
    }

    public async Task<WishCommandResult> AdoptAsync(
        string channelName, AdoptWishRequest request, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        // The entry the manager picked is named by alias and generation (plan #485 2.1a); without the alias the
        // body does not say which stay it means.
        if (request.ExpectedAlias is null)
        {
            return WishCommandResult.Of(WishOutcome.RequestInvalid);
        }

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
            .Where(AdoptableIn(channelId))
            .FirstOrDefaultAsync(s => s.Id == request.SlotId, cancellationToken);
        // Bound to the picked generation, before any other check on the slot (Sol-R2-P2-4): the same slot id under
        // another alias or with another AddedAt (a revived row, a date that arrived meanwhile) is not the entry the
        // manager saw, so it "does not exist like that any more".
        if (slot is null
            || !string.Equals(slot.Name, request.ExpectedAlias, StringComparison.Ordinal)
            || !SameInstant(slot.AddedAt, request.ExpectedAddedAtUtc)
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
        var holderId = await FindHolderIdAsync(slot.Id, ownId, cancellationToken);
        if (holderId is not null)
        {
            return new WishCommandResult(WishOutcome.SlotAlreadyLinked, ConflictingWishId: holderId);
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

        var conflict = await SaveOrConflictAsync(transaction, channelId, wish.SevenTvEmoteId, cancellationToken, slot.Id);
        if (conflict is not null)
        {
            return conflict;
        }

        await AuditAndCommitAsync(transaction, actor, AuditActions.WishAdopt, channel.ChannelName, wish, cancellationToken);
        await changedPublisher.PublishAsync(channel.ChannelName, cancellationToken);
        // The slot's emote is the channel's row of the wish's emote: its image needs no further query.
        var imageUrl = WishImageQueries.WishImageUrlOf(slot.Emote.ImageUrl, slot.Emote.IsPlaceholder);
        return new WishCommandResult(WishOutcome.Success, Wish: ToDto(wish, settings, imageUrl));
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
        }, cancellationToken, recordPreviousExpiry: true);

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
        }, cancellationToken, recordPreviousExpiry: true);

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
        }, cancellationToken, recordPreviousExpiry: true);

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

    public async Task<WishCommandResult> CompleteRemovalAsync(
        string channelName, long wishId, CompleteWishRemovalRequest request, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        // Row 1 (plan #485 2.1a): the report names slot, generation and set; evidence is optional but closed.
        if (string.IsNullOrEmpty(request.SlotId)
            || request.SlotAddedAtUtc is not { } reportedGeneration
            || string.IsNullOrEmpty(request.EmoteSetId)
            || (request.Evidence is not null
                && request.Evidence is not (WishRemovalEvidence.Acknowledged or WishRemovalEvidence.Absent)))
        {
            return WishCommandResult.Of(WishOutcome.RequestInvalid);
        }

        var channelId = await ResolveChannelIdAsync(channelName, cancellationToken);
        if (channelId is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        // The channel row before the wish row, and every read behind both (Sol-R2-P1-2): every slot writer holds the
        // channel row for its inventory transaction, so the report sees the inventory before or after a sync,
        // never halfway. Not RunTransitionAsync, which takes only the wish row.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if ((await db.LockChannelsByIdsAsync([channelId], cancellationToken)).Count == 0)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        await db.LockWishesByIdsAsync([wishId], cancellationToken);
        var channel = await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new { c.ChannelName, c.ActiveEmoteSetId })
            .SingleAsync(cancellationToken);
        var wish = await db.Wishes.FirstOrDefaultAsync(w => w.Id == wishId && w.ChannelId == channelId, cancellationToken);

        // Row 2.
        if (wish is null)
        {
            return WishCommandResult.Of(WishOutcome.WishNotFound);
        }

        // Row 3: the set is proven only by being the channel's active set right now; slots are entries of it.
        // Row 4: the reported generation must be the one the wish is bound to (also: re-bound, reset by a re-add).
        if (!string.Equals(request.EmoteSetId, channel.ActiveEmoteSetId, StringComparison.Ordinal)
            || !string.Equals(request.SlotId, wish.SlotId, StringComparison.Ordinal)
            || !SameInstant(wish.SlotAddedAtUtc, reportedGeneration))
        {
            return WishCommandResult.Of(WishOutcome.StateConflict);
        }

        var settings = await LoadSettingsAsync(channelId, cancellationToken);

        // Row 5: already booked (a lost 200, a second report of the same run): the same answer, no write at all.
        if (wish.Status == WishStatus.Removed)
        {
            var bookedImage = await ImageUrlOfAsync(channelId, wish.SevenTvEmoteId, cancellationToken);
            return new WishCommandResult(WishOutcome.Success, Wish: ToDto(wish, settings, bookedImage));
        }

        // Row 6: the pending wish reconcile would bind the wish to an entry the inventory already shows.
        if ((wish.Status is WishStatus.Active or WishStatus.Presented or WishStatus.RemovedExternally)
            && await ReplacementVisibleAsync(channelId, wish, reportedGeneration, cancellationToken))
        {
            return WishCommandResult.Of(WishOutcome.StateConflict);
        }

        // Rows 7 to 10 through the transition table: active, presented and removed_externally go to removed, every
        // other state is a conflict. An external removal the reconcile saw first becomes ours only within the
        // adoption window after it was observed (AK11).
        var now = Now();
        var target = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Remove);
        if (target is null
            || (wish.Status == WishStatus.RemovedExternally
                && (wish.EndedAtUtc is not { } endedAt || now > endedAt + WishLimits.AdoptionWindow)))
        {
            return WishCommandResult.Of(WishOutcome.StateConflict);
        }

        var previousExpiry = wish.ExpiresAtUtc is { } expiry && expiry <= now ? expiry : (DateTime?)null;
        var previousStatus = wish.Status;
        wish.Status = target;
        CountDecision(wish);
        StampStateChange(wish, previousStatus, now);

        var conflict = await SaveOrConflictAsync(transaction, channelId, wish.SevenTvEmoteId, cancellationToken);
        if (conflict is not null)
        {
            return conflict;
        }

        // A paper trail of what was reported, not a basis for any check: ids and one instant, no names (AK19).
        var removal = new RemovalReport(wish.SlotId!, wish.SlotAddedAtUtc!.Value, request.EmoteSetId, request.Evidence);
        await AuditAndCommitAsync(
            transaction, actor, AuditActions.WishRemove, channel.ChannelName, wish, cancellationToken, previousExpiresAtUtc: previousExpiry,
            removal: removal);
        await changedPublisher.PublishAsync(channel.ChannelName, cancellationToken);
        var imageUrl = await ImageUrlOfAsync(channelId, wish.SevenTvEmoteId, cancellationToken);
        return new WishCommandResult(WishOutcome.Success, Wish: ToDto(wish, settings, imageUrl));
    }

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
        var images = await db.ReadWishImageUrlsAsync([channelId], rows.Select(w => w.SevenTvEmoteId).ToList(), cancellationToken);
        var items = rows.Select(w => ToDto(w, settings, images.GetValueOrDefault((channelId, w.SevenTvEmoteId)))).ToList();
        return new PagedResult<WishDto>(items, page, pageSize, total);
    }

    public async Task<WishDetailDto?> GetAsync(string channelName, long wishId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);

        var channelId = await ResolveChannelIdAsync(channelName, cancellationToken);
        if (channelId is null)
        {
            return null;
        }

        var wish = await db.Wishes.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wishId && w.ChannelId == channelId, cancellationToken);
        if (wish is null)
        {
            return null;
        }

        var settings = await LoadSettingsAsync(channelId, cancellationToken);
        var dto = ToDto(wish, settings, await ImageUrlOfAsync(channelId, wish.SevenTvEmoteId, cancellationToken));
        return new WishDetailDto(dto, await LoadUsageAsync(wish, channelId, Now(), cancellationToken));
    }

    public async Task<IReadOnlyList<WishAdoptableSlotDto>?> ListAdoptableSlotsAsync(
        string channelName, string? sevenTvEmoteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);

        var channelId = await ResolveChannelIdAsync(channelName, cancellationToken);
        if (channelId is null)
        {
            return null;
        }

        var emoteFilter = string.IsNullOrWhiteSpace(sevenTvEmoteId) ? null : sevenTvEmoteId.Trim();
        var query = db.EmoteSlots.AsNoTracking().Where(AdoptableIn(channelId));
        if (emoteFilter is not null)
        {
            query = query.Where(s => s.Emote.SevenTvEmoteId == emoteFilter);
        }

        var slots = await query
            .Select(s => new
            {
                s.Id,
                s.Emote.SevenTvEmoteId,
                s.Name,
                s.AddedAt,
                s.AddedAtFromRest,
                s.Emote.ImageUrl,
                s.Emote.IsPlaceholder
            })
            .ToListAsync(cancellationToken);
        if (slots.Count == 0)
        {
            return [];
        }

        // One read of the wishes adopt would run into: a holder of one of these slots (by slot id, as adopt
        // asks), or a running wish of one of these emotes in the channel.
        var slotIds = slots.Select(s => s.Id).ToList();
        var emoteIds = slots.Select(s => s.SevenTvEmoteId).Distinct(StringComparer.Ordinal).ToList();
        var running = WishStatus.Running.ToArray();
        var wishes = await db.Wishes.AsNoTracking()
            .Where(w => (w.SlotId != null && slotIds.Contains(w.SlotId) && HoldingStatuses.Contains(w.Status))
                || (w.ChannelId == channelId && emoteIds.Contains(w.SevenTvEmoteId) && running.Contains(w.Status)))
            .Select(w => new { w.Id, w.ChannelId, w.SevenTvEmoteId, w.Status, w.StatusReason, w.SlotId, w.Permanent })
            .OrderBy(w => w.Id)
            .ToListAsync(cancellationToken);

        return slots
            .Select(slot =>
            {
                var holder = wishes.FirstOrDefault(w =>
                    HoldingStatuses.Contains(w.Status) && string.Equals(w.SlotId, slot.Id, StringComparison.Ordinal));
                var other = wishes.FirstOrDefault(w =>
                    w.ChannelId == channelId
                    && WishStatus.IsRunning(w.Status)
                    && string.Equals(w.SevenTvEmoteId, slot.SevenTvEmoteId, StringComparison.Ordinal)
                    && w.Id != holder?.Id);
                // The row adopt with this wish's id asks (WishTransitions), nothing rebuilt here.
                var adoptable = other is not null
                    && WishTransitions.Target(
                        other.Status, other.StatusReason, WishAction.Adopt, new WishTransitionContext(Permanent: other.Permanent)) is not null;
                return new WishAdoptableSlotDto(
                    slot.Id,
                    slot.SevenTvEmoteId,
                    slot.Name,
                    slot.AddedAt,
                    slot.AddedAtFromRest && slot.AddedAt is not null,
                    WishImageQueries.WishImageUrlOf(slot.ImageUrl, slot.IsPlaceholder),
                    holder?.Id,
                    other?.Id,
                    adoptable);
            })
            .OrderBy(s => s.Alias, StringComparer.Ordinal)
            .ThenBy(s => s.SlotId, StringComparer.Ordinal)
            .ToList();
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    // The usage of the wish's window (E4, E5, E16), evaluated at the given time. Window, sum, mean and gap come from
    // the shared read (WishUsageQueries); the rank stays here because only the detail response shows it.
    private async Task<WishUsageDto?> LoadUsageAsync(Wish wish, string channelId, DateTime evaluatedAtUtc, CancellationToken cancellationToken)
    {
        if (wish.StartsAtUtc is not { } startsAt)
        {
            return null;
        }

        var read = await db.ReadAsync(channelId, wish.SevenTvEmoteId, startsAt, evaluatedAtUtc, cancellationToken);
        if (read is null)
        {
            return null;
        }

        var from = DateOnly.FromDateTime(read.WindowStartUtc);
        var to = DateOnly.FromDateTime(evaluatedAtUtc);

        // E16: the rank among all active slots of the channel in the same window. The wish's own slots are among
        // them, so a single-slot emote ranks against itself consistently; the number placed is the emote's total.
        var activeSlotIds = await db.EmoteSlots
            .Where(s => !s.IsArchived && !s.SeedUnsettled && s.Emote.ChannelId == channelId)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        int? rank = null;
        int? rankOf = null;
        if (activeSlotIds.Count > 0 && read.OwnSlotIds.Intersect(activeSlotIds, StringComparer.Ordinal).Any())
        {
            var perSlot = await db.UsageStats
                .Where(u => activeSlotIds.Contains(u.SlotId) && u.Date >= from && u.Date <= to)
                .GroupBy(u => u.SlotId)
                .Select(g => new { SlotId = g.Key, Total = g.Sum(u => (long)u.UseCount) })
                .ToListAsync(cancellationToken);
            var totals = new List<long>(activeSlotIds.Count);
            totals.AddRange(perSlot.Select(p => p.Total));
            totals.AddRange(Enumerable.Repeat(0L, activeSlotIds.Count - perSlot.Count));
            rank = WishUsageWindow.Rank(read.Total, totals);
            rankOf = activeSlotIds.Count;
        }

        return new WishUsageDto(read.WindowStartUtc, evaluatedAtUtc, read.Total, read.MeanPerDay, rank, rankOf, read.HasCoverageGap, read.Daily);
    }

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

    // A protection change is a decision (E25; plan #484 2.6, Sol-P1-1): behind the channel lock, lock the channel's
    // active wishes ascending by id, read them only afterwards, bump their DecisionVersion and drop a queued
    // removal, so a removal queued under the old threshold can never be carried out under the new one. The
    // caller commits this together with the settings row and its audit entry. Presented wishes stay untouched
    // (before K4 none carries a queue; K4 decides for bot-remove).
    private async Task SupersedeActiveDecisionsAsync(string channelId, CancellationToken cancellationToken)
    {
        // Under the channel lock no writer can turn a wish active (the reconcile and adopt take that lock first);
        // a wish that left active meanwhile is filtered out by the re-read behind the wish locks.
        var ids = await db.Wishes
            .Where(w => w.ChannelId == channelId && w.Status == WishStatus.Active)
            .Select(w => w.Id)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return;
        }

        await db.LockWishesByIdsAsync(ids, cancellationToken);
        var wishes = await db.Wishes
            .Where(w => ids.Contains(w.Id) && w.ChannelId == channelId && w.Status == WishStatus.Active)
            .ToListAsync(cancellationToken);
        foreach (var wish in wishes)
        {
            wish.DecisionVersion++;
            wish.RemovalQueuedAtUtc = null;
            wish.RemovalQueuedVersion = null;
        }
    }

    private async Task<EffectiveSettings> LoadSettingsAsync(string channelId, CancellationToken cancellationToken)
    {
        var row = await db.ChannelWishSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
        return row is null ? EffectiveSettings.Defaults : ToSettings(row);
    }

    private async Task<string?> ImageUrlOfAsync(string channelId, string sevenTvEmoteId, CancellationToken cancellationToken)
    {
        var images = await db.ReadWishImageUrlsAsync([channelId], [sevenTvEmoteId], cancellationToken);
        return images.GetValueOrDefault((channelId, sevenTvEmoteId));
    }

    // Slots of this emote that are in the set right now, under the one predicate adopt uses.
    private async Task<IReadOnlyList<string>> ActiveSlotIdsAsync(string channelId, string sevenTvEmoteId, CancellationToken cancellationToken)
    {
        var ids = await db.EmoteSlots
            .Where(AdoptableIn(channelId))
            .Where(s => s.Emote.SevenTvEmoteId == sevenTvEmoteId)
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

    // The wish holding the slot (active, presented, kept: the states of IX_Wishes_SlotId_Live), other than ownId.
    private Task<long?> FindHolderIdAsync(string slotId, long? ownId, CancellationToken cancellationToken) =>
        db.Wishes
            .Where(w => w.SlotId == slotId && HoldingStatuses.Contains(w.Status) && w.Id != ownId)
            .Select(w => (long?)w.Id)
            .FirstOrDefaultAsync(cancellationToken);

    // "Replacement visible" (plan #485 2.1a, row 6 of complete-removal, Sol-R3-P1), read behind the channel lock:
    // among the channel's adoptable slots of the wish's emote, (a) the wish's own slot row with an AddedAt that is
    // not the reported generation (a revived row), or (b) any other row under the wish's alias that no other wish
    // holds, whatever its date (before, after or none). Deliberately wider than the reconcile's return window:
    // the report does not know when the stay ended, and one conflict too many books nothing wrong. The own row
    // with the reported generation is no replacement, nor is an entry under another alias (E13).
    private async Task<bool> ReplacementVisibleAsync(
        string channelId, Wish wish, DateTime reportedGeneration, CancellationToken cancellationToken)
    {
        var slots = await db.EmoteSlots.AsNoTracking()
            .Where(AdoptableIn(channelId))
            .Where(s => s.Emote.SevenTvEmoteId == wish.SevenTvEmoteId && (s.Id == wish.SlotId || s.Name == wish.Alias))
            .Select(s => new { s.Id, s.Name, s.AddedAt })
            .ToListAsync(cancellationToken);
        if (slots.Exists(s => s.Id == wish.SlotId && !SameInstant(s.AddedAt, reportedGeneration)))
        {
            return true;
        }

        var others = slots
            .Where(s => s.Id != wish.SlotId && string.Equals(s.Name, wish.Alias, StringComparison.Ordinal))
            .Select(s => s.Id)
            .ToList();
        if (others.Count == 0)
        {
            return false;
        }

        var held = await db.Wishes.AsNoTracking()
            .Where(w => w.SlotId != null && others.Contains(w.SlotId) && HoldingStatuses.Contains(w.Status) && w.Id != wish.Id)
            .Select(w => w.SlotId!)
            .ToListAsync(cancellationToken);
        return others.Exists(id => !held.Contains(id, StringComparer.Ordinal));
    }

    // Saves the staged wish. A hit on one of the two partial unique indexes is a conflict, not an error: the
    // channel lock makes it unreachable in practice, the index is the belt. Rolls the transaction back then and
    // names the winner: the running wish of the emote, or the holder of slotId when the caller links one.
    private async Task<WishCommandResult?> SaveOrConflictAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        string channelId,
        string sevenTvEmoteId,
        CancellationToken cancellationToken,
        string? slotId = null)
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
            long? conflictingId = outcome switch
            {
                WishOutcome.AlreadyRunning => await FindRunningWishIdAsync(channelId, sevenTvEmoteId, cancellationToken),
                WishOutcome.SlotAlreadyLinked when slotId is not null => await FindHolderIdAsync(slotId, null, cancellationToken),
                _ => null
            };
            return new WishCommandResult(outcome, ConflictingWishId: conflictingId);
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
        CancellationToken cancellationToken,
        bool recordPreviousExpiry = false)
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
        // Contract 2.7: a decision on an overdue wish keeps the expiry it replaced; read before the command changes it.
        var previousExpiry = recordPreviousExpiry && wish.ExpiresAtUtc is { } expiry && expiry <= now ? expiry : (DateTime?)null;
        var step = await decide(wish, settings, now, cancellationToken);
        if (step.Refusal is not null)
        {
            db.ChangeTracker.Clear();
            return step.Refusal;
        }

        CountDecision(wish);
        StampStateChange(wish, previousStatus, now);

        var conflict = await SaveOrConflictAsync(transaction, channelId, wish.SevenTvEmoteId, cancellationToken);
        if (conflict is not null)
        {
            return conflict;
        }

        var channel = ChannelName.Normalize(channelName);
        await AuditAndCommitAsync(transaction, actor, auditAction, channel, wish, cancellationToken, step.AuditDurationDays, previousExpiry);
        await changedPublisher.PublishAsync(channel, cancellationToken);
        var imageUrl = await ImageUrlOfAsync(channelId, wish.SevenTvEmoteId, cancellationToken);
        return new WishCommandResult(WishOutcome.Success, Wish: ToDto(wish, settings, imageUrl));
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
        int? auditDurationDays = null,
        DateTime? previousExpiresAtUtc = null,
        RemovalReport? removal = null)
    {
        // previousExpiresAtUtc only on a late decision (contract 2.7), the reported entry only on a booked removal
        // (plan #485 2.1a); the details stay free of alias, name and login.
        var details = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["wishId"] = wish.Id,
            ["sevenTvEmoteId"] = wish.SevenTvEmoteId,
            ["durationDays"] = auditDurationDays ?? wish.DurationDays
        };
        if (previousExpiresAtUtc is { } previous)
        {
            details["previousExpiresAtUtc"] = previous;
        }

        if (removal is not null)
        {
            details["slotId"] = removal.SlotId;
            details["slotAddedAtUtc"] = removal.SlotAddedAtUtc;
            details["emoteSetId"] = removal.EmoteSetId;
            if (removal.Evidence is { } evidence)
            {
                details["evidence"] = evidence;
            }
        }

        db.AddAuditEntry(
            actor,
            action,
            channelName: channelName,
            targetType: "wish",
            targetId: wish.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            details: details);
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

    // A successful command counts as a decision (E25) and drops a queued bot removal.
    private static void CountDecision(Wish wish)
    {
        wish.DecisionVersion++;
        wish.RemovalQueuedAtUtc = null;
        wish.RemovalQueuedVersion = null;
    }

    // No target of a manager command carries a reason. Entering an end state starts the retention clock, the way
    // back out of one clears it, and a move from one end state into another keeps the clock (plan #485 2.1a:
    // removed_externally -> removed keeps the time the entry left the set).
    private static void StampStateChange(Wish wish, string previousStatus, DateTime now)
    {
        if (string.Equals(previousStatus, wish.Status, StringComparison.Ordinal))
        {
            return;
        }

        wish.StatusChangedAtUtc = now;
        wish.StatusReason = null;
        wish.EndedAtUtc = !WishStatus.IsEnded(wish.Status) ? null
            : WishStatus.IsEnded(previousStatus) ? wish.EndedAtUtc ?? now
            : now;
    }

    // Two instants as the database stores them (timestamptz, microseconds): a value sent by a client is compared
    // after the same truncation a write would apply. Null matches only null.
    internal static bool SameInstant(DateTime? stored, DateTime? sent) =>
        stored is null || sent is null
            ? stored is null && sent is null
            : ToDatabasePrecision(stored.Value) == ToDatabasePrecision(sent.Value);

    private static DateTime ToDatabasePrecision(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => value
        };
        return new DateTime(utc.Ticks - (utc.Ticks % (TimeSpan.TicksPerMillisecond / 1000)), DateTimeKind.Utc);
    }

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

    // "Adoptable" (plan #485 2.1a): a slot of the channel that is in the set, not archived and not a migration
    // seed no sync has settled. The one predicate behind adopt, the slot ids of wish_emote_already_in_set and
    // GET …/wishes/slots; nothing else may rebuild it.
    internal static Expression<Func<EmoteSlot, bool>> AdoptableIn(string channelId) =>
        s => s.Emote.ChannelId == channelId && !s.IsArchived && !s.SeedUnsettled;

    internal static string[] StatusesOf(WishListView view, bool botMode)
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

    internal static EffectiveSettings ToSettings(ChannelWishSettings row) => new(
        row.DefaultDurationDays,
        row.ProbationLeadDays,
        row.ProtectionThresholdPerDay,
        row.BotModeEnabled,
        row.BotSuspendedReason,
        row.BotModeActivatedByTwitchUserId);

    internal static WishDto ToDto(Wish w, EffectiveSettings settings, string? imageUrl) => new(
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
        w.SlotAddedAtUtc,
        imageUrl,
        w.LinkOrigin,
        w.ExtensionCount,
        w.Permanent,
        w.BotOwned,
        w.BotWriteAtUtc is not null && w.Status != WishStatus.Active,
        w.BotNextAttemptAtUtc,
        w.RemovalQueuedAtUtc,
        settings.IsBotMode ? "bot" : "manual");

    private sealed record LockedChannel(string ChannelName, int? ActiveEmoteSetCapacity);

    // What a booked removal reported, for its audit entry only.
    private sealed record RemovalReport(string SlotId, DateTime SlotAddedAtUtc, string EmoteSetId, string? Evidence);

    // What a transition command decided: a refusal, or success with an optional durationDays for the audit entry.
    private sealed record CommandStep(WishCommandResult? Refusal, int? AuditDurationDays = null)
    {
        public static readonly CommandStep Done = new((WishCommandResult?)null);
        public static readonly CommandStep Conflict = new(WishCommandResult.Of(WishOutcome.StateConflict));
        public static readonly Task<CommandStep> DoneTask = Task.FromResult(Done);
        public static readonly Task<CommandStep> ConflictTask = Task.FromResult(Conflict);
    }

    internal sealed record EffectiveSettings(
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
