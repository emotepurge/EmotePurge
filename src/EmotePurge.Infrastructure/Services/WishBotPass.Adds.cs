using System.Globalization;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

// The adds and confirmations of the queue (#487 plan 2.6 with 2.5a; T9).
//
// An add, in this order: the preflight without the editor stage, on the credential row as it is now (nothing is taken
// from the budget for an add that could not be sent anyway); TryTakeAction; WaitForWriteSlotAsync (before the intent,
// never under a row lock, K6c-4); the editor proof, refreshed by a new read where it is older than EditorProofRefreshAge (a long queue outlives
// step 3's read); the full preflight; the lock probe; (1) the intent in a committed transaction of its own (2.5a (a):
// approved, no BotWriteAtUtc, the decision version just read, one add per set and emote at a time); the probe; (2) the
// pre-read of the attempt's set without a row lock (the emote already there → presented already_in_set; no read → the
// intent is taken back); the probe; (3) the send transaction (2.5a (c)): channel FOR SHARE, wish FOR UPDATE, the fence,
// the re-check (intent unchanged, bot mode on, active set = BotSetId, the full preflight on the locked credential row,
// the HTTP budget of the lock deadline), MarkWriteSent, exactly one addEmote, its booking under the same locks, the
// commit; the probe after. A "no" before sending takes the never-sent intent back in a short transaction of its own; a
// lost epoch or a failed take-back leaves it to the crash path (2.5a (e)). After Ok the answer's entries stand in for
// the read at 0 s (AK89): a hit books bot_write and bot_confirm in the same commit.
//
// The crash path (2.5a (e)): an approved wish found with BotWriteAtUtc (not this pass's) becomes unconfirmed without
// BotAddAckAtUtc, so it is confirmed at most without ownership; it is never added again.
//
// A confirmation (2.6 points 5 and 6): when BotNextAttemptAtUtc is due, the preflight without the editor stage, one action,
// the read of BotSetId, the booking in a short transaction under the same locks: a match of this attempt's generation in
// the still active set → bot_confirm (ownership only with BotAddAckAtUtc and the bot's addedById); anything else counts
// as a read and plans the next by WishBotConfirmSchedule. The deadline step (WishBotPass.Deadlines.cs) presents what is
// left after 16 minutes.
//
// Every result of a call with the token reports to the session (2.5a (i)): Unauthorized → ReportUnauthorized, any
// RetryAfter (RateLimited, or an Ok with a rate limit below it, K6e-4) → PauseAsync, joined to the transaction that books
// the call's outcome (K6c-3) or, where that transaction does not get to its commit, reported on its own afterwards.
public sealed partial class WishBotPass
{
    // The selector of T9 (plan 2.5 "Warteschlange"): approved (a crash remnant with BotWriteAtUtc included), ordered by the
    // approval, and unconfirmed with a due read, ordered by the last write; both only in bot channels.
    private async Task<List<WishBotQueueItem>> SelectAddWorkAsync(IReadOnlyCollection<string> botChannels, CancellationToken cancellationToken)
    {
        var now = Now();
        var rows = await db.Wishes.AsNoTracking()
            .Where(w => botChannels.Contains(w.ChannelId)
                && w.SevenTvEmoteId != ""
                && (w.Status == WishStatus.Approved || (w.Status == WishStatus.Unconfirmed && w.BotNextAttemptAtUtc <= now)))
            .Select(w => new { w.Id, w.ChannelId, w.Status, w.ApprovedAtUtc, w.BotWriteAtUtc, w.StatusChangedAtUtc })
            .ToListAsync(cancellationToken);
        return rows
            .Select(w => w.Status == WishStatus.Approved
                ? new WishBotQueueItem(WishBotQueueKind.Add, w.Id, w.ChannelId, w.ApprovedAtUtc ?? w.StatusChangedAtUtc)
                : new WishBotQueueItem(WishBotQueueKind.Confirm, w.Id, w.ChannelId, w.BotWriteAtUtc ?? w.StatusChangedAtUtc))
            .ToList();
    }

