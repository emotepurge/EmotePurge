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
/// <b>Flow.</b> (1) One pre-check query without a wall clock: does the channel have a wish in <c>pending</c>,
/// <c>approved</c>, <c>active</c> or <c>presented</c>, an open returner (plan Sol-R2-3), or a kept wish to
/// release? No: return without a transaction.
/// (2) Transaction: the channel row (<c>FOR NO KEY UPDATE</c>), then the affected wish rows (<c>FOR UPDATE</c>,
/// ascending id), then the slots. (3) Linked <c>active</c> and <c>presented</c> wishes against their slot:
/// archived, unchanged (rename, confirmation refresh, authoritative healing), date confirmation, or generation
/// change; then <c>kept</c> wishes whose stay is over give their slot back. (4) Unlinked <c>pending</c>,
/// <c>approved</c> and <c>removed_externally</c> wishes through <see cref="WishSlotMatcher"/>, an open wish
/// also gets its hint there (K6, plan #486 2.5).
/// (5) Every changed wish is saved on its own behind a savepoint, together with its audit entry; a hit on a
/// wish unique index rolls back to it and leaves that wish as it was (an open wish then shows
/// <c>already_running</c>). (6) Commit, then at most one <c>wish.changed</c>, only for a change a client can see.
/// </para>
/// <para>
/// <b>Date correction of a capture link (K6, plan #486 2.5, Sol-P1-3).</b> A late EventAPI delta stamps a new
/// slot with the time it was seen, so a capture can link an entry that was in the set before the redemption.
/// When the authoritative date replaces the provisional one and lies before <see cref="Wish.CreatedAtUtc"/>, a
/// wish the reconcile linked without any click (<c>irc</c>, <c>auto</c>, never approved) and nobody extended
/// goes back to <c>pending</c> with <c>already_in_set</c>, before start and expiry would become authoritative.
/// Hand entries, approved wishes, extended ones and <c>kept</c> keep the behaviour from before K6.
/// </para>
/// <para>
/// <b>Start of a date confirmation.</b> The start moves with the slot's confirmed date only when it was taken
/// from the slot: it equals the generation (<see cref="Wish.SlotAddedAtUtc"/>), or, while the generation is
/// unknown (an adoption of an undated slot), it equals <see cref="Wish.SlotConfirmedAtUtc"/>, which the adoption
/// stamps with the same instant as its stand-in start. A start the manager chose through <c>adopt</c>, or the old
/// start a returned wish kept, stays; only the generation and the authoritative flag follow the slot. While the
/// generation is unknown and the slot undated the confirmation stamp is not refreshed, so that equality holds
/// until the slot gets its date. A date after that stamp is a generation change, not a confirmation: the slot
/// left and came back unseen. A stand-in start that returns then takes the returned slot's date, so it still
/// follows the later authoritative one.
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
    private const string WishTargetType = "wish";
    private const string DateCorrectionCause = "date_correction";

    // The states the reconcile always looks at; removed_externally only as an open returner.
    private static readonly string[] ReconciledStatuses =
        [WishStatus.Pending, WishStatus.Approved, WishStatus.Active, WishStatus.Presented];

    // The states whose SlotId holds the slot against other wishes: the running ones with a live link, and
    // kept, which keeps its entry for good (operator ruling 2026-11-18). An approved wish's SlotId is history
    // (a re-add keeps it, plan finding 5), so its old slot stays a candidate.
    private static readonly string[] LinkHoldingStatuses =
        [WishStatus.Unconfirmed, WishStatus.Active, WishStatus.Presented, WishStatus.Kept];

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

        var announce = await RunAsync(db, channelId, cancellationToken);
        if (announce is not null)
        {
            await changedPublisher.PublishAsync(announce, cancellationToken);
        }
    }

    // The wishes a run looks at, as one query: the pre-check is its Any, the run locks its ids. An open
    // returner is a removed_externally wish whose emote has no running wish in the channel and for which an
    // admissible slot (plan finding 7) of the same emote with the wish's alias lies in the matcher's window
    // around EndedAtUtc and is held by no other wish (a running link or a kept one). The bounds come from
    // WishLimits like the matcher's, and nothing compares with "now": a run days later still finds the
    // returner, an old row without a candidate never triggers a run. A kept wish is affected only while its
    // stay is over and it still holds the slot (see ReleaseKept).
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
                        && !db.Wishes.Any(l => l.SlotId == s.Id && linkHolding.Contains(l.Status))))
                || (w.Status == WishStatus.Kept
                    && w.SlotId != null
                    && db.EmoteSlots.Any(s => s.Id == w.SlotId
                        && (s.IsArchived
                            || (s.AddedAt != null
                                && w.SlotConfirmedAtUtc != null
                                && s.AddedAt > w.SlotConfirmedAtUtc
                                && (w.SlotAddedAtUtc == null || s.AddedAt > w.SlotAddedAtUtc)))))));
    }

    // Steps 2 to 6. Returns the channel name to announce, or null when nothing a client sees has changed.
    private async Task<string?> RunAsync(AppDbContext db, string channelId, CancellationToken cancellationToken)
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

        // The run's clock is read only now: a slot writer this run waited for has committed, so its
        // provisional AddedAt lies before every confirmation this run stamps.
        var now = timeProvider.GetUtcNow().UtcDateTime;

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
            .Where(w => w.ChannelId == channelId
                && !ids.Contains(w.Id)
                && (RunningStatuses.Contains(w.Status) || (w.Status == WishStatus.Kept && w.SlotId != null)))
            .Select(w => new OtherWish(w.SevenTvEmoteId, w.Status, w.SlotId))
            .ToListAsync(cancellationToken);

        var emoteIds = wishes.Where(w => w.HasEmote).Select(w => w.SevenTvEmoteId).Distinct(StringComparer.Ordinal).ToArray();
        var linkedSlotIds = wishes.Where(w => w.SlotId is not null).Select(w => w.SlotId!).Distinct(StringComparer.Ordinal).ToArray();
        // An open name wish compares with every alias in the set, so then every live slot is read; the casing
        // rule is .NET's, not the database's.
        var allLive = wishes.Any(w => w.Status == WishStatus.Pending && !w.HasEmote && w.MatchKind == WishMatchKind.Name);
        var slots = await db.EmoteSlots.AsNoTracking()
            .Where(s => s.Emote.ChannelId == channelId
                && (emoteIds.Contains(s.Emote.SevenTvEmoteId) || linkedSlotIds.Contains(s.Id) || (allLive && !s.IsArchived)))
            .Select(s => new SlotRow(
                s.Id, s.Emote.SevenTvEmoteId, s.Name, s.Emote.Name, s.AddedAt, s.AddedAtFromRest, s.IsArchived, s.ArchivedAt, s.SeedUnsettled))
            .ToListAsync(cancellationToken);

        // The released links of these wishes (K6): the reconcile never offers such a slot to that wish again.
        var excluded = (await db.WishSlotExclusions.AsNoTracking()
                .Where(e => ids.Contains(e.WishId))
                .Select(e => new { e.WishId, e.SlotId })
                .ToListAsync(cancellationToken))
            .Select(e => (e.WishId, e.SlotId))
            .ToHashSet();
        var pendingExpiryDays = await db.ChannelWishSettings.AsNoTracking()
            .Where(s => s.ChannelId == channelId)
            .Select(s => (int?)s.PendingExpiryDays)
            .FirstOrDefaultAsync(cancellationToken) ?? WishLimits.DefaultPendingExpiryDays;

        var pass = new Pass(wishes, others, slots, excluded, pendingExpiryDays, now);
        var save = new Saver(db, transaction, channelId, channelName, pass, now, logger);
        var before = wishes.ToDictionary(w => w.Id, Visible.Of);

        // Step 3: linked live wishes against their slot.
        foreach (var wish in wishes.Where(w => IsLive(w.Status) && w.SlotId is not null).OrderBy(w => w.Id).ToList())
        {
            if (pass.SlotById(wish.SlotId!) is { } slot)
            {
                pass.ReconcileLinked(wish, slot);
                await save.OneAsync(wish, cancellationToken);
            }
        }

        // Step 3b: kept wishes whose stay is over give their slot back, before step 4 looks for candidates.
        foreach (var wish in wishes.Where(w => w.Status == WishStatus.Kept && w.SlotId is not null).OrderBy(w => w.Id).ToList())
        {
            if (pass.SlotById(wish.SlotId!) is { } slot)
            {
                ReleaseKept(wish, slot);
                await save.OneAsync(wish, cancellationToken);
            }
        }

        // Step 4: wishes without a valid link, in creation order over every state, so the older of two contenders
        // wins a slot, also a hand entry against a younger redemption (AK58). An approved wish counts as unlinked
        // whatever its SlotId says (a re-add keeps the old one). An open wish released by step 3 is one of them.
        var unlinked = wishes
            .Where(w => w.Status == WishStatus.Pending
                || (w.Status == WishStatus.Approved && w.BotWriteAtUtc is null)
                || w.Status == WishStatus.RemovedExternally
                || (w.Status == WishStatus.Active && w.SlotId is null))
            .OrderBy(w => w.CreatedAtUtc)
            .ThenBy(w => w.Id)
            .ToList();
        foreach (var wish in unlinked)
        {
            pass.ReconcileUnlinked(wish);
            if (await save.OneAsync(wish, cancellationToken) && wish.Status == WishStatus.Pending)
            {
                // The link hit the running index (another wish of the emote committed meanwhile): the wish stays
                // open and says so, in a second save (plan #486 2.5).
                wish.StatusReason = WishStatusReason.AlreadyRunning;
                await save.OneAsync(wish, cancellationToken);
            }
        }

        // Step 4b: a link later in step 4 can change an earlier open wish's hint (an older wish whose slot is
        // excluded sees a younger one take it); the hints of the wishes still open are decided once more.
        foreach (var wish in unlinked.Where(w => w.Status == WishStatus.Pending))
        {
            pass.RefreshHint(wish);
            await save.OneAsync(wish, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return wishes.Any(w => before[w.Id] != Visible.Of(w)) ? channelName : null;
    }

    // Step 5: one wish, one save, behind a savepoint, with the audit entries the pass staged for it. A unique-index
    // hit (a running wish of the emote or a live link of the slot committed meanwhile without this run seeing it)
    // rolls back to the savepoint, puts the tracked row back to what the database holds and drops its audit
    // entries, so no later save retries them; the run goes on and OneAsync says true. Every other error
    // propagates and the transaction rolls back on dispose.
    private sealed class Saver(
        AppDbContext db, IDbContextTransaction transaction, string channelId, string channelName, Pass pass, DateTime now, ILogger logger)
    {
        public async Task<bool> OneAsync(Wish wish, CancellationToken cancellationToken)
        {
            var entry = db.Entry(wish);
            var audits = pass.TakeAudits(wish.Id);
            if (entry.State != EntityState.Modified)
            {
                return false;
            }

            var staged = new List<AuditLogEntry>();
            foreach (var (action, details) in audits)
            {
                staged.Add(db.AddAuditEntry(
                    AuditActor.System,
                    action,
                    channelName: channelName,
                    targetType: WishTargetType,
                    targetId: wish.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    details: details,
                    occurredAtUtc: now));
            }

            await transaction.CreateSavepointAsync(SavepointName, cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.ReleaseSavepointAsync(SavepointName, cancellationToken);
                return false;
            }
            catch (DbUpdateException ex) when (WishIndexOf(ex) is { } index)
            {
                await transaction.RollbackToSavepointAsync(SavepointName, cancellationToken);
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
                foreach (var audit in staged)
                {
                    db.Entry(audit).State = EntityState.Detached;
                }

                logger.LogInformation(
                    "Wish reconcile for channel {ChannelId}: wish {WishId} keeps its state; its change hit {IndexName}.",
                    channelId, wish.Id, index);
                return true;
            }
        }
    }

    private static string? WishIndexOf(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
            && postgres.ConstraintName is RunningEmoteIndex or LiveSlotIndex
            ? postgres.ConstraintName
            : null;

    private static bool IsLive(string status) => status is WishStatus.Active or WishStatus.Presented;

    // A kept wish holds its entry only while it is in the set (operator ruling 2026-11-18): once the slot
    // is archived, or carries a date after the wish's last confirmation and after its known generation (it
    // left and came back unseen), the stay is over and the slot is free again. A date equal to (or, as an
    // authoritative correction, before) the known generation is the same stay, even when the confirmation
    // was stamped before it. Only the link goes; state, dates and the generation stay as the record of the
    // stay. Affected carries the same predicate for the pre-check.
    private static void ReleaseKept(Wish wish, SlotRow slot)
    {
        if (slot.IsArchived
            || (slot.AddedAt is { } addedAt
                && wish.SlotConfirmedAtUtc is { } lastSeen
                && addedAt > lastSeen
                && (wish.SlotAddedAtUtc is not { } generation || addedAt > generation)))
        {
            wish.SlotId = null;
        }
    }

    // The decisions of one run over in-memory state. Tracked wishes change in place; the sets of running
    // emotes and live links are derived from them on every question, so a wish moved earlier in the run
    // (a return, a link, a revert after a savepoint) is seen by the next one.
    private sealed class Pass(
        List<Wish> wishes,
        List<OtherWish> others,
        List<SlotRow> slots,
        HashSet<(long WishId, string SlotId)> excluded,
        int pendingExpiryDays,
        DateTime now)
    {
        private readonly Dictionary<string, SlotRow> _slotsById = slots.ToDictionary(s => s.Id, StringComparer.Ordinal);
        private readonly Dictionary<long, List<(string Action, object Details)>> _audits = [];

        public SlotRow? SlotById(string slotId) => _slotsById.GetValueOrDefault(slotId);

        // The audit entries staged for a wish since its last save; the saver writes them behind the same savepoint.
        public List<(string Action, object Details)> TakeAudits(long wishId) =>
            _audits.Remove(wishId, out var staged) ? staged : [];

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
                // Generation not known yet (adoption of an undated slot). A date after the last confirmation
                // proves a stay the reconcile never saw begin (a missed archive hook, then a restore with a
                // provisional date): a generation change, removed at that confirmation, and step 4 of this run
                // decides on the return (operator ruling 2026-11-18). Any other date is a confirmation.
                if (wish.SlotConfirmedAtUtc is { } lastSeen && addedAt > lastSeen)
                {
                    RemoveExternally(wish, lastSeen);
                    return;
                }

                if (!ReleasedByDateCorrection(wish, slot))
                {
                    ConfirmDate(wish, slot, startFromSlot: wish.StartsAtUtc == wish.SlotConfirmedAtUtc);
                }

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
                // AK8: the provisional date is confirmed by an authoritative one not after it, unless it shows that
                // a capture link took an entry from before the redemption (plan #486 2.5).
                if (!ReleasedByDateCorrection(wish, slot))
                {
                    ConfirmDate(wish, slot, startFromSlot: wish.StartsAtUtc == generation);
                }

                return;
            }

            // Generation change: the slot was gone and is back without a reconcile having seen it. Removed at the
            // last confirmation, the earliest possible time (at the expense of the return); step 4 of this run
            // decides on the return.
            RemoveExternally(wish, wish.SlotConfirmedAtUtc ?? now);
        }

        // Step 4: the matcher decides, among admissible slots held by no other wish (running link or kept).
        public void ReconcileUnlinked(Wish wish)
        {
            switch (wish.Status)
            {
                case WishStatus.Pending:
                    ReconcilePending(wish);
                    break;

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

        // An open wish (K6, plan #486 2.5): a link by emote id or by name, else the hint, decided anew every run.
        // A hint changes neither StatusChangedAtUtc (the clock of the pending expiry) nor anything else.
        private void ReconcilePending(Wish wish)
        {
            var decision = DecidePending(wish);
            if (decision.SlotId is { } slotId)
            {
                Link(wish, _slotsById[slotId]);
                return;
            }

            wish.StatusReason = decision.Hint;
        }

        private WishPendingDecision DecidePending(Wish wish)
        {
            var visible = Admissible(wish).ToList();
            var linked = LinkedSlotIdsExcept(wish);
            var free = visible.Where(c => !linked.Contains(c.SlotId)).ToList();
            return WishSlotMatcher.DecidePending(
                InputOf(wish, WishStatus.Pending, wish.ApprovedAtUtc), free, visible, RunningCheckFor(wish));
        }

        // Step 4b: only the hint; a link waits for the next run (step 4 only takes slots, so none can appear here).
        public void RefreshHint(Wish wish)
        {
            if (DecidePending(wish) is { IsLink: false } decision)
            {
                wish.StatusReason = decision.Hint;
            }
        }

        private SlotRow? Match(Wish wish, string ruleStatus, DateTime? approvedAtUtc)
        {
            // A match by emote id needs an emote: the K6 placeholder of an open wish matches no slot this way.
            if (!wish.HasEmote)
            {
                return null;
            }

            var linked = LinkedSlotIdsExcept(wish);
            var candidates = Admissible(wish).Where(c => !linked.Contains(c.SlotId)).ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var result = WishSlotMatcher.Match(InputOf(wish, ruleStatus, approvedAtUtc), candidates);
            return result.SlotId is { } slotId ? _slotsById[slotId] : null;
        }

        // Not archived, settled, dated, and not excluded for this wish (K6, every path of the reconcile); of the
        // wish's emote when it has one, else every slot (a name wish; the matcher compares the alias).
        private IEnumerable<WishSlotCandidate> Admissible(Wish wish) =>
            slots
                .Where(s => (!wish.HasEmote || string.Equals(s.SevenTvEmoteId, wish.SevenTvEmoteId, StringComparison.Ordinal))
                    && !s.IsArchived
                    && !s.SeedUnsettled
                    && s.AddedAt is not null
                    && !excluded.Contains((wish.Id, s.Id)))
                .Select(s => new WishSlotCandidate(s.Id, s.SevenTvEmoteId, s.Name, s.AddedAt!.Value));

        private WishMatchInput InputOf(Wish wish, string ruleStatus, DateTime? approvedAtUtc) =>
            new(
                ruleStatus,
                wish.SevenTvEmoteId,
                wish.Alias,
                approvedAtUtc,
                wish.EndedAtUtc,
                wish.CreatedAtUtc,
                wish.MatchKind,
                wish.RequestedName,
                WishSlotMatcher.PendingWindowEndFor(wish.StatusChangedAtUtc, pendingExpiryDays),
                wish.FirstActivatedAtUtc,
                wish.Source);

        // pending or approved -> active (kept when permanent): start = AddedAt, expiry from start and duration,
        // alias follows the slot, LinkOrigin auto, never bot ownership, ApprovedAtUtc untouched. The placeholder of
        // an open wish is filled in the same update (plan #486 2.8a): the id from the slot for a name match, the
        // name from the slot's emote row, or the slot's name when that is empty. One wish.autoLink per link (W12).
        private void Link(Wish wish, SlotRow slot)
        {
            var target = WishTransitions.Target(
                wish.Status, wish.StatusReason, WishAction.Link, new WishTransitionContext(Permanent: wish.Permanent));
            if (target is null)
            {
                return;
            }

            var fromStatus = wish.Status;
            var start = slot.AddedAt!.Value;
            wish.Status = target;
            wish.StatusReason = null;
            wish.StatusChangedAtUtc = now;
            wish.EndedAtUtc = WishStatus.IsEnded(target) ? now : null;
            wish.SlotId = slot.Id;
            if (!wish.HasEmote)
            {
                wish.SevenTvEmoteId = slot.SevenTvEmoteId;
            }

            wish.Alias = slot.Name;
            if (wish.EmoteName.Length == 0)
            {
                wish.EmoteName = slot.EmoteName.Length > 0 ? slot.EmoteName : slot.Name;
            }

            wish.LinkOrigin = WishLinkOrigin.Auto;
            wish.StartsAtUtc = start;
            wish.ExpiresAtUtc = WishSchedule.ExpiresAt(start, wish.DurationDays);
            HoldGeneration(wish, slot);
            if (target == WishStatus.Active)
            {
                wish.FirstActivatedAtUtc ??= now;
            }

            Stage(wish, AuditActions.WishAutoLink, new
            {
                wishId = wish.Id,
                source = wish.Source,
                fromStatus,
                matchKind = wish.MatchKind,
            });
        }

        // Sol-P1-3 (plan #486 2.5): the authoritative date of the slot a capture linked without any click lies
        // before the redemption. Nobody decided yet (no extension booked, never approved): the link goes, spotless,
        // back to pending with already_in_set, before start, expiry and the flag would turn authoritative. No
        // exclusion row: the window rules the slot out anyway. Every other wish keeps the behaviour from before K6.
        private bool ReleasedByDateCorrection(Wish wish, SlotRow slot)
        {
            if (!slot.AddedAtFromRest
                || slot.AddedAt is not { } authoritative
                || authoritative >= wish.CreatedAtUtc
                || wish.Source != WishSource.Irc
                || wish.LinkOrigin != WishLinkOrigin.Auto
                || wish.ApprovedAtUtc is not null
                || wish.ExtensionCount > 0)
            {
                return false;
            }

            // active, or presented with unconfirmed_start; the table says no to every other state.
            var fromStatus = wish.Status;
            if (WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Unlink, new WishTransitionContext(IrcAutoLinked: true))
                is not WishStatus.Pending)
            {
                return false;
            }

            var slotId = wish.SlotId;
            WishUnlinkReset.ToPending(wish, now, WishStatusReason.AlreadyInSet);
            Stage(wish, AuditActions.WishUnlink, new { wishId = wish.Id, fromStatus, slotId, cause = DateCorrectionCause });
            return true;
        }

        private void Stage(Wish wish, string action, object details)
        {
            if (!_audits.TryGetValue(wish.Id, out var staged))
            {
                staged = [];
                _audits[wish.Id] = staged;
            }

            staged.Add((action, details));
        }

        // removed_externally -> the state before, with the old deadline and without ownership: presented with
        // the reason that stayed, else active. LinkOrigin stays (the link continues the same entry). A stand-in
        // start is the exception: it was never the date of any stay, so it takes the returned slot's date and
        // from then on follows the slot's authoritative date like a start taken from it (operator ruling
        // 2026-11-18).
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
            if (HasStandInStart(wish) && slot.AddedAt is { } returnedAt)
            {
                MoveStart(wish, returnedAt);
            }

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
            if (startFromSlot && slot.AddedAtFromRest && slot.AddedAt is { } newStart)
            {
                MoveStart(wish, newStart);
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

        private bool IsEmoteRunningElsewhere(Wish wish) => RunningCheckFor(wish)(wish.SevenTvEmoteId);

        // Whether a wish other than this one runs for a 7TV emote id (the running index's question).
        private Func<string, bool> RunningCheckFor(Wish wish) => emoteId =>
            emoteId.Length > 0
            && (wishes.Any(w => w.Id != wish.Id
                    && WishStatus.IsRunning(w.Status)
                    && string.Equals(w.SevenTvEmoteId, emoteId, StringComparison.Ordinal))
                || others.Any(o => WishStatus.IsRunning(o.Status)
                    && string.Equals(o.SevenTvEmoteId, emoteId, StringComparison.Ordinal)));

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

        // The start (and the expiry by the same difference) moves to newStart; a wish without a start stays.
        private static void MoveStart(Wish wish, DateTime newStart)
        {
            if (wish.StartsAtUtc is not { } oldStart)
            {
                return;
            }

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

        // The "now" an adoption of an undated slot stood in with, while the reconcile has not learned the
        // slot's generation yet (see the class remarks).
        private static bool HasStandInStart(Wish wish) =>
            wish.SlotAddedAtUtc is null && wish.StartsAtUtc is not null && wish.StartsAtUtc == wish.SlotConfirmedAtUtc;
    }

    private sealed record SlotRow(
        string Id,
        string SevenTvEmoteId,
        string Name,
        string EmoteName,
        DateTime? AddedAt,
        bool AddedAtFromRest,
        bool IsArchived,
        DateTime? ArchivedAt,
        bool SeedUnsettled);

    // A wish of the channel outside this run's locked set: a running one (unconfirmed, or one that became
    // running after the ids were read), or a kept one holding its slot.
    private sealed record OtherWish(string SevenTvEmoteId, string Status, string? SlotId);

    // What a client can see of a wish (the WishDto's stored fields). A change of these is what announces
    // wish.changed; a refresh of SlotConfirmedAtUtc or of the generation alone is not.
    private readonly record struct Visible(
        string Status,
        string? StatusReason,
        string SevenTvEmoteId,
        string EmoteName,
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
            w.Status, w.StatusReason, w.SevenTvEmoteId, w.EmoteName, w.Alias, w.StartsAtUtc, w.StartsAtAuthoritative, w.ExpiresAtUtc, w.EndedAtUtc,
            w.SlotId, w.LinkOrigin, w.BotOwned, w.RemovalQueuedAtUtc);
    }
}
