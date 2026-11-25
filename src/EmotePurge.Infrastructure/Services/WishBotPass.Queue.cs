using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

// Step 5 of the bot pass, the derived queue (#487 plan 2.5 "Warteschlange", K6b-14, K6b-17; T9 the frame, adds and
// confirmations; T10 docks repair checks, removal attempts and queued removals onto it).
//
// The frame: one selection without a lock (SelectQueueAsync, one selector per kind), one pure order
// (WishBotQueue.Order: repair checks, open removal attempts, then the rest together, oldest first, then id), one loop that
// dispatches item by item (RunQueueItemAsync) until the queue is empty, the budget is used up (a handler's TryTakeAction
// said no) or the lock or the epoch is lost. A handler takes its action from the budget itself, right before its first
// 7TV call or intent (after its cheap checks, so a skipped item costs nothing), and waits for the write slot itself
// before a write (WaitForWriteSlotAsync, never under a row lock). A row that is gone or changed after the selection is
// nothing to do. T10 adds a selector to SelectQueueAsync and a case to RunQueueItemAsync per kind; neither the loop nor
// the order change.
//
// The shared lock path of every wish booking of the queue (LockWishRowsAsync, plan 2.5a (c)): channel row FOR SHARE (the
// first lock, so the deadline starts there), the wish row FOR UPDATE within that channel, then the credential row FOR
// SHARE with the runner epoch (the fence), each statement under the BotAccountLockDeadline (K6e-3). BookWishAsync wraps it
// for the short bookings without a 7TV call (2.5a (c) last sentence): decide on the locked rows, join a reported pause
// (2.5a (i), K6c-3), commit, one wish.changed.
public sealed partial class WishBotPass
{
    // A proof older than this before an intent is refreshed by a new editor read (plan 2.5 preflight: at most 1 minute at
    // the write). The margin covers what lies between the intent and the send transaction's re-check: the pre-read (one
    // page, at most 10 s) and the first lock wait (at most 10 s).
    private static readonly TimeSpan EditorProofRefreshAge = WishBotPreflight.EditorProofFreshness - TimeSpan.FromSeconds(30);

    // False: the lock or the epoch was lost, or the credential changed under a send (the pass ends).
    private async Task<bool> RunQueueAsync(
        IBotAccountSession session, BotAccountAvailabilityState account, CancellationToken cancellationToken)
    {
        var items = await SelectQueueAsync(account, cancellationToken);
        _stuckRemovals.Retain(session.RunnerEpoch, items.Where(i => i.Kind == WishBotQueueKind.Removal).Select(i => i.WishId));
        if (items.Count == 0)
        {
            return true;
        }

        var state = new QueueState(account);
        foreach (var item in WishBotQueue.Order(items))
        {
            var step = await RunQueueItemAsync(session, state, item, cancellationToken);
            if (step == QueueStep.Stop)
            {
                return false;
            }

            if (step == QueueStep.Exhausted)
            {
                logger.LogInformation("Wish bot: the action budget of this pass is used up; the rest of the queue waits for the next pass.");
                break;
            }
        }

        return true;
    }

    private Task<QueueStep> RunQueueItemAsync(
        IBotAccountSession session, QueueState state, WishBotQueueItem item, CancellationToken cancellationToken) => item.Kind switch
        {
            WishBotQueueKind.RepairCheck => RunRepairCheckAsync(session, item, cancellationToken),
            WishBotQueueKind.RemovalAttempt => RunRemovalAttemptAsync(session, item, cancellationToken),
            WishBotQueueKind.Add => RunAddAsync(session, state, item, cancellationToken),
            WishBotQueueKind.Confirm => RunConfirmAsync(session, item.ChannelId, item.WishId, cancellationToken),
            WishBotQueueKind.Removal => RunRemovalAsync(session, state, item, cancellationToken),
            _ => throw new InvalidOperationException($"No handler for queue items of kind {item.Kind}."),
        };

