using System.Text.RegularExpressions;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The manager-facing wish service, part 1 (plan T4): manual entry, "take over from the set", settings and
/// the reads. See <see cref="IWishService"/> for the contract.
/// <para>
/// <b>Locks (plan 2.3).</b> Every write opens a transaction and takes the channel row first
/// (<see cref="RowLocks.LockChannelsByIdsAsync"/>, <c>FOR NO KEY UPDATE</c>), because it inserts a row with
/// a channel foreign key or writes a <c>SlotId</c>: without that lock the insert reserves its unique key and
/// then waits for <c>KEY SHARE</c> on the channel while the identity merge, which holds the channel
/// <c>FOR UPDATE</c>, waits for the reserved key. An adoption with a wish id then locks that wish row
/// (<see cref="RowLocks.LockWishesByIdsAsync"/>) and only afterwards reads it, so the state it decides on is
/// the committed one and stays so. Because of that lock the state is stable within the transaction; the
/// unique indexes remain as a belt and map to the conflict outcomes, never to a 500.
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

        if (!IsValidAlias(alias) || emoteName.Length > WishLimits.EmoteNameMaxLength || emoteName.Any(char.IsControl))
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

    // The audit entry needs the wish id, which an insert only has after its first save; both saves are in
    // the one transaction, so the entry exists if and only if the command committed.
    private async Task AuditAndCommitAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        AuditActor actor,
        string action,
        string channelName,
        Wish wish,
        CancellationToken cancellationToken)
    {
        db.AddAuditEntry(
            actor,
            action,
            channelName: channelName,
            targetType: "wish",
            targetId: wish.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            details: new { wishId = wish.Id, sevenTvEmoteId = wish.SevenTvEmoteId, durationDays = wish.DurationDays });
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

    private static bool IsValidAlias(string alias) =>
        alias.Length is >= 1 and <= WishLimits.AliasMaxLength && !alias.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));

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