    private async Task<QueueStep> RunAddAsync(
        IBotAccountSession session, QueueState state, WishBotQueueItem item, CancellationToken cancellationToken)
    {
        var wish = await db.Wishes.AsNoTracking().FirstOrDefaultAsync(w => w.Id == item.WishId && w.ChannelId == item.ChannelId, cancellationToken);
        if (wish is null || !string.Equals(wish.Status, WishStatus.Approved, StringComparison.Ordinal) || !wish.HasEmote)
        {
            return QueueStep.Continue;
        }

        if (wish.BotWriteAtUtc is not null)
        {
            return await ResumeCrashedAddAsync(session, wish, cancellationToken);
        }

        if (await PreflightAsync(session, item.ChannelId, requireEditorProof: false, cancellationToken) is { } early)
        {
            return early;
        }

        if (!session.TryTakeAction())
        {
            return QueueStep.Exhausted;
        }

        await session.WaitForWriteSlotAsync(cancellationToken);
        switch (await EnsureEditorProofAsync(session, state, item.ChannelId, cancellationToken))
        {
            case ProofStep.Stop:
                return QueueStep.Stop;
            case ProofStep.Unproven:
                return QueueStep.Continue;
        }

        if (await PreflightAsync(session, item.ChannelId, requireEditorProof: true, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (!await session.HoldsLockAsync(cancellationToken))
        {
            return QueueStep.Stop;
        }

        var (booking, intent) = await CommitIntentAsync(session, item, wish.DecisionVersion, cancellationToken);
        if (booking == Booking.Fenced)
        {
            return QueueStep.Stop;
        }

        if (intent is null)
        {
            return QueueStep.Continue;
        }

        // From here the intent is durable: a crash or a lost lock leaves it to the crash path, it is never sent blind.
        if (!await session.HoldsLockAsync(cancellationToken))
        {
            return QueueStep.Stop;
        }

        var preRead = await PreReadAsync(session, item, intent, cancellationToken);
        if (preRead != QueueStep.Continue || !intent.ReadyToSend)
        {
            return preRead;
        }

        return await SendAddAsync(session, item, intent, cancellationToken);
    }

    // The preflight before an intent or an authenticated read, no lock: the channel's settings and the credential row in
    // one statement, so the kill switch is the database's of this moment (AK81, plan 2.5), not the session's of the pass
    // start. Null to go on, otherwise what the queue does instead (the credential changed: the pass ends; any other "no":
    // the item waits; plan 2.5).
    private async Task<QueueStep?> PreflightAsync(
        IBotAccountSession session, string channelId, bool requireEditorProof, CancellationToken cancellationToken)
    {
        var facts = await (
                from s in db.ChannelWishSettings.AsNoTracking().Where(s => s.ChannelId == channelId)
                from c in db.BotAccountCredentials.AsNoTracking().Where(c => c.Id == BotAccountCredential.SingletonId).DefaultIfEmpty()
                select new
                {
                    Settings = s,
                    s.Channel!.TwitchChannelId,
                    HasAccountRow = c != null,
                    Status = c != null ? c.Status : null,
                    ExpiresAtUtc = c != null ? c.ExpiresAtUtc : null,
                    BotSevenTvUserId = c != null ? c.BotSevenTvUserId : null,
                    KillSwitch = c != null && c.KillSwitch,
                    PausedUntilUtc = c != null ? c.PausedUntilUtc : null,
                    CredentialVersion = c != null ? c.CredentialVersion : 0,
                    IdentityVersion = c != null ? c.IdentityVersion : 0,
                })
            .FirstOrDefaultAsync(cancellationToken);
        if (facts is not { Settings.BotModeEnabled: true } || session.Token is null)
        {
            return QueueStep.Continue;
        }

        var account = FreshAccount(
            facts.HasAccountRow
                ? new BotAccountSnapshot(
                    facts.Status!, facts.ExpiresAtUtc, facts.BotSevenTvUserId, facts.KillSwitch, facts.PausedUntilUtc, facts.CredentialVersion, facts.IdentityVersion)
                : null,
            session.Account);
        var outcome = WishBotPreflight.Check(
            SwitchesOn,
            CoveredForBot(facts.Settings, facts.TwitchChannelId),
            facts.Settings,
            account,
            session.Account.CredentialVersion,
            session.TokenUsable,
            Now(),
            requireEditorProof);
        return outcome switch
        {
            WishBotPreflightOutcome.Pass => null,
            WishBotPreflightOutcome.CredentialChanged => QueueStep.Stop,
            _ => QueueStep.Continue,
        };
    }

    // ------------------------------------------------------------------------------------ (1) the intent

    private async Task<(Booking Booking, AddIntent? Intent)> CommitIntentAsync(
        IBotAccountSession session, WishBotQueueItem item, int decisionVersion, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deadline = new BotAccountLockDeadline(timeProvider);
            var (locking, locked) = await LockWishRowsAsync(session, item.ChannelId, item.WishId, deadline, cancellationToken);
            if (locking == Booking.Fenced)
            {
                session.ReportLockLost();
                return (Booking.Fenced, null);
            }

            if (locking != Booking.Hit)
            {
                return (locking, null);
            }

            var wish = locked!.Wish;
            if (!string.Equals(wish.Status, WishStatus.Approved, StringComparison.Ordinal)
                || wish.BotWriteAtUtc is not null
                || wish.DecisionVersion != decisionVersion
                || !wish.HasEmote
                || locked.Settings is not { BotModeEnabled: true }
                || locked.ActiveEmoteSetId is not { Length: > 0 } setId)
            {
                // The reconcile or a manager was faster (AK83), or a PATCH moved the version: the next pass takes it up.
                return (Booking.Missed, null);
            }

            // One add per set and emote at a time, across channels (2.5a (a), R2-2).
            await deadline.ApplyAsync(db, cancellationToken);
            var busy = await db.Wishes.AsNoTracking().AnyAsync(
                o => o.Id != wish.Id
                    && o.BotSetId == setId
                    && o.SevenTvEmoteId == wish.SevenTvEmoteId
                    && ((o.Status == WishStatus.Approved && o.BotWriteAtUtc != null) || o.Status == WishStatus.Unconfirmed),
                cancellationToken);
            if (busy)
            {
                logger.LogInformation(
                    "Wish bot: wish {WishId} waits, another add of the same emote into the same set is open.", wish.Id);
                return (Booking.Missed, null);
            }

            var stamp = ToDatabasePrecision(Now());
            wish.BotWriteAtUtc = stamp;
            wish.BotSetId = setId;
            wish.DecisionVersion++;
            wish.BotAttemptVersion = wish.DecisionVersion;
            wish.SlotId = null;
            wish.LinkOrigin = null;
            wish.BotAddAckAtUtc = null;
            wish.BotAttemptCount = 0;
            wish.BotNextAttemptAtUtc = null;
            wish.BotConfirmDeadlineAtUtc = null;
            var intent = new AddIntent(stamp, wish.DecisionVersion, setId) { EmoteId = wish.SevenTvEmoteId };

            await deadline.ApplyAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await deadline.ApplyAsync(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (Booking.Hit, intent);
        }
        catch (Exception ex) when (BotAccountLockDeadline.IsExpiry(ex, cancellationToken))
        {
            // Unclear whether the intent committed: then it is a crash remnant, resolved without a second add.
            logger.LogWarning(
                "Wish bot: recording the add intent of wish {WishId} ran out of its lock deadline or failed ({ExceptionType}).",
                item.WishId, ex.GetType().Name);
            return (Booking.Expired, null);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    // ------------------------------------------------------------------------------------ (2) the pre-read

    private async Task<QueueStep> PreReadAsync(
        IBotAccountSession session, WishBotQueueItem item, AddIntent intent, CancellationToken cancellationToken)
    {
        var preflight = await PreflightAsync(session, item.ChannelId, requireEditorProof: false, cancellationToken);
        if (preflight is not null)
        {
            return await WithdrawThenAsync(session, item, intent, pause: null, preflight.Value, cancellationToken);
        }

        var result = await writeClient.ReadSetEntriesAsync(session.Token!, intent.SetId, cancellationToken: cancellationToken);
        var pause = Report(session, result.Status);
        if (!await session.HoldsLockAsync(cancellationToken))
        {
            if (pause is { } pending)
            {
                await session.PauseAsync(pending, cancellationToken);
            }

            return QueueStep.Stop;
        }

        if (!result.IsOk || pause is not null)
        {
            logger.LogInformation("Wish bot: the pre-read for wish {WishId} came to {Outcome}; the intent is taken back.", item.WishId, result.Outcome);
            return await WithdrawThenAsync(session, item, intent, pause, QueueStep.Continue, cancellationToken);
        }

        if (result.Value!.Any(e => string.Equals(e.EmoteId, intent.EmoteId, StringComparison.Ordinal)))
        {
            var booking = await BookWishAsync(session, item.ChannelId, item.WishId, null, (locked, now) =>
            {
                var wish = locked.Wish;
                if (!intent.Matches(wish)
                    || WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.BotAlreadyInSet) is not { } target)
                {
                    return false;
                }

                ClearAddAttempt(wish);

                // The hit is in the attempt's set; when the channel's active set moved meanwhile, the never-sent intent
                // only goes back, and the next pass adds into the new set (plan 2.5a (c), set binding).
                if (!string.Equals(locked.ActiveEmoteSetId, intent.SetId, StringComparison.Ordinal))
                {
                    return true;
                }

                wish.Status = target;
                wish.StatusReason = WishStatusReason.AlreadyInSet;
                wish.StatusChangedAtUtc = now;
                return true;
            }, cancellationToken);
            return booking == Booking.Fenced ? QueueStep.Stop : QueueStep.Continue;
        }

        intent.ReadyToSend = true;
        return QueueStep.Continue;
    }

    // ------------------------------------------------------------------------------------ (3) the send transaction

    private async Task<QueueStep> SendAddAsync(
        IBotAccountSession session, WishBotQueueItem item, AddIntent intent, CancellationToken cancellationToken)
    {
        var sent = false;
        var refusal = AddRefusal.None;
        var readNext = false;
        string? announce = null;
        TimeSpan? unreportedPause = null;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deadline = new BotAccountLockDeadline(timeProvider);
            var (locking, locked) = await LockWishRowsAsync(session, item.ChannelId, item.WishId, deadline, cancellationToken);
            if (locking == Booking.Fenced)
            {
                // The epoch moved on: the intent stays for the successor's crash path (2.5a (e)).
                session.ReportLockLost();
                return QueueStep.Stop;
            }

            if (locking != Booking.Hit)
            {
                return QueueStep.Continue;
            }

            refusal = RecheckAdd(session, locked!, intent, deadline, Now());
            if (refusal != AddRefusal.None)
            {
                logger.LogInformation("Wish bot: the add of wish {WishId} is not sent ({Refusal}).", item.WishId, refusal);
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                var wish = locked!.Wish;
                await deadline.ApplyAsync(db, cancellationToken);
                session.MarkWriteSent();
                sent = true;
                var result = await writeClient.AddEmoteAsync(session.Token!, intent.SetId, wish.SevenTvEmoteId, wish.Alias, cancellationToken);
                logger.LogInformation(
                    "Wish bot: the add of wish {WishId} came to {Outcome} (send proof: {SendProof}, answer carried entries: {AnswerHadEntries}).",
                    item.WishId, result.Outcome, result.IsOk, result.Value is not null);

                readNext = BookAdd(session, locked, intent, result, Now());
                unreportedPause = result.Status.RetryAfter;
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
                announce = locked.ChannelName;
            }
        }
        catch (Exception ex) when (BotAccountLockDeadline.IsExpiry(ex, cancellationToken))
        {
            logger.LogWarning(
                "Wish bot: the send transaction of wish {WishId} ran out of its lock deadline or failed ({ExceptionType}){Sent}.",
                item.WishId, ex.GetType().Name, sent ? "; the intent stays open for the crash path" : string.Empty);
            refusal = sent ? AddRefusal.None : AddRefusal.Expired;
            readNext = false;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        if (!sent && refusal is not (AddRefusal.None or AddRefusal.AttemptGone)
            && await WithdrawAsync(session, item, intent, pause: null, cancellationToken) == Booking.Fenced)
        {
            return QueueStep.Stop;
        }

        if (unreportedPause is { } pending)
        {
            await session.PauseAsync(pending, cancellationToken);
        }

        if (announce is not null)
        {
            await changedPublisher.PublishAsync(announce, cancellationToken);
        }

        if (refusal == AddRefusal.CredentialChanged || (sent && !await session.HoldsLockAsync(cancellationToken)))
        {
            return QueueStep.Stop;
        }

        // The answer did not carry the entry: the schedule begins with the read at 0 s (2.6 point 3).
        return readNext ? await RunConfirmAsync(session, item.ChannelId, item.WishId, cancellationToken) : QueueStep.Continue;
    }

    // The checks behind the send transaction's locks (2.5a (c)).
    private AddRefusal RecheckAdd(
        IBotAccountSession session, LockedWish locked, AddIntent intent, BotAccountLockDeadline deadline, DateTime now)
    {
        if (!intent.Matches(locked.Wish))
        {
            return AddRefusal.AttemptGone;
        }

        if (locked.Settings is not { BotModeEnabled: true } settings)
        {
            return AddRefusal.ModeOff;
        }

        if (!string.Equals(locked.ActiveEmoteSetId, intent.SetId, StringComparison.Ordinal))
        {
            return AddRefusal.SetChanged;
        }

        var preflight = WishBotPreflight.Check(
            SwitchesOn,
            CoveredForBot(settings, locked.TwitchChannelId),
            settings,
            SnapshotOf(locked.Account),
            session.Account.CredentialVersion,
            session.TokenUsable,
            now,
            requireEditorProof: true);
        if (preflight == WishBotPreflightOutcome.CredentialChanged)
        {
            return AddRefusal.CredentialChanged;
        }

        if (preflight != WishBotPreflightOutcome.Pass)
        {
            return AddRefusal.Preflight;
        }

        return deadline.Fits(BotAccountRegistration.WriteClientTimeout) ? AddRefusal.None : AddRefusal.NoTimeLeft;
    }

    // The booking of addEmote's answer under the send transaction's locks (2.6 point 3); the pause of a RetryAfter is the
    // caller's. True when the wish is unconfirmed and its read at 0 s is due.
    private bool BookAdd(
        IBotAccountSession session, LockedWish locked, AddIntent intent, SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>> result, DateTime now)
    {
        var wish = locked.Wish;
        switch (result.Outcome)
        {
            case SevenTvWriteOutcome.Ok:
                ToUnconfirmed(wish, intent.StampUtc, ToDatabasePrecision(now), now);
                db.AddAuditEntry(
                    AuditActor.System,
                    AuditActions.WishBotAdd,
                    channelName: locked.ChannelName,
                    targetType: "wish",
                    targetId: wish.Id.ToString(CultureInfo.InvariantCulture),
                    details: new { wishId = wish.Id, sevenTvEmoteId = wish.SevenTvEmoteId },
                    occurredAtUtc: now);

                // AK89: the answer's entries stand in for the read at 0 s.
                if (result.Value is { } entries)
                {
                    var confirmation = WishBotOwnership.Confirm(
                        entries, wish.SevenTvEmoteId, wish.Alias, locked.Account.BotSevenTvUserId, intent.StampUtc, now, sendAcknowledged: true);
                    if (confirmation.Confirmed)
                    {
                        ApplyConfirmation(wish, confirmation, now);
                        return false;
                    }
                }

                return true;

            case SevenTvWriteOutcome.Rejected:
                ClearAddAttempt(wish);
                wish.Status = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.BotReject)!;
                wish.StatusReason = FailedReasonOf(result.Status.Rejection);
                wish.StatusChangedAtUtc = now;
                return false;

            case SevenTvWriteOutcome.LackingPrivileges:
                // AK80: the wish stays approved, the channel is suspended as not_editor (one wish.botSuspend).
                ClearAddAttempt(wish);
                if (locked.Settings is { } settings)
                {
                    Suspend(settings, locked.ChannelName, WishBotChannelReasons.NotEditor, now);
                }

                return false;

            case SevenTvWriteOutcome.Unauthorized:
                // K6b-3: reported, not booked as invalid, no suspension; the next pass's verification decides.
                ClearAddAttempt(wish);
                session.ReportUnauthorized();
                return false;

            case SevenTvWriteOutcome.RateLimited:
                ClearAddAttempt(wish);
                return false;

            default:
                // Timeout, Unavailable: whether 7TV wrote is open; the reads decide, at most without ownership.
                ToUnconfirmed(wish, intent.StampUtc, acknowledgedAtUtc: null, now);
                return true;
        }
    }

    // Takes back an intent that was provably never sent (2.5a (c)), with a reported pause joined; then goes on as given.
    private async Task<QueueStep> WithdrawThenAsync(
        IBotAccountSession session, WishBotQueueItem item, AddIntent intent, TimeSpan? pause, QueueStep then, CancellationToken cancellationToken) =>
        await WithdrawAsync(session, item, intent, pause, cancellationToken) == Booking.Fenced ? QueueStep.Stop : then;

    // Conditional on the intent being unchanged; a failure leaves it to the crash path.
    private Task<Booking> WithdrawAsync(
        IBotAccountSession session, WishBotQueueItem item, AddIntent intent, TimeSpan? pause, CancellationToken cancellationToken) =>
        BookWishAsync(session, item.ChannelId, item.WishId, pause, (locked, _) =>
        {
            if (!intent.Matches(locked.Wish))
            {
                return false;
            }

            ClearAddAttempt(locked.Wish);
            return true;
        }, cancellationToken);

    // ------------------------------------------------------------------------------------ the crash path

    private async Task<QueueStep> ResumeCrashedAddAsync(
        IBotAccountSession session, Wish seen, CancellationToken cancellationToken)
    {
        if (seen.BotSetId is null || seen.BotWriteAtUtc is not { } written)
        {
            // Nothing to read: the deadline step presents it.
            return QueueStep.Continue;
        }

        logger.LogWarning(
            "Wish bot: wish {WishId} carries an add intent of an earlier pass; it is confirmed by reads, never added again.", seen.Id);
        var booking = await BookWishAsync(session, seen.ChannelId, seen.Id, null, (locked, now) =>
        {
            var wish = locked.Wish;
            if (!string.Equals(wish.Status, WishStatus.Approved, StringComparison.Ordinal)
                || wish.BotWriteAtUtc != written
                || wish.DecisionVersion != seen.DecisionVersion
                || wish.BotAttemptVersion != wish.DecisionVersion)
            {
                return false;
            }

            ToUnconfirmed(wish, written, acknowledgedAtUtc: null, now);
            return true;
        }, cancellationToken);

        return booking switch
        {
            Booking.Fenced => QueueStep.Stop,
            Booking.Hit => await RunConfirmAsync(session, seen.ChannelId, seen.Id, cancellationToken),
            _ => QueueStep.Continue,
        };
    }

    // ------------------------------------------------------------------------------------ a confirmation read

    private async Task<QueueStep> RunConfirmAsync(
        IBotAccountSession session, string channelId, long wishId, CancellationToken cancellationToken)
    {
        var seen = await db.Wishes.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wishId && w.ChannelId == channelId, cancellationToken);
        // Past its deadline only the deadline step decides (2.6 point 5): no read, no booking here.
        if (seen is not { BotSetId: { } setId, BotNextAttemptAtUtc: { } planned }
            || !string.Equals(seen.Status, WishStatus.Unconfirmed, StringComparison.Ordinal)
            || planned > Now()
            || seen.BotAttemptVersion != seen.DecisionVersion
            || PastConfirmDeadline(seen, Now()))
        {
            return QueueStep.Continue;
        }

        if (await PreflightAsync(session, channelId, requireEditorProof: false, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (!session.TryTakeAction())
        {
            return QueueStep.Exhausted;
        }

        if (!await session.HoldsLockAsync(cancellationToken))
        {
            return QueueStep.Stop;
        }

        var readAt = Now();
        var result = await writeClient.ReadSetEntriesAsync(session.Token!, setId, cancellationToken: cancellationToken);
        var pause = Report(session, result.Status);
        if (!await session.HoldsLockAsync(cancellationToken))
        {
            if (pause is { } pending)
            {
                await session.PauseAsync(pending, cancellationToken);
            }

            return QueueStep.Stop;
        }

        var booking = await BookWishAsync(session, channelId, wishId, pause, (locked, now) =>
        {
            var wish = locked.Wish;
            if (!string.Equals(wish.Status, WishStatus.Unconfirmed, StringComparison.Ordinal)
                || !string.Equals(wish.BotSetId, setId, StringComparison.Ordinal)
                || wish.BotWriteAtUtc != seen.BotWriteAtUtc
                || wish.DecisionVersion != seen.DecisionVersion
                || wish.BotAttemptVersion != wish.DecisionVersion
                || wish.BotNextAttemptAtUtc != planned
                || wish.BotAttemptCount != seen.BotAttemptCount
                || wish.BotConfirmDeadlineAtUtc != seen.BotConfirmDeadlineAtUtc
                || PastConfirmDeadline(wish, now))
            {
                // Changed meanwhile, or the read crossed the deadline: the deadline step presents it.
                return false;
            }

            if (ConfirmationOf(locked, result, readAt) is { Confirmed: true } confirmation)
            {
                ApplyConfirmation(wish, confirmation, now);
                return true;
            }

            // No match, a read that failed, or a set that changed: it counts as a read (2.6 point 5).
            wish.BotNextAttemptAtUtc = WishBotConfirmSchedule.Next(planned, wish.BotAttemptCount);
            wish.BotAttemptCount++;
            return true;
        }, cancellationToken);

        return booking == Booking.Fenced ? QueueStep.Stop : QueueStep.Continue;
    }

    // The decision of a read under the booking's locks (2.6 point 6, 2.5a (d)): bound to the attempt's set while it is
    // still the channel's active one, and to the bot account and consent as the fence shows them.
    private static WishBotConfirmation ConfirmationOf(
        LockedWish locked, SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>> result, DateTime readAtUtc)
    {
        var wish = locked.Wish;
        var row = locked.Account;
        if (!result.IsOk
            || result.Value is not { } entries
            || wish.BotWriteAtUtc is not { } written
            || locked.Settings is not { BotModeEnabled: true } settings
            || row.KillSwitch
            || !string.Equals(row.Status, BotAccountStatus.Valid, StringComparison.Ordinal)
            || !WishBotPreflight.ConsentApplies(settings, row.BotSevenTvUserId)
            || !string.Equals(locked.ActiveEmoteSetId, wish.BotSetId, StringComparison.Ordinal))
        {
            return WishBotConfirmation.NotConfirmed;
        }

        return WishBotOwnership.Confirm(
            entries, wish.SevenTvEmoteId, wish.Alias, row.BotSevenTvUserId, written, readAtUtc, wish.BotAddAckAtUtc is not null);
    }

    // ------------------------------------------------------------------------------------ field rules

    // bot_write (or the crash path): unconfirmed, the schedule from the write, the acknowledgement only for a proven Ok.
    private static void ToUnconfirmed(Wish wish, DateTime writtenAtUtc, DateTime? acknowledgedAtUtc, DateTime now)
    {
        wish.Status = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.BotWrite)!;
        wish.StatusReason = null;
        wish.StatusChangedAtUtc = now;
        wish.BotAddAckAtUtc = acknowledgedAtUtc;
        wish.BotAttemptCount = 0;
        wish.BotNextAttemptAtUtc = WishBotConfirmSchedule.First(writtenAtUtc);
        wish.BotConfirmDeadlineAtUtc = WishBotConfirmSchedule.Deadline(writtenAtUtc);
    }

    // bot_confirm (2.6 point 6, 2.1 points 1 and 3): the authoritative start from addedAt, LinkOrigin bot, kept for a
    // permanent wish (no ownership); ownership with BotSetId kept and one decision counted, else BotSetId cleared.
    private static void ApplyConfirmation(Wish wish, WishBotConfirmation confirmation, DateTime now)
    {
        var target = WishTransitions.Target(
            wish.Status, wish.StatusReason, WishAction.BotConfirm, new WishTransitionContext(Permanent: wish.Permanent))!;
        var start = confirmation.AddedAtUtc!.Value;
        wish.Status = target;
        wish.StatusReason = null;
        wish.StatusChangedAtUtc = now;
        wish.EndedAtUtc = WishStatus.IsEnded(target) ? now : null;
        wish.StartsAtUtc = start;

        // kept has no expiry (2.1 point 1), whatever duration the wish carried before.
        wish.ExpiresAtUtc = string.Equals(target, WishStatus.Active, StringComparison.Ordinal)
            ? WishSchedule.ExpiresAt(start, wish.DurationDays)
            : null;
        wish.StartsAtAuthoritative = true;
        wish.LinkOrigin = WishLinkOrigin.Bot;
        if (string.Equals(target, WishStatus.Active, StringComparison.Ordinal))
        {
            wish.FirstActivatedAtUtc ??= now;
        }

        if (confirmation.Owned && string.Equals(target, WishStatus.Active, StringComparison.Ordinal))
        {
            wish.BotOwned = true;
            wish.BotOwnedAddedAtUtc = start;
            wish.DecisionVersion++;
        }
        else
        {
            wish.BotOwned = false;
            wish.BotOwnedAddedAtUtc = null;
            wish.BotSetId = null;
        }

        wish.BotAttemptCount = 0;
        wish.BotNextAttemptAtUtc = null;
        wish.BotConfirmDeadlineAtUtc = null;
        wish.BotAttemptVersion = null;
    }

    private static bool PastConfirmDeadline(Wish wish, DateTime now) =>
        wish.BotConfirmDeadlineAtUtc is { } deadline && deadline <= now;

    // "Versuch leeren" for an add: the wish is as before the intent (its decision version stays counted).
    private static void ClearAddAttempt(Wish wish)
    {
        wish.BotWriteAtUtc = null;
        wish.BotSetId = null;
        wish.BotAttemptVersion = null;
        wish.BotAddAckAtUtc = null;
        wish.BotAttemptCount = 0;
        wish.BotNextAttemptAtUtc = null;
        wish.BotConfirmDeadlineAtUtc = null;
    }

    // 2.5a (i): Unauthorized is reported at once; a RetryAfter is returned for the caller to join to its booking.
    private static TimeSpan? Report(IBotAccountSession session, SevenTvWriteResult status)
    {
        if (status.Outcome == SevenTvWriteOutcome.Unauthorized)
        {
            session.ReportUnauthorized();
        }

        return status.RetryAfter;
    }

    private static string FailedReasonOf(SevenTvWriteRejection? rejection) => rejection switch
    {
        SevenTvWriteRejection.AliasConflict => WishStatusReason.AliasConflict,
        SevenTvWriteRejection.SetFull => WishStatusReason.SetFull,
        SevenTvWriteRejection.EmoteGone => WishStatusReason.EmoteGone,
        _ => WishStatusReason.Unknown,
    };

    private enum AddRefusal
    {
        None,

        // The intent is no longer this one (resolved by DELETE …/wishes/bot, the fallback or the deadline step): nothing
        // to take back.
        AttemptGone,
        ModeOff,
        SetChanged,
        CredentialChanged,
        Preflight,
        NoTimeLeft,
        Expired,
    }

    // The intent as committed: its stamp, the decision version it was bound to, the set it writes to.
    private sealed record AddIntent(DateTime StampUtc, int Version, string SetId)
    {
        public string EmoteId { get; init; } = string.Empty;

        // The pre-read found the set without the emote.
        public bool ReadyToSend { get; set; }

        public bool Matches(Wish wish) =>
            string.Equals(wish.Status, WishStatus.Approved, StringComparison.Ordinal)
            && wish.BotWriteAtUtc == StampUtc
            && wish.DecisionVersion == Version
            && wish.BotAttemptVersion == Version
            && string.Equals(wish.BotSetId, SetId, StringComparison.Ordinal);
    }
}
