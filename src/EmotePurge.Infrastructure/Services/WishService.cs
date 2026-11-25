using System.Linq.Expressions;
using System.Text.RegularExpressions;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
/// wish rows as well (<see cref="SupersedeActiveDecisionsAsync"/>, plan #484 2.6). The K6 commands on an open wish
/// (<see cref="ApproveAsync"/>, <see cref="RejectAsync"/>, <see cref="UnlinkAsync"/>) take the channel row first and
/// then the wish row(s); an approval that becomes an extension locks both wish rows in one ascending call
/// (plan #486 2.6, Sol-P2-9), the same row lock <see cref="ExtendAsync"/> and the expiry run take. Deleting an ended
/// wish (<see cref="DeleteAsync"/>, K7a, spec E32) takes the channel row, checks the caller's Twitch id behind it, and
/// then locks the wish row only within that channel (<see cref="RowLocks.LockChannelWishesByIdsAsync"/>), so it never
/// locks or waits for a row of a channel it has not locked; the wishes merged into it follow without waiting
/// (<see cref="RowLocks.LockChannelWishesByIdsNoWaitAsync"/>, SQLSTATE <c>55P03</c> gives way as a state conflict), so it
/// never waits for a second wish row while it holds one.
/// </para>
/// <para>
/// <b>Bot flow (#487 plan 2.3, 2.5a (b), 2.9).</b> The channel's effective bot state (<see cref="WishBotChannelState"/>,
/// through <see cref="IWishBotChannelStateReader"/>) decides <c>mode</c> and which states need a human; the right to the
/// settings (D7) hangs on the stored activation and is checked again behind the channel lock. Every mutating path asks
/// <see cref="WishBotAttemptGuard"/> under the wish's row lock: an open bot attempt answers 409
/// <c>wish_state_conflict</c>, and the threshold change skips such a wish.
/// </para>
/// </summary>
public sealed class WishService(
    AppDbContext db,
    IWishChangedPublisher changedPublisher,
    TimeProvider timeProvider,
    ILogger<WishService> logger,
    IWishBotChannelStateReader botChannelStates,
    IWishReconcileTrigger? reconcileTrigger = null,
    IBotAccountNudge? botNudge = null) : IWishService
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

    private static readonly IReadOnlyDictionary<long, string> EmptyLabels = new Dictionary<long, string>();

    public async Task<WishSettingsDto?> GetSettingsAsync(string channelName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);

        var channelId = await ResolveChannelIdAsync(channelName, cancellationToken);
        return channelId is null ? null : (await LoadSettingsAsync(channelId, cancellationToken)).ToDto();
    }

    public async Task<WishCommandResult> UpdateSettingsAsync(
        string channelName,
        UpdateWishSettingsRequest request,
        AuditActor actor,
        bool isGlobalAdmin = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        if (!IsDurationInRange(request.DefaultDurationDays)
            || request.ProbationLeadDays is < WishLimits.ProbationLeadDaysMin or > WishLimits.ProbationLeadDaysMax
            || (!request.KeepProtectionThreshold && request.ProtectionThresholdPerDay is { } requested && !IsThresholdInRange(requested))
            || request.PendingExpiryDays is < WishLimits.PendingExpiryDaysMin or > WishLimits.PendingExpiryDaysMax)
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

        // D7 again behind the channel lock, on the row as it is now (#487 plan 2.9, Sol-P2-6): the filter decided before
        // the lock, and a bot activation confirmed in between restricts the settings to broadcaster, activator and admins.
        var stored = row is null ? EffectiveSettings.Defaults : ToSettings(row);
        if (!CanChangeSettings(stored, locked.TwitchChannelId, actor.TwitchUserId, isGlobalAdmin))
        {
            db.ChangeTracker.Clear();
            return WishCommandResult.Of(WishOutcome.SettingsRestricted);
        }

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
        if (request.PendingExpiryDays is { } pendingExpiryDays)
        {
            row.PendingExpiryDays = pendingExpiryDays;
        }

        db.AddAuditEntry(
            actor,
            AuditActions.WishSettings,
            channelName: locked.ChannelName,
            details: new
            {
                defaultDurationDays = row.DefaultDurationDays,
                probationLeadDays = row.ProbationLeadDays,
                protectionThresholdPerDay = row.ProtectionThresholdPerDay,
                pendingExpiryDays = row.PendingExpiryDays
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

        var settings = await LoadStoredSettingsAsync(channel.Id, cancellationToken);
        return CanChangeSettings(settings, channel.TwitchChannelId, callerTwitchUserId, isGlobalAdmin);
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

        var setFull = await TryIsSetFullAsync(channel.ActiveEmoteSetCapacity, channelId, cancellationToken);
        var imageUrl = await TryImageUrlOfAsync(channelId, emoteId, cancellationToken);
        return new WishCommandResult(WishOutcome.Success, Wish: ToDto(wish, settings, imageUrl), SetFull: setFull);
    }

    public async Task<WishCommandResult> AdoptAsync(
        string channelName,
        AdoptWishRequest request,
        AuditActor actor,
        WishCaptureAccess? access = null,
        CancellationToken cancellationToken = default)
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

        var scope = await ResolveScopeAsync(channelName, access, cancellationToken);
        if (scope is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        var channelId = scope.Id;
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

            // #487 plan 2.5a (b): an open bot attempt blocks every decision until it is resolved.
            if (WishBotAttemptGuard.Blocks(wish))
            {
                db.ChangeTracker.Clear();
                return WishCommandResult.Of(WishOutcome.StateConflict);
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
            || (wish is not null && wish.HasEmote
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

            // The manager picked this entry for this wish: an exclusion an earlier unlink left for the pair goes
            // (plan #486 2.6). Inside the transaction, so a refusal below keeps it.
            await db.WishSlotExclusions
                .Where(x => x.WishId == wish.Id && x.SlotId == slot.Id)
                .ExecuteDeleteAsync(cancellationToken);
        }

        // An open wish without an emote (K6 placeholder) takes the slot's emote; the CHECK wants all three filled.
        if (!wish.HasEmote)
        {
            wish.SevenTvEmoteId = slot.Emote.SevenTvEmoteId;
        }

        // Plan #486 2.8a: the emote row's name, else the entry's name, for a new adoption as for an existing wish.
        if (wish.EmoteName.Length == 0)
        {
            wish.EmoteName = EmoteNameOf(slot);
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
        return new WishCommandResult(WishOutcome.Success, Wish: await ResponseDtoAsync(wish, settings, imageUrl, scope, cancellationToken));
    }

    public Task<WishCommandResult> UpdateAsync(
        string channelName,
        long wishId,
        UpdateWishRequest request,
        AuditActor actor,
        WishCaptureAccess? access = null,
        CancellationToken cancellationToken = default)
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

            // Plan #486 2.6 (P3-12): an alias without an emote means nothing to the matching; an open wish without
            // one takes only the duration.
            if (target is null || (alias is not null && !wish.HasEmote))
            {
                return CommandStep.ConflictTask;
            }

            // A change of an open wish may change its match (the alias picks among several entries): reconcile
            // after the commit.
            var open = wish.Status == WishStatus.Pending;
            wish.Status = target;
            wish.Alias = alias ?? wish.Alias;
            wish.DurationDays = request.DurationDays ?? wish.DurationDays;
            return Task.FromResult(open ? CommandStep.DoneAndReconcile : CommandStep.Done);
        }, access, cancellationToken);
    }

    public Task<WishCommandResult> ExtendAsync(
        string channelName, long wishId, AuditActor actor, WishCaptureAccess? access = null, CancellationToken cancellationToken = default) =>
        RunTransitionAsync(channelName, wishId, actor, AuditActions.WishExtend, (wish, settings, now, _) =>
            Task.FromResult(TryExtend(wish, settings, now) is { } extensionDays
                ? new CommandStep(null, AuditDurationDays: extensionDays)
                : CommandStep.Conflict), access, cancellationToken, recordPreviousExpiry: true);

    public Task<WishCommandResult> KeepAsync(
        string channelName, long wishId, AuditActor actor, WishCaptureAccess? access = null, CancellationToken cancellationToken = default) =>
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
        }, access, cancellationToken, recordPreviousExpiry: true);

    public Task<WishCommandResult> CloseAsync(
        string channelName, long wishId, AuditActor actor, WishCaptureAccess? access = null, CancellationToken cancellationToken = default) =>
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
        }, access, cancellationToken, recordPreviousExpiry: true);

    public Task<WishCommandResult> RetryAsync(
        string channelName, long wishId, AuditActor actor, WishCaptureAccess? access = null, CancellationToken cancellationToken = default) =>
        RunTransitionAsync(channelName, wishId, actor, AuditActions.WishRetry, (wish, _, now, _) =>
        {
            var target = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Retry);
            if (target is null)
            {
                return CommandStep.ConflictTask;
            }

            // The manager's click is the approval (R3-12). A fresh approval is a fresh start for the bot as well (#487
            // plan 2.1 point 2); an open repair phase stays.
            wish.Status = target;
            wish.ApprovedAtUtc = now;
            ResetBotAttempt(wish);
            return CommandStep.DoneTask;
        }, access, cancellationToken);

    public Task<WishCommandResult> ReAddAsync(
        string channelName,
        long wishId,
        ReAddWishRequest request,
        AuditActor actor,
        WishCaptureAccess? access = null,
        CancellationToken cancellationToken = default)
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
            // The bot starts afresh too (#487 plan 2.1 point 2): without this it would take the wish for a crash
            // remnant and the reconcile would leave it out; the link origin goes with the old link, an open repair
            // phase stays.
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
            wish.LinkOrigin = null;
            ResetBotAttempt(wish);
            return CommandStep.Done;
        }, access, cancellationToken);
    }

    public async Task<WishCommandResult> CompleteRemovalAsync(
        string channelName,
        long wishId,
        CompleteWishRemovalRequest request,
        AuditActor actor,
        WishCaptureAccess? access = null,
        CancellationToken cancellationToken = default)
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

        var scope = await ResolveScopeAsync(channelName, access, cancellationToken);
        if (scope is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        var channelId = scope.Id;

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

        // #487 plan 2.5a (b): an open bot attempt blocks the report like every other decision.
        if (WishBotAttemptGuard.Blocks(wish))
        {
            db.ChangeTracker.Clear();
            return WishCommandResult.Of(WishOutcome.StateConflict);
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
            return new WishCommandResult(WishOutcome.Success, Wish: await ResponseDtoAsync(wish, settings, bookedImage, scope, cancellationToken));
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
        var imageUrl = await TryImageUrlOfAsync(channelId, wish.SevenTvEmoteId, cancellationToken);
        return new WishCommandResult(WishOutcome.Success, Wish: await ResponseDtoAsync(wish, settings, imageUrl, scope, cancellationToken));
    }

    // #487 plan 2.9 (R2-8): the channel row first, so the active set read behind it cannot change before the commit.
    // The attempt's set and generation anchor stay; only the schedule starts again, bound to the new decision version so
    // the runner's conditional bookings (2.5a (f)) still match after the decision this command counts.
    public Task<WishCommandResult> RecheckAsync(
        string channelName, long wishId, AuditActor actor, WishCaptureAccess? access = null, CancellationToken cancellationToken = default) =>
        RunTransitionAsync(channelName, wishId, actor, AuditActions.WishRecheck, async (wish, settings, now, ct) =>
        {
            if (WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Recheck) is not { } target)
            {
                return CommandStep.Conflict;
            }

            if (!settings.IsBotMode)
            {
                return CommandStep.Refuse(WishOutcome.BotUnavailable);
            }

            var activeSetId = await db.Channels.AsNoTracking()
                .Where(c => c.Id == wish.ChannelId)
                .Select(c => c.ActiveEmoteSetId)
                .FirstOrDefaultAsync(ct);
            if (wish.BotSetId is null || !string.Equals(wish.BotSetId, activeSetId, StringComparison.Ordinal))
            {
                return CommandStep.Refuse(WishOutcome.BotSetChanged);
            }

            wish.Status = target;
            wish.BotAttemptCount = 0;
            wish.BotNextAttemptAtUtc = now;
            wish.BotConfirmDeadlineAtUtc = now + WishLimits.BotConfirmWindow;
            return new CommandStep(null, NudgeBot: true, AfterCount: w => w.BotAttemptVersion = w.DecisionVersion);
        }, access, cancellationToken, lockChannel: true);

    // #487 plan 2.9 (AK84): the request is a decision of its own; the queue is bound to the version after it, which is the
    // one the runner checks before it removes (E25).
    public Task<WishCommandResult> RequestBotRemovalAsync(
        string channelName, long wishId, AuditActor actor, WishCaptureAccess? access = null, CancellationToken cancellationToken = default) =>
        RunTransitionAsync(channelName, wishId, actor, AuditActions.WishBotRemoveRequest, (wish, settings, now, _) =>
        {
            if (!string.Equals(wish.Status, WishStatus.Presented, StringComparison.Ordinal)
                || wish.StatusReason is not (WishStatusReason.Protected or WishStatusReason.CoverageGap or WishStatusReason.BotUnavailable)
                || wish.RemovalQueuedAtUtc is not null)
            {
                return CommandStep.ConflictTask;
            }

            if (!wish.BotOwned)
            {
                return Task.FromResult(CommandStep.Refuse(WishOutcome.NotBotOwned));
            }

            if (!wish.StartsAtAuthoritative)
            {
                return Task.FromResult(CommandStep.Refuse(WishOutcome.StartUnconfirmed));
            }

            if (!settings.IsBotMode)
            {
                return Task.FromResult(CommandStep.Refuse(WishOutcome.BotUnavailable));
            }

            return Task.FromResult(new CommandStep(null, NudgeBot: true, AfterCount: w =>
            {
                w.RemovalQueuedAtUtc = now;
                w.RemovalQueuedVersion = w.DecisionVersion;
            }));
        }, access, cancellationToken, recordPreviousExpiry: true);

    public async Task<WishCommandResult> ApproveAsync(
        string channelName,
        long wishId,
        ApproveWishRequest request,
        AuditActor actor,
        WishCaptureAccess? access = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        // The pure input checks, before any lock or query. A sent id has the shape of a manual entry's and is stored
        // and compared only in its canonical form (plan #486 2.5a), so an old hex link meets the running ULID.
        if (request.DurationDays is { } requested && !IsDurationInRange(requested))
        {
            return WishCommandResult.Of(WishOutcome.DurationOutOfRange);
        }

        string? bodyEmoteId = null;
        if (request.SevenTvEmoteId is { } rawId)
        {
            var trimmedId = rawId.Trim();
            if (!EmoteIdPattern.IsMatch(trimmedId))
            {
                return WishCommandResult.Of(WishOutcome.EmoteIdInvalid);
            }

            bodyEmoteId = SevenTvEmoteIdCanonical.Canonicalize(trimmedId);
        }

        var bodyAlias = NullIfEmpty(request.Alias?.Trim());
        var bodyName = NullIfEmpty(request.EmoteName?.Trim());
        if ((bodyAlias is not null && !IsValidAlias(bodyAlias)) || (bodyName is not null && !IsValidEmoteName(bodyName)))
        {
            return WishCommandResult.Of(WishOutcome.AliasInvalid);
        }

        var scope = await ResolveScopeAsync(channelName, access, cancellationToken);
        if (scope is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var channel = await LockChannelAsync(scope.Id, cancellationToken);
        if (channel is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        // Behind the channel row nobody else turns a wish of the emote running except a re-add or retry (wish row
        // only); the running index is the belt for that. Find the running wish first, then lock both rows in one
        // ascending call (Sol-P2-9: the row lock ExtendAsync and the expiry run take), and only then read and decide.
        var unlockedEmoteId = await db.Wishes.AsNoTracking()
            .Where(w => w.Id == wishId && w.ChannelId == scope.Id)
            .Select(w => w.SevenTvEmoteId)
            .FirstOrDefaultAsync(cancellationToken);
        if (unlockedEmoteId is null)
        {
            return WishCommandResult.Of(WishOutcome.WishNotFound);
        }

        var candidateEmoteId = bodyEmoteId ?? NullIfEmpty(unlockedEmoteId);
        var runningId = candidateEmoteId is null ? null : await FindRunningWishIdAsync(scope.Id, candidateEmoteId, cancellationToken);
        await db.LockWishesByIdsAsync(runningId is { } lockedRunning ? [wishId, lockedRunning] : [wishId], cancellationToken);

        var wish = await db.Wishes.FirstOrDefaultAsync(w => w.Id == wishId && w.ChannelId == scope.Id, cancellationToken);
        if (wish is null)
        {
            return WishCommandResult.Of(WishOutcome.WishNotFound);
        }

        if (WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Approve) is not { } approvedStatus)
        {
            return WishCommandResult.Of(WishOutcome.StateConflict);
        }

        var emoteId = bodyEmoteId ?? NullIfEmpty(wish.SevenTvEmoteId);
        if (emoteId is null)
        {
            return WishCommandResult.Of(WishOutcome.EmoteRequired);
        }

        var settings = await LoadSettingsAsync(scope.Id, cancellationToken);
        var now = Now();

        // (1) A wish of the emote runs: re-read behind its lock. Still running, the approval becomes its extension;
        // without an expiry no extension can be booked (Sol-R2-P2-7). Gone meanwhile: the order starts over at (2).
        if (runningId is { } targetId)
        {
            var target = await db.Wishes.FirstOrDefaultAsync(
                w => w.Id == targetId && w.ChannelId == scope.Id && w.SevenTvEmoteId == emoteId, cancellationToken);
            if (target is not null && WishStatus.IsRunning(target.Status))
            {
                // #487 plan 2.5a (b): an open bot attempt on the running wish blocks its extension, so the approval
                // books nothing.
                if (WishBotAttemptGuard.Blocks(target))
                {
                    db.ChangeTracker.Clear();
                    return WishCommandResult.Of(WishOutcome.StateConflict);
                }

                return await MergeIntoRunningAsync(transaction, actor, channel.ChannelName, scope, settings, wish, target, now, cancellationToken);
            }
        }

        // (2) Already in the set: the way out is "take over from the set" for this wish.
        var slotIds = await ActiveSlotIdsAsync(scope.Id, emoteId, cancellationToken);
        if (slotIds.Count > 0)
        {
            var running = await FindRunningWishIdAsync(scope.Id, emoteId, cancellationToken);
            db.ChangeTracker.Clear();
            return new WishCommandResult(WishOutcome.EmoteAlreadyInSet, SlotIds: slotIds, ConflictingWishId: running);
        }

        // (3) Approved. Alias: the body's, the wish's own (a PATCH), the requested name, the emote name; the emote name
        // the body's, the wish's, the alias (plan 2.8a: every field filled in this same update). The requested name is
        // redeemer data (2.4a): only a caller confirmed on the row's Twitch id may have it copied into alias and emote
        // name, which every manager sees; anyone else names the emote in the body or gets wish_alias_invalid.
        var requestedName = scope.IncludeRequestData ? NullIfEmpty(wish.RequestedName?.Trim()) : null;
        var alias = bodyAlias ?? NullIfEmpty(wish.Alias) ?? requestedName ?? bodyName ?? NullIfEmpty(wish.EmoteName);
        var emoteName = bodyName ?? NullIfEmpty(wish.EmoteName) ?? alias;
        if (alias is null || emoteName is null || !IsValidAlias(alias) || !IsValidEmoteName(emoteName))
        {
            db.ChangeTracker.Clear();
            return WishCommandResult.Of(WishOutcome.AliasInvalid);
        }

        var previousStatus = wish.Status;
        wish.Status = approvedStatus;
        wish.SevenTvEmoteId = emoteId;
        wish.Alias = alias;
        wish.EmoteName = emoteName;
        wish.DurationDays = request.DurationDays ?? wish.DurationDays;
        wish.ApprovedAtUtc = now;
        CountDecision(wish);
        StampStateChange(wish, previousStatus, now);

        var conflict = await SaveOrConflictAsync(transaction, scope.Id, emoteId, cancellationToken);
        if (conflict is not null)
        {
            return conflict;
        }

        AddApproveAudit(actor, channel.ChannelName, wish, WishStatus.Approved, mergedIntoWishId: null);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await changedPublisher.PublishAsync(channel.ChannelName, cancellationToken);
        var imageUrl = await TryImageUrlOfAsync(scope.Id, emoteId, cancellationToken);
        return new WishCommandResult(WishOutcome.Success, Wish: await ResponseDtoAsync(wish, settings, imageUrl, scope, cancellationToken));
    }

    public Task<WishCommandResult> RejectAsync(
        string channelName, long wishId, AuditActor actor, WishCaptureAccess? access = null, CancellationToken cancellationToken = default) =>
        RunTransitionAsync(channelName, wishId, actor, AuditActions.WishReject, (wish, _, _, _) =>
        {
            var target = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Reject);
            if (target is null)
            {
                return CommandStep.ConflictTask;
            }

            // The emote fields stay as they are: rejected may carry the placeholder (plan 2.8a).
            wish.Status = target;
            return CommandStep.DoneTask;
        }, access, cancellationToken, lockChannel: true);

    public async Task<WishCommandResult> UnlinkAsync(
        string channelName, long wishId, AuditActor actor, WishCaptureAccess? access = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(actor);

        var scope = await ResolveScopeAsync(channelName, access, cancellationToken);
        if (scope is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        // The unlink clears a SlotId: the channel row first, like every slot writer, then the wish row, then read.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var channel = await LockChannelAsync(scope.Id, cancellationToken);
        if (channel is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        await db.LockWishesByIdsAsync([wishId], cancellationToken);
        var wish = await db.Wishes.FirstOrDefaultAsync(w => w.Id == wishId && w.ChannelId == scope.Id, cancellationToken);
        if (wish is null)
        {
            return WishCommandResult.Of(WishOutcome.WishNotFound);
        }

        // #487 plan 2.5a (b).
        if (WishBotAttemptGuard.Blocks(wish))
        {
            db.ChangeTracker.Clear();
            return WishCommandResult.Of(WishOutcome.StateConflict);
        }

        // Only active (spec K6): the table also lets presented with unconfirmed_start back to pending, but that row
        // is the reconcile's date correction (plan 2.5), not a manager command.
        var ircAutoLinked = wish.Source == WishSource.Irc && wish.LinkOrigin == WishLinkOrigin.Auto;
        if (wish.Status != WishStatus.Active
            || WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Unlink, new WishTransitionContext(IrcAutoLinked: ircAutoLinked))
                is not WishStatus.Pending
            || wish.SlotId is not { } slotId)
        {
            return WishCommandResult.Of(WishOutcome.StateConflict);
        }

        var now = Now();
        var fromStatus = wish.Status;
        // Counts the decision and drops a queued removal itself (E25): no CountDecision on top.
        WishUnlinkReset.ToPending(wish, now, reason: null);
        if (!await db.WishSlotExclusions.AnyAsync(x => x.WishId == wish.Id && x.SlotId == slotId, cancellationToken))
        {
            db.WishSlotExclusions.Add(new WishSlotExclusion { WishId = wish.Id, SlotId = slotId, CreatedAtUtc = now });
        }

        // The same detail keys as the date correction's wish.unlink, without its cause.
        db.AddAuditEntry(
            actor,
            AuditActions.WishUnlink,
            channelName: channel.ChannelName,
            targetType: "wish",
            targetId: wish.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            details: new { wishId = wish.Id, fromStatus, slotId });
        var settings = await LoadSettingsAsync(scope.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await changedPublisher.PublishAsync(channel.ChannelName, cancellationToken);
        // Another open wish of the emote may take the entry now (AK61); a failure here heals with the next sync.
        await TriggerReconcileAsync(scope.Id, cancellationToken);
        var imageUrl = await TryImageUrlOfAsync(scope.Id, wish.SevenTvEmoteId, cancellationToken);
        return new WishCommandResult(WishOutcome.Success, Wish: await ResponseDtoAsync(wish, settings, imageUrl, scope, cancellationToken));
    }

    public async Task<WishCommandResult> DeleteAsync(
        string channelName,
        long wishId,
        DateTime expectedEndedAtUtc,
        AuditActor actor,
        WishCaptureAccess access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(access);

        var channelId = access.ChannelId;

        // Spec E32, plan #500 2.2: the channel row first, like every writer that changes who holds a slot.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if ((await db.LockChannelsByIdsAsync([channelId], cancellationToken)).Count == 0)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        // E41 behind the lock: the row's Twitch id as it is now, checked before the wish is looked up, so a refused
        // caller does not learn whether the id exists.
        var channel = await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new { c.ChannelName, c.TwitchChannelId })
            .SingleAsync(cancellationToken);
        if (!IsConfirmedOn(access, channel.TwitchChannelId))
        {
            return WishCommandResult.Of(WishOutcome.AccessDenied);
        }

        // The wish row only within the locked channel (2.2a): a row of another channel is never locked or waited for.
        if ((await db.LockChannelWishesByIdsAsync(channelId, [wishId], cancellationToken)).Count == 0)
        {
            return WishCommandResult.Of(WishOutcome.WishNotFound);
        }

        var wish = await db.Wishes.FirstOrDefaultAsync(w => w.Id == wishId && w.ChannelId == channelId, cancellationToken);
        if (wish is null)
        {
            return WishCommandResult.Of(WishOutcome.WishNotFound);
        }

        // E34 and E44: only an end state, and only the episode the confirmation showed.
        if (!WishStatus.IsEnded(wish.Status) || !SameInstant(wish.EndedAtUtc, expectedEndedAtUtc))
        {
            db.ChangeTracker.Clear();
            return WishCommandResult.Of(WishOutcome.StateConflict);
        }

        // #487 plan 2.5a (b), K6b-12: an open bot attempt blocks (defensive, an ended wish carries none), and an open
        // repair phase keeps the row and its evidence until it is resolved, whatever the bot mode is now: deleting it
        // would destroy the evidence the needs_review result is built from. Afterwards the delete works as in K7a.
        if (WishBotAttemptGuard.Blocks(wish))
        {
            db.ChangeTracker.Clear();
            return WishCommandResult.Of(WishOutcome.StateConflict);
        }

        if (wish.BotRepairAtUtc is not null)
        {
            db.ChangeTracker.Clear();
            return WishCommandResult.Of(WishOutcome.RepairCheckOpen);
        }

        // E32: the wishes merged into this one, within the channel, ascending and without waiting. The delete's ON DELETE
        // SET NULL writes them, so their locks are taken here, before anything is written. Never waiting for a second
        // wish row while holding this one rules out a deadlock with the retention's unordered batches: if the pass (or
        // anyone) holds a child, the click gives way with a state conflict and the client offers a retry. No child can
        // appear meanwhile: wishes are merged only into running ones, and this one has ended. A child that went between
        // the read and the lock is simply not returned.
        var childIds = await db.Wishes.AsNoTracking()
            .Where(w => w.MergedIntoWishId == wishId)
            .Select(w => w.Id)
            .ToListAsync(cancellationToken);
        try
        {
            await db.LockChannelWishesByIdsNoWaitAsync(channelId, childIds, cancellationToken);
        }
        catch (Exception exception) when (IsLockNotAvailable(exception))
        {
            // The statement aborted the transaction: roll it back explicitly, so the context is usable again at once.
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            logger.LogInformation("Deleting wish {WishId} gave way to a lock on a wish merged into it.", wishId);
            return WishCommandResult.Of(WishOutcome.StateConflict);
        }

        // E35: the merged children and the slot exclusions are never loaded, so the database's ON DELETE SET NULL and
        // CASCADE do the rest; a tracked child would have its reference cleared by EF instead.
        var heldSlot = wish.Status == WishStatus.Kept && wish.SlotId is not null;
        StageWishAudit(
            actor, AuditActions.WishDelete, channel.ChannelName, wish, auditDurationDays: null, previousExpiresAtUtc: null, removal: null,
            fromStatus: wish.Status);
        db.Wishes.Remove(wish);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // E36: best effort after the commit. A freed entry is the reconcile's to hand on (an open or approved wish of
        // the emote may take it); a failure there heals with the next sync.
        await changedPublisher.PublishAsync(channel.ChannelName, cancellationToken);
        if (heldSlot)
        {
            await TriggerReconcileAsync(channelId, cancellationToken);
        }

        return WishCommandResult.Of(WishOutcome.Success);
    }

    public async Task<PagedResult<WishDto>?> ListAsync(
        string channelName,
        WishListView view,
        int page,
        int pageSize,
        WishCaptureAccess? access = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);

        var scope = await ResolveScopeAsync(channelName, access, cancellationToken);
        if (scope is null)
        {
            return null;
        }

        var channelId = scope.Id;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var settings = await LoadSettingsAsync(channelId, cancellationToken);
        var statuses = StatusesOf(view, settings.ChannelMode);
        var query = db.Wishes.AsNoTracking().Where(w => w.ChannelId == channelId && statuses.Contains(w.Status));
        var ordered = view switch
        {
            WishListView.Ended => query.OrderByDescending(w => w.EndedAtUtc).ThenByDescending(w => w.Id),
            // Plan #486 2.6 [W22]: an open wish has no expiry (NULL sorts last), so it would trail the list; it leads,
            // oldest first, and the rest follows in the order of the other views.
            WishListView.Open => query
                .OrderBy(w => w.Status == WishStatus.Pending ? 0 : 1)
                .ThenBy(w => w.Status == WishStatus.Pending ? (DateTime?)w.CreatedAtUtc : null)
                .ThenBy(w => w.ExpiresAtUtc)
                .ThenByDescending(w => w.CreatedAtUtc)
                .ThenBy(w => w.Id),
            _ => query.OrderBy(w => w.ExpiresAtUtc).ThenByDescending(w => w.CreatedAtUtc).ThenBy(w => w.Id),
        };

        var total = await query.CountAsync(cancellationToken);
        var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var images = await db.ReadWishImageUrlsAsync([channelId], rows.Select(w => w.SevenTvEmoteId).ToList(), cancellationToken);
        var labels = scope.IncludeRequestData ? await db.ReadRewardLabelsAsync(rows, cancellationToken) : EmptyLabels;
        var items = rows
            .Select(w => ToDto(
                w,
                settings,
                images.GetValueOrDefault((channelId, w.SevenTvEmoteId)),
                scope.IncludeRequestData,
                w.RewardId is { } rewardId ? labels.GetValueOrDefault(rewardId) : null))
            .ToList();
        return new PagedResult<WishDto>(items, page, pageSize, total);
    }

    public async Task<WishDetailDto?> GetAsync(
        string channelName, long wishId, WishCaptureAccess? access = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelName);

        var scope = await ResolveScopeAsync(channelName, access, cancellationToken);
        if (scope is null)
        {
            return null;
        }

        var channelId = scope.Id;
        var wish = await db.Wishes.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wishId && w.ChannelId == channelId, cancellationToken);
        if (wish is null)
        {
            return null;
        }

        var settings = await LoadSettingsAsync(channelId, cancellationToken);
        var dto = await ResponseDtoAsync(wish, settings, await ImageUrlOfAsync(channelId, wish.SevenTvEmoteId, cancellationToken), scope, cancellationToken);
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

        return new WishUsageDto(read.WindowStartUtc, evaluatedAtUtc, read.Total, read.MeanPerDay, read.MeanWithheld, rank, rankOf, read.HasCoverageGap, read.Daily);
    }

    private Task<string?> ResolveChannelIdAsync(string channelName, CancellationToken cancellationToken)
    {
        var normalized = ChannelName.Normalize(channelName);
        return db.Channels.AsNoTracking()
            .Where(c => c.ChannelName == normalized)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Plan #486 2.4a: with an access the method works on the row it was decided on (a rename in between cannot hit
    // another one) and gives out redeemer data only while the caller is confirmed on that row's current Twitch id.
    // Without one the name is resolved as in K1 and no redeemer data of a captured wish leaves.
    private async Task<ChannelScope?> ResolveScopeAsync(string channelName, WishCaptureAccess? access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelName);

        if (access is not null)
        {
            var row = await db.Channels.AsNoTracking()
                .Where(c => c.Id == access.ChannelId)
                .Select(c => new { c.ChannelName, c.TwitchChannelId })
                .FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : new ChannelScope(access.ChannelId, row.ChannelName, IsConfirmedOn(access, row.TwitchChannelId));
        }

        var channelId = await ResolveChannelIdAsync(channelName, cancellationToken);
        return channelId is null ? null : new ChannelScope(channelId, ChannelName.Normalize(channelName), IncludeRequestData: false);
    }

    // The DTO of one wish, with the reward's label when the caller may see it. The label read decorates a committed
    // command too, so its failure answers without the label instead of failing the command.
    private async Task<WishDto> ResponseDtoAsync(
        Wish wish, EffectiveSettings settings, string? imageUrl, ChannelScope scope, CancellationToken cancellationToken)
    {
        string? label = null;
        if (scope.IncludeRequestData && wish.RewardId is { } rewardId)
        {
            try
            {
                label = (await db.ReadRewardLabelsAsync([wish], cancellationToken)).GetValueOrDefault(rewardId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reading the reward label of a wish failed for channel {ChannelId}; answering without it", scope.Id);
            }
        }

        return ToDto(wish, settings, imageUrl, scope.IncludeRequestData, label);
    }

    private Task TriggerReconcileAsync(string channelId, CancellationToken cancellationToken) =>
        reconcileTrigger?.TriggerAsync(channelId, cancellationToken) ?? Task.CompletedTask;

    // Approve, step (1) of plan #486 2.6: both rows are locked and re-read, the target still runs. The extension is
    // ExtendAsync's (TryExtend, E12, E25) with its wish.extend; the open wish becomes merged and takes the target's
    // emote fields in the same update (2.8a). Both rows and both audit entries commit together.
    private async Task<WishCommandResult> MergeIntoRunningAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        AuditActor actor,
        string channelName,
        ChannelScope scope,
        EffectiveSettings settings,
        Wish wish,
        Wish target,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var previousTargetStatus = target.Status;
        var previousExpiry = target.ExpiresAtUtc is { } expiry && expiry <= now ? expiry : (DateTime?)null;
        if (TryExtend(target, settings, now) is not { } extensionDays)
        {
            // approved or unconfirmed: nothing to extend, nothing booked; the open wish stays decidable.
            db.ChangeTracker.Clear();
            return new WishCommandResult(WishOutcome.RunningNotExtendable, ConflictingWishId: target.Id);
        }

        CountDecision(target);
        StampStateChange(target, previousTargetStatus, now);

        var previousStatus = wish.Status;
        wish.Status = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.MergeIntoRunning)
            ?? throw new InvalidOperationException("An open wish can always merge into a running one.");
        wish.MergedIntoWishId = target.Id;
        wish.SevenTvEmoteId = target.SevenTvEmoteId;
        wish.Alias = target.Alias;
        wish.EmoteName = target.EmoteName;
        CountDecision(wish);
        StampStateChange(wish, previousStatus, now);

        AddApproveAudit(actor, channelName, wish, WishStatus.Merged, target.Id);
        StageWishAudit(actor, AuditActions.WishExtend, channelName, wish: target, extensionDays, previousExpiry, removal: null);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await changedPublisher.PublishAsync(channelName, cancellationToken);
        var imageUrl = await TryImageUrlOfAsync(scope.Id, target.SevenTvEmoteId, cancellationToken);
        return new WishCommandResult(
            WishOutcome.Success,
            Wish: await ResponseDtoAsync(wish, settings, imageUrl, scope, cancellationToken),
            ExtendedExpiresAtUtc: target.ExpiresAtUtc);
    }

    private void AddApproveAudit(AuditActor actor, string channelName, Wish wish, string outcome, long? mergedIntoWishId) =>
        db.AddAuditEntry(
            actor,
            AuditActions.WishApprove,
            channelName: channelName,
            targetType: "wish",
            targetId: wish.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            details: new { wishId = wish.Id, outcome, mergedIntoWishId });

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
            .Select(c => new LockedChannel(c.ChannelName, c.ActiveEmoteSetCapacity, c.TwitchChannelId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    // A protection change is a decision (E25; plan #484 2.6, Sol-P1-1): behind the channel lock, lock the channel's
    // active wishes ascending by id, read them only afterwards, bump their DecisionVersion and drop a queued
    // removal, so a removal queued under the old threshold can never be carried out under the new one. The
    // caller commits this together with the settings row and its audit entry. Presented wishes stay untouched
    // (before K4 none carries a queue; K4 decides for bot-remove), and so does a wish with an open bot attempt.
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

        // #487 plan 2.5a (b): a wish with an open bot attempt is skipped; the change comes too late for it, like a 409.
        foreach (var wish in wishes.Where(w => !WishBotAttemptGuard.Blocks(w)))
        {
            wish.DecisionVersion++;
            wish.RemovalQueuedAtUtc = null;
            wish.RemovalQueuedVersion = null;
        }
    }

    // The settings with the channel's effective bot state (#487 plan 2.3). Only an activated channel needs the channel's
    // Twitch id and the bot account's state; a manual one costs no further read.
    private async Task<EffectiveSettings> LoadSettingsAsync(string channelId, CancellationToken cancellationToken)
    {
        var row = await db.ChannelWishSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
        if (row is null)
        {
            return EffectiveSettings.Defaults;
        }

        var settings = ToSettings(row);
        if (!row.BotModeEnabled)
        {
            return settings;
        }

        var twitchChannelId = await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => c.TwitchChannelId)
            .FirstOrDefaultAsync(cancellationToken);
        return settings with { ChannelMode = await botChannelStates.ReadAsync(row, twitchChannelId, cancellationToken) };
    }

    // The stored settings only, for the D7 rule, which hangs on the stored activation and needs no channel state.
    private async Task<EffectiveSettings> LoadStoredSettingsAsync(string channelId, CancellationToken cancellationToken)
    {
        var row = await db.ChannelWishSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
        return row is null ? EffectiveSettings.Defaults : ToSettings(row);
    }

    // The reads behind a response are decoration of a command that has already committed: a failure there must
    // never turn the committed command into an error (the manager would repeat it and book it twice). A caller's
    // own cancellation still propagates.
    private async Task<string?> TryImageUrlOfAsync(string channelId, string sevenTvEmoteId, CancellationToken cancellationToken)
    {
        try
        {
            return await ImageUrlOfAsync(channelId, sevenTvEmoteId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reading the wish image after a committed command failed for channel {ChannelId}; answering without an image", channelId);
            return null;
        }
    }

    private async Task<bool> TryIsSetFullAsync(int? capacity, string channelId, CancellationToken cancellationToken)
    {
        if (capacity is not { } limit)
        {
            return false;
        }

        try
        {
            return await db.EmoteSlots.CountAsync(s => !s.IsArchived && !s.SeedUnsettled && s.Emote.ChannelId == channelId, cancellationToken) >= limit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Counting the set's entries after a committed command failed for channel {ChannelId}; answering without the full-set hint", channelId);
            return false;
        }
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
        // The empty K6 placeholder is no emote: nothing runs "for" it (the CHECK keeps it out of every running state).
        if (sevenTvEmoteId.Length == 0)
        {
            return Task.FromResult<long?>(null);
        }

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
        WishCaptureAccess? access,
        CancellationToken cancellationToken,
        bool recordPreviousExpiry = false,
        bool lockChannel = false)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        ArgumentNullException.ThrowIfNull(actor);

        var scope = await ResolveScopeAsync(channelName, access, cancellationToken);
        if (scope is null)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        var channelId = scope.Id;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // The K6 commands on an open wish stand in line with the reconcile and the capture's expiry, which both hold
        // the channel row (plan #486 2.6): channel row first, then the wish row.
        if (lockChannel && (await db.LockChannelsByIdsAsync([channelId], cancellationToken)).Count == 0)
        {
            return WishCommandResult.Of(WishOutcome.ChannelNotFound);
        }

        await db.LockWishesByIdsAsync([wishId], cancellationToken);
        var wish = await db.Wishes.FirstOrDefaultAsync(w => w.Id == wishId && w.ChannelId == channelId, cancellationToken);
        if (wish is null)
        {
            return WishCommandResult.Of(WishOutcome.WishNotFound);
        }

        // #487 plan 2.5a (b): an open bot attempt blocks every command, before the command looks at the wish.
        if (WishBotAttemptGuard.Blocks(wish))
        {
            db.ChangeTracker.Clear();
            return WishCommandResult.Of(WishOutcome.StateConflict);
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
        step.AfterCount?.Invoke(wish);

        var conflict = await SaveOrConflictAsync(transaction, channelId, wish.SevenTvEmoteId, cancellationToken);
        if (conflict is not null)
        {
            return conflict;
        }

        var channel = scope.Name;
        await AuditAndCommitAsync(transaction, actor, auditAction, channel, wish, cancellationToken, step.AuditDurationDays, previousExpiry);
        await changedPublisher.PublishAsync(channel, cancellationToken);
        if (step.TriggerReconcile)
        {
            await TriggerReconcileAsync(channelId, cancellationToken);
        }

        if (step.NudgeBot && botNudge is not null)
        {
            await botNudge.NudgeAsync(cancellationToken);
        }

        var imageUrl = await TryImageUrlOfAsync(channelId, wish.SevenTvEmoteId, cancellationToken);
        return new WishCommandResult(WishOutcome.Success, Wish: await ResponseDtoAsync(wish, settings, imageUrl, scope, cancellationToken));
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
        StageWishAudit(actor, action, channelName, wish, auditDurationDays, previousExpiresAtUtc, removal);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // The standard details of a wish command's audit entry, staged for the caller's save.
    private void StageWishAudit(
        AuditActor actor,
        string action,
        string channelName,
        Wish wish,
        int? auditDurationDays,
        DateTime? previousExpiresAtUtc,
        RemovalReport? removal,
        string? fromStatus = null)
    {
        // previousExpiresAtUtc only on a late decision (contract 2.7), the reported entry only on a booked removal
        // (plan #485 2.1a), fromStatus only on a delete (plan #500 2.3); the details stay free of alias, name and login.
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

        if (fromStatus is not null)
        {
            details["fromStatus"] = fromStatus;
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

    // #487 plan 2.1 point 2: re-add and retry start the bot afresh, so the runner never takes the wish for a crash remnant
    // of an earlier attempt. An open repair phase (BotRepairAtUtc with its evidence) is the one thing that stays (2.5a
    // (h)): manager commands never touch it.
    private static void ResetBotAttempt(Wish wish)
    {
        wish.BotWriteAtUtc = null;
        wish.BotAttemptCount = 0;
        wish.BotNextAttemptAtUtc = null;
        wish.BotSetId = null;
        wish.BotAttemptVersion = null;
        wish.BotRemoveAtUtc = null;
        wish.BotAddAckAtUtc = null;
        wish.BotConfirmDeadlineAtUtc = null;
        if (wish.BotRepairAtUtc is null)
        {
            wish.BotRemoveEvidence = null;
        }
    }

    // The extension of ExtendAsync, shared with the approval that becomes one (plan #486 2.6, Sol-P2-9): only a wish
    // with an expiry, E12 by the channel's default from the current expiry or from now when that has passed. Returns
    // the extension length for the audit entry, or null (and changes nothing) when the wish cannot be extended; one
    // without an expiry changes its duration through PATCH.
    private static int? TryExtend(Wish wish, EffectiveSettings settings, DateTime now)
    {
        var target = WishTransitions.Target(
            wish.Status, wish.StatusReason, WishAction.Extend, new WishTransitionContext(HasExpiry: wish.ExpiresAtUtc is not null));
        if (target is null || wish.ExpiresAtUtc is not { } expiresAt)
        {
            return null;
        }

        var extensionDays = settings.DefaultDurationDays;
        wish.Status = target;
        wish.ExpiresAtUtc = WishSchedule.ExtendedExpiresAt(expiresAt, now, extensionDays);
        wish.ExtensionCount++;
        return extensionDays;
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

    internal static string[] StatusesOf(WishListView view, WishBotChannelMode channelMode)
    {
        // An approved wish needs a human while the channel runs manually or has fallen back (spec 5 "or the bot is
        // down", AK79, #487 [W1]); only while the bot acts for the channel is it the bot's turn.
        var approvedNeedsAction = channelMode != WishBotChannelMode.Bot;
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

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // The emote name of a wish as a manual entry accepts it: 1 to EmoteNameMaxLength code points, no control character.
    private static bool IsValidEmoteName(string name) =>
        name.EnumerateRunes().Count() is >= 1 and <= WishLimits.EmoteNameMaxLength && !name.Any(char.IsControl);

    // Plan #486 2.8a: the emote row's name; while that is empty, the entry's name.
    private static string EmoteNameOf(EmoteSlot slot) => slot.Emote.Name.Length > 0 ? slot.Emote.Name : slot.Name;

    // SQLSTATE 55P03 of a NOWAIT lock. Npgsql counts it as transient, so its execution strategy hands it on wrapped in an
    // InvalidOperationException; a context without that strategy throws the PostgresException itself.
    private static bool IsLockNotAvailable(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable }
        || exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable };

    // Confirmed on the id (plan #486 2.4a), checked against the row as it is now.
    internal static bool IsConfirmedOn(WishCaptureAccess? access, string? rowTwitchChannelId) =>
        access is { IdConfirmed: true }
        && rowTwitchChannelId is not null
        && string.Equals(access.TwitchChannelId, rowTwitchChannelId, StringComparison.Ordinal);

    // Length counts Unicode code points like 7TV and the frontend's alias check, not UTF-16 units.
    private static bool IsValidAlias(string alias) =>
        alias.EnumerateRunes().Count() is >= 1 and <= WishLimits.AliasMaxLength
        && !alias.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));

    /// <summary>
    /// The one rule behind <see cref="CanChangeSettingsAsync"/>, shared with the reward read's <c>canEdit</c> and checked
    /// again behind the channel lock by every settings and reward write: a channel without a stored activation answers yes
    /// (the caller is a manager already); with one only the broadcaster (by id), the manager who activated the bot and a
    /// global admin. The stored activation decides, not the effective state (#487 [W2]): a suspended or fallen-back
    /// channel keeps D7.
    /// </summary>
    internal static bool CanChangeSettings(
        EffectiveSettings settings, string? channelTwitchId, string callerTwitchUserId, bool isGlobalAdmin)
    {
        if (!settings.BotModeEnabled || isGlobalAdmin)
        {
            return true;
        }

        return (channelTwitchId is not null && string.Equals(channelTwitchId, callerTwitchUserId, StringComparison.Ordinal))
            || (settings.ActivatedByTwitchUserId is not null && string.Equals(settings.ActivatedByTwitchUserId, callerTwitchUserId, StringComparison.Ordinal));
    }

    internal static EffectiveSettings ToSettings(ChannelWishSettings row) => new(
        row.DefaultDurationDays,
        row.ProbationLeadDays,
        row.ProtectionThresholdPerDay,
        row.BotModeEnabled,
        row.BotSuspendedReason,
        row.BotModeActivatedByTwitchUserId,
        row.PendingExpiryDays);

    // Redeemer data of a captured wish (plan #486 2.4a) only with includeRequestData: the text, the parsed name, the
    // reward's label and the login. A login of a manual entry is K1 data and stays.
    internal static WishDto ToDto(
        Wish w, EffectiveSettings settings, string? imageUrl, bool includeRequestData = false, string? rewardLabel = null) => new(
        w.Id,
        w.SevenTvEmoteId,
        w.EmoteName,
        w.Alias,
        w.Source,
        w.Status,
        w.StatusReason,
        includeRequestData || w.Source != WishSource.Irc ? w.RequesterLogin : null,
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
        // #487 [W9]: only while the write is under way; an old stamp in any later state is history.
        w.BotWriteAtUtc is not null && w.Status is WishStatus.Approved or WishStatus.Unconfirmed,
        w.BotNextAttemptAtUtc,
        w.RemovalQueuedAtUtc,
        settings.IsBotMode ? "bot" : "manual",
        includeRequestData ? w.RequestText : null,
        includeRequestData ? w.RequestedName : null,
        w.MatchKind,
        includeRequestData ? rewardLabel : null,
        w.MergedIntoWishId);

    private sealed record LockedChannel(string ChannelName, int? ActiveEmoteSetCapacity, string? TwitchChannelId);

    // The channel a call works on: its id, its current name, and whether redeemer data may leave (plan #486 2.4a).
    private sealed record ChannelScope(string Id, string Name, bool IncludeRequestData);

    // What a booked removal reported, for its audit entry only.
    private sealed record RemovalReport(string SlotId, DateTime SlotAddedAtUtc, string EmoteSetId, string? Evidence);

    // What a transition command decided: a refusal, or success with an optional durationDays for the audit entry,
    // whether the reconcile runs after the commit, what is written after the decision was counted (a field bound to
    // the new DecisionVersion), and whether the bot account loop is nudged after the commit (#487 plan 2.9).
    private sealed record CommandStep(
        WishCommandResult? Refusal,
        int? AuditDurationDays = null,
        bool TriggerReconcile = false,
        bool NudgeBot = false,
        Action<Wish>? AfterCount = null)
    {
        public static readonly CommandStep Done = new((WishCommandResult?)null);
        public static readonly CommandStep DoneAndReconcile = new((WishCommandResult?)null, TriggerReconcile: true);
        public static readonly CommandStep Conflict = new(WishCommandResult.Of(WishOutcome.StateConflict));
        public static readonly Task<CommandStep> DoneTask = Task.FromResult(Done);
        public static readonly Task<CommandStep> ConflictTask = Task.FromResult(Conflict);

        public static CommandStep Refuse(WishOutcome outcome) => new(WishCommandResult.Of(outcome));
    }

    // ChannelMode is the channel's effective bot state (#487 plan 2.3); ToSettings leaves it manual, LoadSettingsAsync and
    // the overview fill it for an activated channel.
    internal sealed record EffectiveSettings(
        int DefaultDurationDays,
        int ProbationLeadDays,
        decimal? ProtectionThresholdPerDay,
        bool BotModeEnabled,
        string? BotSuspendedReason,
        string? ActivatedByTwitchUserId,
        int PendingExpiryDays,
        WishBotChannelMode ChannelMode = WishBotChannelMode.Manual)
    {
        public static readonly EffectiveSettings Defaults = new(
            WishLimits.DefaultDurationDays, WishLimits.DefaultProbationLeadDays, null, false, null, null, WishLimits.DefaultPendingExpiryDays);

        /// <summary>The bot acts for the channel right now; <c>mode</c> in the DTO is <c>bot</c> only then.</summary>
        public bool IsBotMode => ChannelMode == WishBotChannelMode.Bot;

        public WishSettingsDto ToDto() => new(
            DefaultDurationDays, ProbationLeadDays, ProtectionThresholdPerDay, BotModeEnabled, BotSuspendedReason, PendingExpiryDays);
    }
}