    // Every selector reads without a lock: the adds' (T9) in bot channels; the removals' (T10) take the repair checks in
    // any channel state and the removal attempts and queued removals in bot channels.
    private async Task<List<WishBotQueueItem>> SelectQueueAsync(BotAccountAvailabilityState account, CancellationToken cancellationToken)
    {
        var botChannels = await BotChannelIdsAsync(account, cancellationToken);
        var items = new List<WishBotQueueItem>();
        if (botChannels.Count > 0)
        {
            items.AddRange(await SelectAddWorkAsync(botChannels, cancellationToken));
        }

        items.AddRange(await SelectRemovalWorkAsync(botChannels, cancellationToken));
        return items;
    }

    // The channels whose effective state is bot (plan 2.3) and which have an active set to write to (an empty id: none yet).
    private async Task<List<string>> BotChannelIdsAsync(BotAccountAvailabilityState account, CancellationToken cancellationToken)
    {
        if (!account.IsAvailable)
        {
            return [];
        }

        var channels = await db.ChannelWishSettings.AsNoTracking()
            .Where(s => s.BotModeEnabled && s.Channel!.ActiveEmoteSetId != "")
            .OrderBy(s => s.ChannelId)
            .Select(s => new { Settings = s, s.Channel!.TwitchChannelId })
            .ToListAsync(cancellationToken);
        var now = Now();
        return channels
            .Where(c => WishBotChannelState.Of(
                    c.Settings,
                    account,
                    wishOptions.PilotList.Covers(c.TwitchChannelId),
                    WishBotChannelStateReader.ActivatorIsAdminById(admins, c.Settings),
                    now)
                == WishBotChannelMode.Bot)
            .Select(c => c.Settings.ChannelId)
            .ToList();
    }

    // E8 for a bot channel: the pilot list, or an activator who is a global admin by id.
    private bool CoveredForBot(ChannelWishSettings settings, string? twitchChannelId) =>
        wishOptions.PilotList.Covers(twitchChannelId) || WishBotChannelStateReader.ActivatorIsAdminById(admins, settings);

    // The proof the preflight of an intent needs (plan 2.5: at most 1 minute old at the write; T8b: a long queue outlives
    // the read of step 3). A proof older than EditorProofRefreshAge is refreshed by a read that began after that age
    // (IBotAccountEditorCheck reuses a younger one), booked like step 3 does (a negative read suspends the channel). An
    // unavailable read is not repeated within the pass.
    private async Task<ProofStep> EnsureEditorProofAsync(
        IBotAccountSession session, QueueState state, string channelId, CancellationToken cancellationToken)
    {
        var account = state.Account;
        if (account.BotSevenTvUserId is not { } botId)
        {
            return ProofStep.Unproven;
        }

        if (ProofFresh(await SettingsOfAsync(channelId, cancellationToken), account))
        {
            return ProofStep.Fresh;
        }

        if (state.EditorReadUnavailable)
        {
            return ProofStep.Unproven;
        }

        if (!await session.HoldsLockAsync(cancellationToken))
        {
            return ProofStep.Stop;
        }

        var read = await editorCheck.ReadAsync(botId, account.IdentityVersion, Now() - EditorProofRefreshAge, cancellationToken);
        if (!await session.HoldsLockAsync(cancellationToken))
        {
            return ProofStep.Stop;
        }

        if (!read.IsAvailable)
        {
            logger.LogInformation("Wish bot: the editor read for a stale proof is unavailable; adds wait for the next pass.");
            state.EditorReadUnavailable = true;
            return ProofStep.Unproven;
        }

        if (await BookChannelAsync(session, account, channelId, read, cancellationToken) == Booking.Fenced)
        {
            session.ReportLockLost();
            return ProofStep.Stop;
        }

        return ProofFresh(await SettingsOfAsync(channelId, cancellationToken), account) ? ProofStep.Fresh : ProofStep.Unproven;
    }

