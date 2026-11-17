using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The state-based reconcile of wishes against slots (E9, spec 6.3, plan T7). See
/// <see cref="IWishReconcileService"/> for the contract.
/// <para>
/// <b>Flow.</b> (1) One pre-check query without a wall clock: does the channel have a wish in <c>approved</c>,
/// <c>active</c> or <c>presented</c>, or an open returner (plan Sol-R2-3)? No: return without a transaction.
/// (2) Transaction: the channel row (<c>FOR NO KEY UPDATE</c>), then the affected wish rows (<c>FOR UPDATE</c>,
/// ascending id), then the slots. (3) Linked <c>active</c> and <c>presented</c> wishes against their slot:
/// archived, unchanged (rename, confirmation refresh, authoritative healing), date confirmation, or generation
/// change. (4) Unlinked <c>approved</c> and <c>removed_externally</c> wishes through <see cref="WishSlotMatcher"/>.
/// (5) Every changed wish is saved on its own behind a savepoint; a hit on a wish unique index rolls back to
/// it and leaves that wish as it was. (6) Commit, then at most one <c>wish.changed</c>, only for a change a
/// client can see.
/// </para>
/// <para>
/// <b>Start of a date confirmation.</b> The start moves with the slot's confirmed date only when it was taken
/// from the slot: it equals the generation (<see cref="Wish.SlotAddedAtUtc"/>), or, while the generation is
/// unknown (an adoption of an undated slot), it equals <see cref="Wish.SlotConfirmedAtUtc"/>, which the adoption
/// stamps with the same instant as its stand-in start. A start the manager chose through <c>adopt</c>, or the old
/// start a returned wish kept, stays; only the generation and the authoritative flag follow the slot. While the
/// generation is unknown and the slot undated the confirmation stamp is not refreshed, so that equality holds
/// until the slot gets its date.
/// </para>
/// </summary>
public sealed class WishReconcileService(
    IServiceScopeFactory scopeFactory,
    IWishChangedPublisher changedPublisher,
    TimeProvider timeProvider,
    ILogger<WishReconcileService> logger) : IWishReconcileService
{
    /// <summary>How old <see cref="Wish.SlotConfirmedAtUtc"/> may get before an unchanged slot refreshes it (plan finding 19).</summary>
    public static readonly TimeSpan ConfirmationRefreshInterval = TimeSpan.FromMinutes(30);

    private const string RunningEmoteIndex = "IX_Wishes_ChannelId_SevenTvEmoteId_Running";
    private const string LiveSlotIndex = "IX_Wishes_SlotId_Live";
    private const string SavepointName = "wish_reconcile";

    // The states the reconcile always looks at; removed_externally only as an open returner.
    private static readonly string[] ReconciledStatuses = [WishStatus.Approved, WishStatus.Active, WishStatus.Presented];

    // The running states whose SlotId is a live link. An approved wish's SlotId is history (a re-add keeps
    // it, plan finding 5), so its old slot stays a candidate.
    private static readonly string[] LinkHoldingStatuses = [WishStatus.Unconfirmed, WishStatus.Active, WishStatus.Presented];

    private static readonly string[] RunningStatuses = [.. WishStatus.Running];

    public async Task ReconcileChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelId);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await Affected(db, channelId).AnyAsync(cancellationToken))
        {
            return;
        }

        var announce = await RunAsync(db, channelId, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        if (announce is not null)
        {
            await changedPublisher.PublishAsync(announce, cancellationToken);
        }
    }

    // The wishes a run looks at, as one query: the pre-check is its Any, the run locks its ids. An open
    // returner is a removed_externally wish whose emote has no running wish in the channel and for which an
    // admissible slot (plan finding 7) of the same emote with the wish's alias lies in the matcher's window
    // around EndedAtUtc and is linked to no running wish. The bounds come from WishLimits like the matcher's,
    // and nothing compares with "now": a run days later still finds the returner, an old row without a
    // candidate never triggers a run.
    private static IQueryable<Wish> Affected(AppDbContext db, string channelId)
    {
        var reconciled = ReconciledStatuses;
        var running = RunningStatuses;
        var linkHolding = LinkHoldingStatuses;
        var lookBack = WishLimits.ReturnLookBack;
        var window = WishLimits.AdoptionWindow;

        return db.Wishes.Where(w => w.ChannelId == channelId
            && (reconciled.Contains(w.Status)
                || (w.Status == WishStatus.RemovedExternally
                    && w.EndedAtUtc != null
                    && !db.Wishes.Any(o => o.ChannelId == channelId && o.SevenTvEmoteId == w.SevenTvEmoteId && running.Contains(o.Status))
                    && db.EmoteSlots.Any(s => s.Emote.ChannelId == channelId
                        && s.Emote.SevenTvEmoteId == w.SevenTvEmoteId
                        && !s.IsArchived
                        && !s.SeedUnsettled
                        && s.AddedAt != null
                        && s.Name == w.Alias
                        && s.AddedAt >= w.EndedAtUtc - lookBack
                        && s.AddedAt <= w.EndedAtUtc + window
                        && !db.Wishes.Any(l => l.SlotId == s.Id && linkHolding.Contains(l.Status))))));
    }

    // Steps 2 to 6. Returns the channel name to announce, or null when nothing a client sees has changed.
    private async Task<string?> RunAsync(AppDbContext db, string channelId, DateTime now, CancellationToken cancellationToken)
    {
        // Savepoints are explicit, one per wish; EF's own per-save savepoint would only duplicate them.
        db.Database.AutoSavepointsEnabled = false;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Channel row first (plan finding 11): a purge or an identity merge waits here, and a channel deleted
        // meanwhile ends the run without effect.
        if ((await db.LockChannelsByIdsAsync([channelId], cancellationToken)).Count == 0)
        {
            return null;
        }

        var channelName = await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => c.ChannelName)
            .FirstAsync(cancellationToken);

        var ids = await Affected(db, channelId).Select(w => w.Id).ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return null;
        }

        // Then the wish rows, ascending id, and only then read them (plan 2.3): a command that waited for one of
        // them sees what this run committed, and this run sees what a command committed before it.
        await db.LockWishesByIdsAsync(ids, cancellationToken);
        var wishes = await db.Wishes.Where(w => ids.Contains(w.Id) && w.ChannelId == channelId).ToListAsync(cancellationToken);
        var others = await db.Wishes.AsNoTracking()
            .Where(w => w.ChannelId == channelId && !ids.Contains(w.Id) && RunningStatuses.Contains(w.Status))
            .Select(w => new OtherWish(w.SevenTvEmoteId, w.Status, w.SlotId))
            .ToListAsync(cancellationToken);

        var emoteIds = wishes.Select(w => w.SevenTvEmoteId).Distinct(StringComparer.Ordinal).ToArray();
        var linkedSlotIds = wishes.Where(w => w.SlotId is not null).Select(w => w.SlotId!).Distinct(StringComparer.Ordinal).ToArray();
        var slots = await db.EmoteSlots.AsNoTracking()
            .Where(s => s.Emote.ChannelId == channelId && (emoteIds.Contains(s.Emote.SevenTvEmoteId) || linkedSlotIds.Contains(s.Id)))
            .Select(s => new SlotRow(s.Id, s.Emote.SevenTvEmoteId, s.Name, s.AddedAt, s.AddedAtFromRest, s.IsArchived, s.ArchivedAt, s.SeedUnsettled))
            .ToListAsync(cancellationToken);

        var pass = new Pass(wishes, others, slots, now);
        var before = wishes.ToDictionary(w => w.Id, Visible.Of);

        // Step 3: linked live wishes against their slot.
        foreach (var wish in wishes.Where(w => IsLive(w.Status) && w.SlotId is not null).OrderBy(w => w.Id).ToList())
        {
            if (pass.SlotById(wish.SlotId!) is { } slot)
            {
                pass.ReconcileLinked(wish, slot);
                await SaveOneAsync(db, transaction, channelId, wish, cancellationToken);
            }
        }

        // Step 4: wishes without a valid link, in creation order, so the older of two contenders wins a slot.
        // An approved wish counts as unlinked whatever its SlotId says (a re-add keeps the old one).
        var unlinked = wishes
            .Where(w => (w.Status == WishStatus.Approved && w.BotWriteAtUtc is null)
                || w.Status == WishStatus.RemovedExternally
                || (w.Status == WishStatus.Active && w.SlotId is null))
            .OrderBy(w => w.CreatedAtUtc)
            .ThenBy(w => w.Id)
            .ToList();
        foreach (var wish in unlinked)
        {
            pass.ReconcileUnlinked(wish);
            await SaveOneAsync(db, transaction, channelId, wish, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return wishes.Any(w => before[w.Id] != Visible.Of(w)) ? channelName : null;
    }

    // Step 5: one wish, one save, behind a savepoint. A unique-index hit (a running wish of the emote or a live
    // link of the slot committed meanwhile without this run seeing it) rolls back to the savepoint and puts
    // the tracked row back to what the database holds, so no later save retries it; the run goes on. Every
    // other error propagates and the transaction rolls back on dispose.
    private async Task SaveOneAsync(
        AppDbContext db, IDbContextTransaction transaction, string channelId, Wish wish, CancellationToken cancellationToken)
    {
        var entry = db.Entry(wish);
        if (entry.State != EntityState.Modified)
        {
            return;
        }

        await transaction.CreateSavepointAsync(SavepointName, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(SavepointName, cancellationToken);
        }
        catch (DbUpdateException ex) when (WishIndexOf(ex) is { } index)
        {
            await transaction.RollbackToSavepointAsync(SavepointName, cancellationToken);
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;
            logger.LogInformation(
                "Wish reconcile for channel {ChannelId}: wish {WishId} keeps its state; its change hit {IndexName}.",
                channelId, wish.Id, index);
        }
    }

    private static string? WishIndexOf(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
            && postgres.ConstraintName is RunningEmoteIndex or LiveSlotIndex
            ? postgres.ConstraintName
            : null;

    private static bool IsLive(string status) => status is WishStatus.Active or WishStatus.Presented;

    // The decisions of one run over in-memory state. Tracked wishes change in place; the sets of running
    // emotes and live links are derived from them on every question, so a wish moved earlier in the run
    // (a return, a link, a revert after a savepoint) is seen by the next one.
    private sealed class Pass(List<Wish> wishes, List<OtherWish> others, List<SlotRow> slots, DateTime now)
    {
        private readonly Dictionary<string, SlotRow> _slotsById = slots.ToDictionary(s => s.Id, StringComparer.Ordinal);

        public SlotRow? SlotById(string slotId) => _slotsById.GetValueOrDefault(slotId);

        // Step 3, by the slot's state (plan finding 19).
        public void ReconcileLinked(Wish wish, SlotRow slot)
        {
            if (slot.IsArchived)
            {
                // A missed hook still carries the real time while the slot stays archived; a stamp before the
                // last confirmation cannot be this stay's removal.
                var endedAt = slot.ArchivedAt is { } archivedAt
                    && (wish.SlotConfirmedAtUtc is not { } confirmed || archivedAt >= confirmed)
                    ? archivedAt
                    : now;
                RemoveExternally(wish, endedAt);
                return;
            }

            if (slot.SeedUnsettled)
            {
                return;
            }

            // A rename keeps slot, wish and deadline; the alias follows, bot ownership ends (R3-2).
            if (!string.Equals(wish.Alias, slot.Name, StringComparison.Ordinal))
            {
                wish.Alias = slot.Name;
                EndOwnership(wish);
            }

            if (slot.AddedAt is not { } addedAt)
            {
                // An undated slot says nothing about its generation; the next dated sync decides.
                return;
            }

            if (wish.SlotAddedAtUtc is not { } generation)
            {
                // Generation not known yet (adoption of an undated slot): the slot's date is a confirmation,
                // never a change of generation.
                ConfirmDate(wish, slot, startFromSlot: wish.StartsAtUtc == wish.SlotConfirmedAtUtc);
                return;
            }

            if (addedAt == generation)
            {
                if (wish.SlotConfirmedAtUtc is not { } confirmed || now - confirmed >= ConfirmationRefreshInterval)
                {
                    wish.SlotConfirmedAtUtc = now;
                }

                // Healing (Sol-R2-2): the flag must not stay false while the slot is authoritative.
                if (slot.AddedAtFromRest && !wish.StartsAtAuthoritative)
                {
                    wish.StartsAtAuthoritative = true;
                }

                return;
            }

            if (!wish.StartsAtAuthoritative && slot.AddedAtFromRest && addedAt <= generation)
            {
                // AK8: the provisional date is confirmed by an authoritative one not after it.
                ConfirmDate(wish, slot, startFromSlot: wish.StartsAtUtc == generation);
                return;
            }

            // Generation change: the slot was gone and is back without a reconcile having seen it. Removed at the
            // last confirmation, the earliest possible time (at the expense of the return); step 4 of this run
            // decides on the return.
            RemoveExternally(wish, wish.SlotConfirmedAtUtc ?? now);
        }

        // Step 4: the matcher decides, among admissible slots linked to no other running wish.
        public void ReconcileUnlinked(Wish wish)
        {
            switch (wish.Status)
            {
                case WishStatus.Approved:
                    if (Match(wish, wish.Status, wish.ApprovedAtUtc) is { } approvedSlot)
                    {
                        Link(wish, approvedSlot);
                    }

                    break;

                case WishStatus.RemovedExternally:
                    // The pre-filter: a return needs the emote's running place free.
                    if (!IsEmoteRunningElsewhere(wish) && Match(wish, wish.Status, wish.ApprovedAtUtc) is { } returnSlot)
                    {
                        Return(wish, returnSlot);
                    }

                    break;

                case WishStatus.Active when wish.SlotId is null:
                    // K4's exception: an active wish without a slot gets one, without any change to state, start
                    // or ownership. The approved rule decides, from the approval on.
                    if (Match(wish, WishStatus.Approved, wish.ApprovedAtUtc ?? wish.CreatedAtUtc) is { } ownSlot)
                    {
                        wish.SlotId = ownSlot.Id;
                        wish.Alias = ownSlot.Name;
                        HoldGeneration(wish, ownSlot);
                    }

                    break;
            }
        }

        private SlotRow? Match(Wish wish, string ruleStatus, DateTime? approvedAtUtc)
        {
            var linked = LinkedSlotIdsExcept(wish);
            var candidates = slots
                .Where(s => string.Equals(s.SevenTvEmoteId, wish.SevenTvEmoteId, StringComparison.Ordinal)
                    && !s.IsArchived
                    && !s.SeedUnsettled
                    && s.AddedAt is not null
                    && !linked.Contains(s.Id))
                .Select(s => new WishSlotCandidate(s.Id, s.SevenTvEmoteId, s.Name, s.AddedAt!.Value))
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var result = WishSlotMatcher.Match(
                new WishMatchInput(ruleStatus, wish.SevenTvEmoteId, wish.Alias, approvedAtUtc, wish.EndedAtUtc), candidates);
            return result.SlotId is { } slotId ? _slotsById[slotId] : null;
        }

        // approved -> active (kept when permanent): start = AddedAt, expiry from start and duration, alias
        // follows the slot, LinkOrigin auto, never bot ownership.
        private void Link(Wish wish, SlotRow slot)
        {
            var target = WishTransitions.Target(
                wish.Status, wish.StatusReason, WishAction.Link, new WishTransitionContext(Permanent: wish.Permanent));
            if (target is null)
            {
                return;
            }

            var start = slot.AddedAt!.Value;
            wish.Status = target;
            wish.StatusReason = null;
            wish.StatusChangedAtUtc = now;
            wish.EndedAtUtc = WishStatus.IsEnded(target) ? now : null;
            wish.SlotId = slot.Id;
            wish.Alias = slot.Name;
            wish.LinkOrigin = WishLinkOrigin.Auto;
            wish.StartsAtUtc = start;
            wish.ExpiresAtUtc = WishSchedule.ExpiresAt(start, wish.DurationDays);
            HoldGeneration(wish, slot);
            if (target == WishStatus.Active)
            {
                wish.FirstActivatedAtUtc ??= now;
            }
        }

        // removed_externally -> the state before, with the old deadline and without ownership: presented with
        // the reason that stayed, else active. LinkOrigin stays (the link continues the same entry).
        private void Return(Wish wish, SlotRow slot)
        {
            var target = WishTransitions.Target(
                wish.Status, wish.StatusReason, WishAction.Return,
                new WishTransitionContext(HadPresentedReason: wish.StatusReason is not null));
            if (target is null)
            {
                return;
            }

            wish.Status = target;
            wish.StatusChangedAtUtc = now;
            wish.EndedAtUtc = null;
            wish.SlotId = slot.Id;
            wish.Alias = slot.Name;
            HoldGeneration(wish, slot);
            if (target == WishStatus.Active)
            {
                wish.StatusReason = null;
                wish.FirstActivatedAtUtc ??= now;
            }
        }

        // active/presented -> removed_externally. SlotId and StatusReason stay (the return needs both).
        private void RemoveExternally(Wish wish, DateTime endedAt)
        {
            var target = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.ExternalRemoval);
            if (target is null)
            {
                return;
            }

            wish.Status = target;
            wish.StatusChangedAtUtc = now;
            wish.EndedAtUtc = endedAt;
            EndOwnership(wish);
        }

        // AK8 and its generation-unknown twin: the start (and the expiry by the same difference) follows the
        // slot's authoritative date only when it was taken from the slot.
        private void ConfirmDate(Wish wish, SlotRow slot, bool startFromSlot)
        {
            if (startFromSlot && slot.AddedAtFromRest && wish.StartsAtUtc is { } oldStart && slot.AddedAt is { } newStart)
            {
                if (wish.ExpiresAtUtc is { } oldExpiry)
                {
                    var shifted = WishSchedule.Shift(oldStart, oldExpiry, newStart);
                    wish.StartsAtUtc = shifted.StartsAtUtc;
                    wish.ExpiresAtUtc = shifted.ExpiresAtUtc;
                }
                else
                {
                    wish.StartsAtUtc = newStart;
                }
            }

            HoldGeneration(wish, slot);
        }

        // Wherever the reconcile holds a generation (link, return, confirmation) the authoritative flag follows
        // the slot (Sol-R2-2), and the confirmation is stamped.
        private void HoldGeneration(Wish wish, SlotRow slot)
        {
            wish.SlotAddedAtUtc = slot.AddedAt;
            wish.SlotConfirmedAtUtc = now;
            wish.StartsAtAuthoritative = slot.AddedAtFromRest;
        }

        // An ownership change counts as a decision and drops a queued bot removal (E25).
        private static void EndOwnership(Wish wish)
        {
            if (!wish.BotOwned)
            {
                return;
            }

            wish.BotOwned = false;
            wish.BotOwnedAddedAtUtc = null;
            wish.DecisionVersion++;
            wish.RemovalQueuedAtUtc = null;
            wish.RemovalQueuedVersion = null;
        }

        private bool IsEmoteRunningElsewhere(Wish wish) =>
            wishes.Any(w => w.Id != wish.Id
                    && WishStatus.IsRunning(w.Status)
                    && string.Equals(w.SevenTvEmoteId, wish.SevenTvEmoteId, StringComparison.Ordinal))
                || others.Any(o => string.Equals(o.SevenTvEmoteId, wish.SevenTvEmoteId, StringComparison.Ordinal));

        private HashSet<string> LinkedSlotIdsExcept(Wish wish)
        {
            var linked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in wishes)
            {
                if (w.Id != wish.Id && w.SlotId is { } slotId && Array.IndexOf(LinkHoldingStatuses, w.Status) >= 0)
                {
                    linked.Add(slotId);
                }
            }

            foreach (var o in others)
            {
                if (o.SlotId is { } slotId && Array.IndexOf(LinkHoldingStatuses, o.Status) >= 0)
                {
                    linked.Add(slotId);
                }
            }

            return linked;
        }
    }

    private sealed record SlotRow(
        string Id,
        string SevenTvEmoteId,
        string Name,
        DateTime? AddedAt,
        bool AddedAtFromRest,
        bool IsArchived,
        DateTime? ArchivedAt,
        bool SeedUnsettled);

    // A running wish of the channel outside this run's locked set (unconfirmed, or one that became running
    // after the ids were read).
    private sealed record OtherWish(string SevenTvEmoteId, string Status, string? SlotId);

    // What a client can see of a wish (the WishDto's stored fields). A change of these is what announces
    // wish.changed; a refresh of SlotConfirmedAtUtc or of the generation alone is not.
    private readonly record struct Visible(
        string Status,
        string? StatusReason,
        string Alias,
        DateTime? StartsAtUtc,
        bool StartsAtAuthoritative,
        DateTime? ExpiresAtUtc,
        DateTime? EndedAtUtc,
        string? SlotId,
        string? LinkOrigin,
        bool BotOwned,
        DateTime? RemovalQueuedAtUtc)
    {
        public static Visible Of(Wish w) => new(
            w.Status, w.StatusReason, w.Alias, w.StartsAtUtc, w.StartsAtAuthoritative, w.ExpiresAtUtc, w.EndedAtUtc,
            w.SlotId, w.LinkOrigin, w.BotOwned, w.RemovalQueuedAtUtc);
    }
}
