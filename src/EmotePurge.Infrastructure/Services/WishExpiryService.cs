using System.Globalization;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// One pass of the wish expiry run (spec K2, plan #484 2.1 to 2.4, T3). See <see cref="IWishExpiryService"/>.
/// <para>
/// <b>Flow.</b> (1) Reconcile every channel that has a wish the reconcile can move (plan finding 17), one after
/// the other, through <see cref="IWishReconcileService"/> in its own scope; a failure is counted and logged and
/// does not stop the due step for that channel (presenting is a reminder, and K4 re-reads before every bot
/// removal). (2) Read "now" once, then the candidates: <c>active</c>, expiry at or before now, no queued removal.
/// (3) Each candidate in a transaction of its own: lock the wish row, read it only then, check the selection and
/// the channel again, hold it back or decide (<see cref="WishExpiryPolicy"/>), write, audit, commit. (4) One
/// <c>wish.changed</c> per channel after its last commit.
/// </para>
/// <para>
/// <b>Locks (plan finding 12).</b> Only the one wish row, never the channel row and no advisory lock: the run
/// inserts nothing with a channel key and writes no <c>SlotId</c>, holds at most one wish row and asks for no
/// further lock behind it, so no cycle with the reconcile, the channel merge or a purge is possible. Two runs at
/// once (two worker containers during a stack update) meet at the row lock: the second reads the committed state,
/// no longer selects the wish and skips it.
/// </para>
/// <para>
/// <b>One <c>wish.due</c> per expiry (plan finding 11).</b> When something other than an extension dropped a
/// queued removal (ownership ended by the reconcile, a threshold change), the wish is due again for the same
/// expiry and is decided again, but behind the row lock the run looks for its earlier entry for that expiry and
/// writes no second one. Only a new expiry allows the next entry.
/// </para>
/// </summary>
public sealed class WishExpiryService(
    AppDbContext db,
    IWishReconcileService reconcileService,
    IWishBotAvailability botAvailability,
    IWishChangedPublisher changedPublisher,
    TimeProvider timeProvider,
    ILogger<WishExpiryService> logger) : IWishExpiryService
{
    private const string WishTargetType = "wish";

    // The states the reconcile can move (plan finding 17): the running ones it compares with their slot, a
    // returner, and a kept wish that still holds a slot.
    private static readonly string[] ReconciledStatuses =
        [WishStatus.Approved, WishStatus.Active, WishStatus.Presented, WishStatus.RemovedExternally];

    public async Task<WishExpiryRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var tally = new Tally();

        // Step 1. Ids only and untracked: the reconcile writes these channels' wishes in a context of its own.
        foreach (var channelId in await ReconcileChannelIdsAsync(cancellationToken))
        {
            try
            {
                await reconcileService.ReconcileChannelAsync(channelId, cancellationToken);
                tally.ReconciledChannels++;
            }
            catch (Exception ex) when (!IsStop(ex, cancellationToken))
            {
                tally.FailedReconciles++;
                logger.LogWarning(
                    "Wish expiry run: the reconcile of channel {ChannelId} failed ({ExceptionType}, SQLSTATE {SqlState}); its due wishes are decided anyway.",
                    channelId, ex.GetType().Name, SqlStateOf(ex));
            }
        }

        // Step 2. One instant for the whole pass, read after the reconcile: the selection and the usage window's
        // evaluation time.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var candidates = await db.Wishes.AsNoTracking()
            .Where(w => w.Status == WishStatus.Active && w.ExpiresAtUtc != null && w.ExpiresAtUtc <= now && w.RemovalQueuedAtUtc == null)
            .OrderBy(w => w.ChannelId)
            .ThenBy(w => w.Id)
            .Select(w => new Candidate(w.Id, w.ChannelId))
            .ToListAsync(cancellationToken);

        // Steps 3 and 4. Candidates come grouped by channel; the event follows the group's last commit.
        string? groupChannelId = null;
        string? announce = null;
        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.ChannelId, groupChannelId, StringComparison.Ordinal))
            {
                await AnnounceAsync(announce, cancellationToken);
                announce = null;
                groupChannelId = candidate.ChannelId;
            }

            var changedChannel = await DecideOneAsync(candidate, now, tally, cancellationToken);
            announce ??= changedChannel;
        }

        await AnnounceAsync(announce, cancellationToken);
        return tally.ToResult();
    }

    private Task<List<string>> ReconcileChannelIdsAsync(CancellationToken cancellationToken)
    {
        var reconciled = ReconciledStatuses;
        return db.Wishes.AsNoTracking()
            .Where(w => reconciled.Contains(w.Status) || (w.Status == WishStatus.Kept && w.SlotId != null))
            .Select(w => w.ChannelId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
    }

    // One candidate in one transaction (contract 2.3). Returns the channel name to announce when the wish changed,
    // else null. A failure rolls back this wish alone, is counted and logged, and the run goes on.
    private async Task<string?> DecideOneAsync(Candidate candidate, DateTime now, Tally tally, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.LockWishesByIdsAsync([candidate.WishId], cancellationToken);
            var wish = await db.Wishes.FirstOrDefaultAsync(w => w.Id == candidate.WishId, cancellationToken);

            // Extended, kept, removed, queued or moved to another channel since the candidates were read: the
            // committed state decides, and the next run finds whatever is due then.
            if (wish is null
                || !string.Equals(wish.ChannelId, candidate.ChannelId, StringComparison.Ordinal)
                || !string.Equals(wish.Status, WishStatus.Active, StringComparison.Ordinal)
                || wish.ExpiresAtUtc is not { } expiresAt
                || expiresAt > now
                || wish.RemovalQueuedAtUtc is not null)
            {
                tally.Skipped++;
                return null;
            }

            var channel = await db.Channels.AsNoTracking()
                .Where(c => c.Id == wish.ChannelId)
                .Select(c => new { c.ChannelName, c.LastSyncedAtUtc })
                .FirstOrDefaultAsync(cancellationToken);
            if (channel is null)
            {
                tally.Skipped++;
                return null;
            }

            if (WishExpiryPolicy.IsHeldBack(wish.StartsAtAuthoritative, expiresAt, channel.LastSyncedAtUtc, now))
            {
                tally.HeldBack++;
                return null;
            }

            var decision = await DecideAsync(wish, now, tally, cancellationToken);
            if (decision is not { } decided)
            {
                tally.Skipped++;
                return null;
            }

            if (decided.Queue)
            {
                wish.RemovalQueuedAtUtc = now;
                wish.RemovalQueuedVersion = wish.DecisionVersion;
            }
            else
            {
                // Expiry, end, decision version, slot and ownership stay: a presented wish can still be extended.
                wish.Status = WishTransitions.Target(
                        wish.Status,
                        wish.StatusReason,
                        WishAction.Present,
                        new WishTransitionContext(HasSlot: wish.SlotId is not null, Permanent: wish.Permanent, HasExpiry: true))
                    ?? throw new InvalidOperationException("The transition table does not allow presenting an active wish.");
                wish.StatusReason = decided.Reason;
                wish.StatusChangedAtUtc = now;
            }

            if (!await HasDueEntryAsync(wish.Id, expiresAt, cancellationToken))
            {
                db.AddAuditEntry(
                    AuditActor.System,
                    AuditActions.WishDue,
                    channelName: channel.ChannelName,
                    targetType: WishTargetType,
                    targetId: wish.Id.ToString(CultureInfo.InvariantCulture),
                    details: new { wishId = wish.Id, outcome = decided.Outcome, expiresAtUtc = expiresAt },
                    occurredAtUtc: now);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            tally.Count(decided);
            return channel.ChannelName;
        }
        catch (Exception ex) when (!IsStop(ex, cancellationToken))
        {
            tally.Failed++;
            logger.LogWarning(
                "Wish expiry run: wish {WishId} failed ({ExceptionType}, SQLSTATE {SqlState}); the next run retries it.",
                candidate.WishId, ex.GetType().Name, SqlStateOf(ex));
            return null;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    // The facts behind the row lock (plan 2.2). The global availability is asked at most once per pass and only for
    // a wish past rows 1 to 3; the usage only when the policy needs it (plan finding 16). Null: the channel row is
    // gone (a purge or merge at the same moment).
    private async Task<WishExpiryDecision?> DecideAsync(Wish wish, DateTime now, Tally tally, CancellationToken cancellationToken)
    {
        var settings = await db.ChannelWishSettings.AsNoTracking()
            .Where(s => s.ChannelId == wish.ChannelId)
            .Select(s => new { s.BotModeEnabled, s.BotSuspendedReason, s.ProtectionThresholdPerDay })
            .FirstOrDefaultAsync(cancellationToken);
        var botModeEnabled = settings?.BotModeEnabled ?? false;

        var available = false;
        if (botModeEnabled && wish.BotOwned && wish.StartsAtAuthoritative)
        {
            tally.BotAvailable ??= await botAvailability.IsAvailableAsync(cancellationToken);
            available = tally.BotAvailable.Value;
        }

        var facts = new WishExpiryFacts(
            botModeEnabled,
            settings?.BotSuspendedReason,
            wish.BotOwned,
            available,
            wish.StartsAtAuthoritative,
            settings?.ProtectionThresholdPerDay);
        if (!WishExpiryPolicy.NeedsUsage(facts))
        {
            return WishExpiryPolicy.Decide(facts);
        }

        if (wish.StartsAtUtc is not { } startsAt)
        {
            // An active wish always has a start; without one there is no window to judge, which reads like a gap
            // and presents instead of removing (the safe direction).
            return WishExpiryPolicy.Decide(facts with { MeanPerDay = 0m, HasCoverageGap = true });
        }

        var usage = await db.ReadAsync(wish.ChannelId, wish.SevenTvEmoteId, startsAt, now, cancellationToken);
        return usage is null
            ? null
            : WishExpiryPolicy.Decide(facts with { MeanPerDay = usage.MeanPerDay, HasCoverageGap = usage.HasCoverageGap });
    }

    // Behind the row lock: is there a wish.due of this wish for this expiry already? An entry for it cannot be older
    // than the expiry (the run stamps its entries with its own "now", which is never before the expiry), so the
    // OccurredAtUtc index bounds the search. Compared as instants, not as text.
    private async Task<bool> HasDueEntryAsync(long wishId, DateTime expiresAt, CancellationToken cancellationToken)
    {
        var targetId = wishId.ToString(CultureInfo.InvariantCulture);
        var details = await db.AuditLogEntries.AsNoTracking()
            .Where(a => a.OccurredAtUtc >= expiresAt
                && a.Action == AuditActions.WishDue
                && a.TargetType == WishTargetType
                && a.TargetId == targetId)
            .Select(a => a.DetailsJson)
            .ToListAsync(cancellationToken);
        return details.Any(d => RecordedExpiry(d) == expiresAt);
    }

    private async Task AnnounceAsync(string? channelName, CancellationToken cancellationToken)
    {
        if (channelName is not null)
        {
            await changedPublisher.PublishAsync(channelName, cancellationToken);
        }
    }

    private static DateTime? RecordedExpiry(string? detailsJson)
    {
        if (detailsJson is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(detailsJson);
        return document.RootElement.TryGetProperty("expiresAtUtc", out var value) && value.TryGetDateTime(out var recorded)
            ? recorded.ToUniversalTime()
            : null;
    }

    private static bool IsStop(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static string? SqlStateOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.SqlState;
            }
        }

        return null;
    }

    private sealed record Candidate(long WishId, string ChannelId);

    // The counters of one pass and its memo of the global bot availability.
    private sealed class Tally
    {
        private readonly Dictionary<string, int> _presentedByReason = new(StringComparer.Ordinal);

        public int ReconciledChannels { get; set; }
        public int FailedReconciles { get; set; }
        public int Queued { get; private set; }
        public int HeldBack { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public bool? BotAvailable { get; set; }

        public void Count(WishExpiryDecision decision)
        {
            if (decision.Queue)
            {
                Queued++;
                return;
            }

            _presentedByReason[decision.Reason!] = _presentedByReason.GetValueOrDefault(decision.Reason!) + 1;
        }

        public WishExpiryRunResult ToResult() =>
            new(ReconciledChannels, FailedReconciles, new Dictionary<string, int>(_presentedByReason, StringComparer.Ordinal), Queued, HeldBack, Skipped, Failed);
    }
}