    private bool ProofFresh(ChannelWishSettings? settings, BotAccountAvailabilityState account) =>
        settings is { BotSuspendedReason: null, BotEditorCheckedAtUtc: { } checkedAt }
        && settings.BotEditorCheckedIdentityVersion == account.IdentityVersion
        && Now() - checkedAt <= EditorProofRefreshAge;

    private Task<ChannelWishSettings?> SettingsOfAsync(string channelId, CancellationToken cancellationToken) =>
        db.ChannelWishSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);

    // The account stages of a preflight before an authenticated read without a channel (a repair check) or under a lock
    // that does not hold the credential row (transaction 1 of a removal): the credential row as it is now, one statement
    // without a lock (AK81).
    private async Task<BotAccountSnapshot> FreshAccountAsync(IBotAccountSession session, CancellationToken cancellationToken) =>
        FreshAccount(
            await db.BotAccountCredentials.AsNoTracking()
                .Where(c => c.Id == BotAccountCredential.SingletonId)
                .Select(c => new BotAccountSnapshot(
                    c.Status, c.ExpiresAtUtc, c.BotSevenTvUserId, c.KillSwitch, c.PausedUntilUtc, c.CredentialVersion, c.IdentityVersion))
                .FirstOrDefaultAsync(cancellationToken),
            session.Account);

    // ------------------------------------------------------------------------------------ the shared lock path

    // Channel FOR SHARE (first lock, the deadline starts) and the channel's rows behind it, the wish FOR UPDATE within that
    // channel, the fence, the wish (wish and settings tracked). Missed: the channel or the wish is gone (K6b-17).
    private async Task<(Booking Booking, LockedWish? Locked)> LockWishRowsAsync(
        IBotAccountSession session, string channelId, long wishId, BotAccountLockDeadline deadline, CancellationToken cancellationToken)
    {
        await deadline.ApplyAsync(db, cancellationToken);
        if ((await db.LockChannelsForShareByIdsAsync([channelId], cancellationToken)).Count == 0)
        {
            return (Booking.Missed, null);
        }

        // The channel's rows first: every writer of them waits for the channel row this transaction holds.
        deadline.Start();
        await deadline.ApplyAsync(db, cancellationToken);
        var settings = await db.ChannelWishSettings.FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
        await deadline.ApplyAsync(db, cancellationToken);
        var channel = await db.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new { c.ChannelName, c.TwitchChannelId, c.ActiveEmoteSetId })
            .FirstAsync(cancellationToken);
        await deadline.ApplyAsync(db, cancellationToken);
        if ((await db.LockChannelWishesByIdsAsync(channelId, [wishId], cancellationToken)).Count == 0)
        {
            return (Booking.Missed, null);
        }

        await deadline.ApplyAsync(db, cancellationToken);
        var row = await db.LockBotAccountForShareAsync(session.RunnerEpoch, cancellationToken);
        if (row is null)
        {
            logger.LogWarning("Bot account: runner epoch {Epoch} was superseded; this pass books nothing more.", session.RunnerEpoch);
            return (Booking.Fenced, null);
        }

        await deadline.ApplyAsync(db, cancellationToken);
        var wish = await db.Wishes.FirstAsync(w => w.Id == wishId, cancellationToken);
        return (Booking.Hit, new LockedWish(wish, settings, channel.ChannelName, channel.TwitchChannelId, channel.ActiveEmoteSetId, row));
    }

    // A short booking without a 7TV call (plan 2.5a (c), (f)): apply decides on the locked rows and changes the tracked wish
    // (and settings), true when it booked something. A reported pause joins the transaction (K6c-3) and is committed with
    // it even when apply books nothing; where the transaction does not get that far, the pause is reported on its own
    // afterwards, so it is never lost and the token rests for the rest of the pass. One wish.changed after a hit.
    private Task<Booking> BookWishAsync(
        IBotAccountSession session,
        string channelId,
        long wishId,
        TimeSpan? pause,
        Func<LockedWish, DateTime, bool> apply,
        CancellationToken cancellationToken) =>
        BookWishAsync(session, channelId, wishId, pause, (locked, now, _) => Task.FromResult(apply(locked, now)), cancellationToken);

    // The same with a decision that reads behind the locks itself (T10: the local slots before the removal's booking); it
    // applies the deadline before each of its statements.
    private async Task<Booking> BookWishAsync(
        IBotAccountSession session,
        string channelId,
        long wishId,
        TimeSpan? pause,
        Func<LockedWish, DateTime, BotAccountLockDeadline, Task<bool>> apply,
        CancellationToken cancellationToken)
    {
        var booking = Booking.Missed;
        string? announce = null;
        var unreportedPause = pause;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deadline = new BotAccountLockDeadline(timeProvider);
            var (locking, locked) = await LockWishRowsAsync(session, channelId, wishId, deadline, cancellationToken);
            booking = locking;
            if (locking == Booking.Fenced)
            {
                session.ReportLockLost();
            }
            else if (locking == Booking.Hit)
            {
                var hit = await apply(locked!, Now(), deadline);
                booking = hit ? Booking.Hit : Booking.Missed;
                if (hit || unreportedPause is not null)
                {
                    if (unreportedPause is { } retryAfter)
                    {
                        await deadline.ApplyAsync(db, cancellationToken);
                        unreportedPause = null;
                        await session.PauseAsync(retryAfter, cancellationToken);
                    }

                    await deadline.ApplyAsync(db, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    await deadline.ApplyAsync(db, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    announce = hit ? locked!.ChannelName : null;
                }
            }
        }
        catch (Exception ex) when (BotAccountLockDeadline.IsExpiry(ex, cancellationToken))
        {
            logger.LogWarning(
                "Wish bot: a booking for wish {WishId} ran out of its lock deadline or failed ({ExceptionType}); nothing was booked.",
                wishId, ex.GetType().Name);
            booking = Booking.Expired;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        if (unreportedPause is { } pending)
        {
            // No transaction open any more: the session books it in a fenced transaction of its own.
            await session.PauseAsync(pending, cancellationToken);
        }

        if (announce is not null)
        {
            await changedPublisher.PublishAsync(announce, cancellationToken);
        }

        return booking;
    }

    // A fresh row with the session's own pause joined: a pause the session booked in a transaction that rolled back
    // still holds for the pass (K6c-3). No row: none.
    private static BotAccountSnapshot FreshAccount(BotAccountSnapshot? row, BotAccountSnapshot session)
    {
        if (row is null)
        {
            return BotAccountSnapshot.Missing;
        }

        return session.PausedUntilUtc is { } held && (row.PausedUntilUtc is not { } stored || stored < held)
            ? row with { PausedUntilUtc = held }
            : row;
    }

    private static BotAccountSnapshot SnapshotOf(BotAccountCredential row) =>
        new(row.Status, row.ExpiresAtUtc, row.BotSevenTvUserId, row.KillSwitch, row.PausedUntilUtc, row.CredentialVersion, row.IdentityVersion);

    private enum QueueStep
    {
        Continue,

        // The lock or the epoch is lost, or the credential changed under a send: the pass ends.
        Stop,

        // The action budget is used up: the rest of the queue waits for the next pass.
        Exhausted,
    }

    private enum ProofStep
    {
        Fresh,
        Unproven,
        Stop,
    }

    // The wish and its channel behind the locks of LockWishRowsAsync; Wish and Settings are tracked, Account is the fence's.
    private sealed record LockedWish(
        Wish Wish,
        ChannelWishSettings? Settings,
        string ChannelName,
        string? TwitchChannelId,
        string? ActiveEmoteSetId,
        BotAccountCredential Account);

    // What the queue of one pass remembers across its items.
    private sealed class QueueState(BotAccountAvailabilityState account)
    {
        public BotAccountAvailabilityState Account { get; } = account;

        // An editor read for a stale proof came back unavailable: not repeated within the pass.
        public bool EditorReadUnavailable { get; set; }
    }
}
