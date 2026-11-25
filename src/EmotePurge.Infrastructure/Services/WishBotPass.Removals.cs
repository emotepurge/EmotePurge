using System.Globalization;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

// The removals of the queue (#487 plan 2.7 with 2.5a, spec 8; T10). Three kinds dock onto the frame of T9:
//
// A queued removal (active past its expiry, or presented with an order of bot-remove), in this order: the preflight
// without the editor stage; TryTakeAction; WaitForWriteSlotAsync (never under a lock); the editor proof; the full
// preflight; the probe; (1) transaction 1 under the wish lock only: eligible (queue bound to the decision version, owned,
// start authoritative, bot channel), not eligible → the queue is cleared without counting a decision (the expiry run
// decides again, without a second wish.due); the active set must still be BotSetId (else ownership_lost); then the fresh
// read of BotSetId within the HTTP budget: the alias gone → removed_externally, one of the four features changed →
// ownership_lost, else the attempt with its evidence (the set included, K6c-2) is committed behind the fence; an open
// repair phase leaves the queue as it is. The probe. (2) transaction 2, the send transaction (2.5a (c)): channel FOR
// SHARE, wish FOR UPDATE, the fence, the re-check, the fresh read of the evidence's set right before the send (within the
// budget end minus the write's 10 s, K6d-5): compared with the evidence, a "no" sends nothing and takes the never-sent
// attempt back in the same commit (a reported pause joined to it); otherwise MarkWriteSent and exactly one removeEmote.
// Only the definite failures are booked there; Ok, Timeout and Unavailable leave the attempt open. The probe.
// (3) transaction 3: the read after it without a lock and a short booking: the bot's entry gone → removed (wish.botRemove),
// or, when another entry of the emote went with it, presented (repair_review) with the repair phase; the detection limit
// (wish.botSuspectRemoval) looks at the local slots behind the locks. (4) transaction 4, the repair, only in the same pass
// right after: exactly one addEmote under the missing alias into the evidence's set, its definite answer booked as
// wish.botRepair.
//
// An open removal attempt (BotRemoveAtUtc; a crash, a lost lock, an unclear answer, a failed read after the send) is
// resumed by a read of the evidence's set (2.5a (e)), never by sending again: the bot's entry still there → the attempt
// is cleared and the queue stays; gone → removed with outcome resumed, the evidence going into the repair phase when other
// entries of the emote were recorded. The deadline step resolves an attempt older than 15 minutes without 7TV.
//
// An open repair phase (BotRepairAtUtc with evidence, 2.5a (h)) is resolved read only, in any channel state, as soon as a
// read is possible: the bot's entry still there → phase and evidence cleared; otherwise one wish.botRepair needs_review
// per recorded entry that is missing; every booking bound to the phase's stamp alone (K6d-1).
//
// A stuck order (2.7 "Festsitzender Auftrag", K6e-5, K6f-2, K6f-3): aborts at a read or a deadline before sending are
// counted per order generation in memory (WishBotStuckRemovals); after three in a row, the first at least 15 minutes ago,
// the order is presented like the fallback run would for this one wish, conditional on the counted generation.
//
// Every result of a call with the token reports to the session (2.5a (i)), every page of a read included.
public sealed partial class WishBotPass
{
    // 2.5 step 0: an open removal attempt older than this is resolved without 7TV.
    private static readonly TimeSpan RemovalResumeWindow = TimeSpan.FromMinutes(15);

    private readonly WishBotStuckRemovals _stuckRemovals = new();

    // ------------------------------------------------------------------------------------ the selector

    // Repair checks in any channel state (2.5a (h)); open attempts and queued removals only in bot channels.
    private async Task<List<WishBotQueueItem>> SelectRemovalWorkAsync(IReadOnlyCollection<string> botChannels, CancellationToken cancellationToken)
    {
        var rows = await db.Wishes.AsNoTracking()
            .Where(w => (w.BotRepairAtUtc != null && w.BotRemoveEvidence != null)
                || (botChannels.Contains(w.ChannelId)
                    && (w.BotRemoveAtUtc != null
                        || ((w.Status == WishStatus.Active || w.Status == WishStatus.Presented) && w.RemovalQueuedAtUtc != null))))
            .Select(w => new { w.Id, w.ChannelId, w.BotRepairAtUtc, w.BotRemoveAtUtc, w.RemovalQueuedAtUtc })
            .ToListAsync(cancellationToken);

        var items = new List<WishBotQueueItem>();
        foreach (var row in rows)
        {
            if (row.BotRepairAtUtc is { } phase)
            {
                items.Add(new WishBotQueueItem(WishBotQueueKind.RepairCheck, row.Id, row.ChannelId, phase));
            }

            if (!botChannels.Contains(row.ChannelId))
            {
                continue;
            }

            if (row.BotRemoveAtUtc is { } attempt)
            {
                items.Add(new WishBotQueueItem(WishBotQueueKind.RemovalAttempt, row.Id, row.ChannelId, attempt));
            }
            else if (row.RemovalQueuedAtUtc is { } queued)
            {
                items.Add(new WishBotQueueItem(WishBotQueueKind.Removal, row.Id, row.ChannelId, queued));
            }
        }

        return items;
    }

    // ------------------------------------------------------------------------------------ a queued removal

    private async Task<QueueStep> RunRemovalAsync(
        IBotAccountSession session, QueueState state, WishBotQueueItem item, CancellationToken cancellationToken)
    {
        var seen = await db.Wishes.AsNoTracking().FirstOrDefaultAsync(w => w.Id == item.WishId && w.ChannelId == item.ChannelId, cancellationToken);
        if (seen is not { RemovalQueuedAtUtc: not null, BotRemoveAtUtc: null, BotRepairAtUtc: null }
            || seen.Status is not (WishStatus.Active or WishStatus.Presented))
        {
            // Gone (K6b-17), resolved, or waiting for its repair phase: nothing to do.
            return QueueStep.Continue;
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

        var generation = new WishBotRemovalGeneration(seen.Id, seen.RemovalQueuedVersion, seen.RemovalQueuedAtUtc);
        var (first, attempt) = await CommitRemovalAttemptAsync(session, state, item, cancellationToken);
        if (first != RemovalStep.Sendable)
        {
            return await AfterRemovalStepAsync(session, item, generation, first, cancellationToken);
        }

        // From here the attempt is durable: a crash or a lost lock leaves it to the resumption, it is never sent blind.
        if (!await session.HoldsLockAsync(cancellationToken))
        {
            return QueueStep.Stop;
        }

        var (second, readAfter) = await SendRemovalAsync(session, item, attempt!, cancellationToken);
        if (second != RemovalStep.Sendable)
        {
            return await AfterRemovalStepAsync(session, item, generation, second, cancellationToken);
        }

        _stuckRemovals.Reset(item.WishId);
        if (!await session.HoldsLockAsync(cancellationToken))
        {
            return QueueStep.Stop;
        }

        // Ok, Timeout or Unavailable: the read after it decides (transaction 3); with the token resting after a 429, or
        // after a transaction that ended at its deadline, it waits for the resumption of a later pass.
        if (!readAfter || !session.TokenUsable)
        {
            return QueueStep.Continue;
        }

        return await ReadAfterRemovalAsync(session, item, attempt!, cancellationToken);
    }

    // What a removal does after a step that did not lead to a send: counts a stuck abort, ends the count otherwise.
    private async Task<QueueStep> AfterRemovalStepAsync(
        IBotAccountSession session, WishBotQueueItem item, WishBotRemovalGeneration generation, RemovalStep step, CancellationToken cancellationToken)
    {
        switch (step)
        {
            case RemovalStep.Stop:
                return QueueStep.Stop;
            case RemovalStep.Aborted:
                if (_stuckRemovals.Abort(session.RunnerEpoch, generation, Now()))
                {
                    return await PresentStuckRemovalAsync(session, item, generation, cancellationToken);
                }

                return QueueStep.Continue;
            case RemovalStep.Untouched:
                return QueueStep.Continue;
            default:
                _stuckRemovals.Reset(item.WishId);
                return step == RemovalStep.CredentialChanged ? QueueStep.Stop : QueueStep.Continue;
        }
    }

    // ------------------------------------------------------------------------------------ (1) transaction 1

    private async Task<(RemovalStep Step, RemovalAttempt? Attempt)> CommitRemovalAttemptAsync(
        IBotAccountSession session, QueueState state, WishBotQueueItem item, CancellationToken cancellationToken)
    {
        var step = RemovalStep.Untouched;
        RemovalAttempt? attempt = null;
        string? announce = null;
        TimeSpan? unreportedPause = null;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deadline = new BotAccountLockDeadline(timeProvider);
            await deadline.ApplyAsync(db, cancellationToken);
            if ((await db.LockChannelWishesByIdsAsync(item.ChannelId, [item.WishId], cancellationToken)).Count == 0)
            {
                // K6b-17: kept and deleted since the selection.
                return (RemovalStep.Untouched, null);
            }

            deadline.Start();
            await deadline.ApplyAsync(db, cancellationToken);
            var wish = await db.Wishes.FirstAsync(w => w.Id == item.WishId, cancellationToken);
            await deadline.ApplyAsync(db, cancellationToken);
            var facts = await db.ChannelWishSettings.AsNoTracking()
                .Where(s => s.ChannelId == item.ChannelId)
                .Select(s => new { Settings = s, s.Channel!.ChannelName, s.Channel.TwitchChannelId, s.Channel.ActiveEmoteSetId })
                .FirstOrDefaultAsync(cancellationToken);
            var now = Now();
            if (wish.RemovalQueuedAtUtc is null
                || wish.BotRemoveAtUtc is not null
                || WishBotAttemptResolution.HasOpenAdd(wish)
                || facts is null)
            {
                return (RemovalStep.Untouched, null);
            }

            if (!RemovalEligible(wish, now))
            {
                // Plan-484 finding 11: the queue goes, the decision version stays; the expiry run decides again.
                wish.RemovalQueuedAtUtc = null;
                wish.RemovalQueuedVersion = null;
                logger.LogInformation("Wish bot: wish {WishId} is no longer eligible for a bot removal; its queue is cleared.", wish.Id);
                step = RemovalStep.Booked;
            }
            else if (wish.BotRepairAtUtc is not null
                || WishBotChannelState.Of(
                        facts.Settings,
                        state.Account,
                        wishOptions.PilotList.Covers(facts.TwitchChannelId),
                        WishBotChannelStateReader.ActivatorIsAdminById(admins, facts.Settings),
                        now)
                    != WishBotChannelMode.Bot)
            {
                // A repair phase first (2.5a (h)); a channel out of bot mode is the fallback run's.
                return (RemovalStep.Untouched, null);
            }
            else if (!string.Equals(facts.ActiveEmoteSetId, wish.BotSetId, StringComparison.Ordinal) || wish.BotSetId is null)
            {
                OwnershipLost(wish, now);
                step = RemovalStep.Booked;
            }
            else
            {
                await deadline.ApplyAsync(db, cancellationToken);
                var result = await writeClient.ReadSetEntriesAsync(
                    session.Token!, wish.BotSetId, HttpTimeLeft(deadline, TimeSpan.Zero), cancellationToken);
                unreportedPause = Report(session, result.Status);
                if (!result.IsOk || result.Value is not { } entries)
                {
                    logger.LogInformation("Wish bot: the read before the removal of wish {WishId} came to {Outcome}.", wish.Id, result.Outcome);
                    step = Counts(result.Outcome) ? RemovalStep.Aborted : RemovalStep.Refused;
                    if (unreportedPause is null)
                    {
                        return (step, null);
                    }
                }
                else
                {
                    switch (WishBotRemovalEvidence.Classify(entries, wish.SevenTvEmoteId, wish.Alias, wish.BotOwnedAddedAtUtc ?? DateTime.MinValue, state.Account.BotSevenTvUserId))
                    {
                        case WishBotRemovalRead.Gone:
                            RemovedExternally(wish, now);
                            step = RemovalStep.Booked;
                            break;
                        case WishBotRemovalRead.Changed:
                            OwnershipLost(wish, now);
                            step = RemovalStep.Booked;
                            break;
                        default:
                            var stamp = ToDatabasePrecision(now);
                            var evidence = WishBotRemovalEvidence.Capture(
                                entries, wish.BotSetId, wish.SevenTvEmoteId, wish.Alias, wish.BotOwnedAddedAtUtc!.Value, state.Account.BotSevenTvUserId!);
                            wish.BotRemoveAtUtc = stamp;
                            wish.BotAttemptVersion = wish.DecisionVersion;
                            wish.BotRemoveEvidence = evidence.ToJson();
                            attempt = new RemovalAttempt(stamp, wish.DecisionVersion, wish.Status, evidence);
                            step = RemovalStep.Sendable;
                            break;
                    }
                }
            }

            // The fence before anything is committed (2.5a (g)).
            await deadline.ApplyAsync(db, cancellationToken);
            if (await db.LockBotAccountForShareAsync(session.RunnerEpoch, cancellationToken) is null)
            {
                logger.LogWarning("Bot account: runner epoch {Epoch} was superseded; this pass books nothing more.", session.RunnerEpoch);
                session.ReportLockLost();
                return (RemovalStep.Stop, null);
            }

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
            announce = step == RemovalStep.Booked ? facts.ChannelName : null;
        }
        catch (Exception ex) when (BotAccountLockDeadline.IsExpiry(ex, cancellationToken))
        {
            // Unclear whether the attempt committed: then it is an open attempt, resumed by a read.
            logger.LogWarning(
                "Wish bot: transaction 1 of the removal of wish {WishId} ran out of its lock deadline or failed ({ExceptionType}).",
                item.WishId, ex.GetType().Name);
            step = RemovalStep.Aborted;
            attempt = null;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        if (unreportedPause is { } pending)
        {
            await session.PauseAsync(pending, cancellationToken);
        }

        if (announce is not null)
        {
            await changedPublisher.PublishAsync(announce, cancellationToken);
        }

        return (step, attempt);
    }

    // ------------------------------------------------------------------------------------ (2) the send transaction

    // Sent: removeEmote went out and answered Ok, Timeout or Unavailable within a transaction that ended normally, so the
    // read after it decides in this pass; otherwise the attempt is booked, taken back, or open for a later resumption.
    private async Task<(RemovalStep Step, bool Sent)> SendRemovalAsync(
        IBotAccountSession session, WishBotQueueItem item, RemovalAttempt attempt, CancellationToken cancellationToken)
    {
        var sent = false;
        var readAfter = false;
        var step = RemovalStep.Untouched;
        var withdrawLater = false;
        string? announce = null;
        TimeSpan? unreportedPause = null;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deadline = new BotAccountLockDeadline(timeProvider);
            var (locking, locked) = await LockWishRowsAsync(session, item.ChannelId, item.WishId, deadline, cancellationToken);
            if (locking == Booking.Fenced)
            {
                // The epoch moved on: the attempt stays for the successor's resumption (2.5a (e)).
                session.ReportLockLost();
                return (RemovalStep.Stop, false);
            }

            if (locking != Booking.Hit)
            {
                return (RemovalStep.Untouched, false);
            }

            var wish = locked!.Wish;
            var now = Now();
            var refusal = RecheckRemoval(session, locked, attempt, deadline, now);
            var changed = false;
            if (refusal == RemovalRefusal.AttemptGone)
            {
                // Resolved by DELETE …/wishes/bot, the fallback or the deadline step: nothing to take back.
                return (RemovalStep.Untouched, false);
            }

            if (refusal != RemovalRefusal.None)
            {
                logger.LogInformation("Wish bot: the removal of wish {WishId} is not sent ({Refusal}).", item.WishId, refusal);
                ClearRemovalAttempt(wish);
                changed = true;
                step = refusal switch
                {
                    RemovalRefusal.CredentialChanged => RemovalStep.CredentialChanged,
                    RemovalRefusal.NoTimeLeft => RemovalStep.Aborted,
                    _ => RemovalStep.Refused,
                };
            }
            else
            {
                // The fresh read right before the send (Revision 6a S6-5), ending 10 s before the budget for removeEmote.
                await deadline.ApplyAsync(db, cancellationToken);
                var read = await writeClient.ReadSetEntriesAsync(
                    session.Token!, attempt.Evidence.SetId, HttpTimeLeft(deadline, BotAccountRegistration.WriteClientTimeout), cancellationToken);
                unreportedPause = Report(session, read.Status);
                changed = true;
                if (!read.IsOk || read.Value is not { } entries)
                {
                    logger.LogInformation("Wish bot: the fresh read before the removal of wish {WishId} came to {Outcome}; the attempt is taken back.", item.WishId, read.Outcome);
                    ClearRemovalAttempt(wish);
                    step = Counts(read.Outcome) ? RemovalStep.Aborted : RemovalStep.Refused;
                }
                else
                {
                    switch (attempt.Evidence.Classify(entries))
                    {
                        case WishBotRemovalRead.Gone:
                            ClearRemovalAttempt(wish);
                            RemovedExternally(wish, now);
                            step = RemovalStep.Booked;
                            break;
                        case WishBotRemovalRead.Changed:
                            ClearRemovalAttempt(wish);
                            OwnershipLost(wish, now);
                            step = RemovalStep.Booked;
                            break;
                        default:
                            if (!attempt.Evidence.OthersUnchanged(entries))
                            {
                                // Another entry of the emote moved: a new evidence first, in the next pass (AK129).
                                logger.LogInformation("Wish bot: the other entries of wish {WishId}'s emote changed; the removal records new evidence first.", item.WishId);
                                ClearRemovalAttempt(wish);
                                step = RemovalStep.Refused;
                            }
                            else if (!deadline.Fits(BotAccountRegistration.WriteClientTimeout))
                            {
                                ClearRemovalAttempt(wish);
                                step = RemovalStep.Aborted;
                            }
                            else
                            {
                                await deadline.ApplyAsync(db, cancellationToken);
                                session.MarkWriteSent();
                                sent = true;
                                var result = await writeClient.RemoveEmoteAsync(
                                    session.Token!, attempt.Evidence.SetId, attempt.Evidence.EmoteId, attempt.Evidence.Alias, cancellationToken);
                                logger.LogInformation("Wish bot: the removal of wish {WishId} came to {Outcome}.", item.WishId, result.Outcome);
                                unreportedPause = result.RetryAfter;
                                var definite = BookRemoveAnswer(session, locked, result, now);
                                readAfter = !definite;
                                changed = definite || unreportedPause is not null;
                                step = RemovalStep.Sendable;
                            }

                            break;
                    }
                }
            }

            // After a send the transaction runs to its commit even with nothing to book: a deadline that passed during
            // the call takes the expiry path, and the read after it waits for a later pass (K6e-3, K6f-1).
            if (!changed && !sent)
            {
                return (step, readAfter);
            }

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
            announce = step == RemovalStep.Booked ? locked.ChannelName : null;
        }
        catch (Exception ex) when (BotAccountLockDeadline.IsExpiry(ex, cancellationToken))
        {
            logger.LogWarning(
                "Wish bot: the send transaction of the removal of wish {WishId} ran out of its lock deadline or failed ({ExceptionType}){Sent}.",
                item.WishId, ex.GetType().Name, sent ? "; the attempt stays open for the resumption" : string.Empty);
            withdrawLater = !sent;
            readAfter = false;
            step = sent ? RemovalStep.Sendable : RemovalStep.Aborted;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        // A never-sent attempt the expired transaction could not take back: in a short transaction of its own, under the
        // same rule; failing that it stays open for the resumption.
        if (withdrawLater && await WithdrawRemovalAsync(session, item, attempt, unreportedPause, cancellationToken) == Booking.Fenced)
        {
            return (RemovalStep.Stop, false);
        }

        if (withdrawLater)
        {
            unreportedPause = null;
        }

        if (unreportedPause is { } pending)
        {
            await session.PauseAsync(pending, cancellationToken);
        }

        if (announce is not null)
        {
            await changedPublisher.PublishAsync(announce, cancellationToken);
        }

        return (sent ? RemovalStep.Sendable : step, readAfter);
    }

    // The checks behind the send transaction's locks (2.5a (c)).
    private RemovalRefusal RecheckRemoval(
        IBotAccountSession session, LockedWish locked, RemovalAttempt attempt, BotAccountLockDeadline deadline, DateTime now)
    {
        if (!attempt.Matches(locked.Wish))
        {
            return RemovalRefusal.AttemptGone;
        }

        if (locked.Settings is not { BotModeEnabled: true } settings)
        {
            return RemovalRefusal.ModeOff;
        }

        if (!string.Equals(locked.ActiveEmoteSetId, attempt.Evidence.SetId, StringComparison.Ordinal)
            || !string.Equals(locked.Wish.BotSetId, attempt.Evidence.SetId, StringComparison.Ordinal))
        {
            return RemovalRefusal.SetChanged;
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
            return RemovalRefusal.CredentialChanged;
        }

        if (preflight != WishBotPreflightOutcome.Pass)
        {
            return RemovalRefusal.Preflight;
        }

        // A read of at least one page and the write after it must fit (K6d-5).
        return deadline.PageTimeLimit(BotAccountRegistration.WriteClientTimeout, BotAccountRegistration.WriteClientTimeout) is null
            ? RemovalRefusal.NoTimeLeft
            : RemovalRefusal.None;
    }

    // The definite answers of removeEmote under the send transaction's locks (2.7 transaction 2); the pause of a RetryAfter
    // is the caller's. Ok, Timeout and Unavailable book nothing: the attempt stays for the read after it. True when the
    // wish or its channel changed.
    private bool BookRemoveAnswer(IBotAccountSession session, LockedWish locked, SevenTvWriteResult result, DateTime now)
    {
        switch (result.Outcome)
        {
            case SevenTvWriteOutcome.Rejected:
                ClearRemovalAttempt(locked.Wish);
                return true;
            case SevenTvWriteOutcome.LackingPrivileges:
                ClearRemovalAttempt(locked.Wish);
                if (locked.Settings is { } settings)
                {
                    Suspend(settings, locked.ChannelName, WishBotChannelReasons.NotEditor, now);
                }

                return true;
            case SevenTvWriteOutcome.Unauthorized:
                // K6b-3: reported, the queue stays, the next pass's verification decides.
                ClearRemovalAttempt(locked.Wish);
                session.ReportUnauthorized();
                return true;
            case SevenTvWriteOutcome.RateLimited:
                ClearRemovalAttempt(locked.Wish);
                return true;
            default:
                return false;
        }
    }

    // Conditional on the attempt being unchanged; a failure leaves it to the resumption.
    private Task<Booking> WithdrawRemovalAsync(
        IBotAccountSession session, WishBotQueueItem item, RemovalAttempt attempt, TimeSpan? pause, CancellationToken cancellationToken) =>
        BookWishAsync(session, item.ChannelId, item.WishId, pause, (locked, _) =>
        {
            if (!attempt.Matches(locked.Wish))
            {
                return false;
            }

            ClearRemovalAttempt(locked.Wish);
            return true;
        }, cancellationToken);

    // ------------------------------------------------------------------------------------ (3) the read after it

    private async Task<QueueStep> ReadAfterRemovalAsync(
        IBotAccountSession session, WishBotQueueItem item, RemovalAttempt attempt, CancellationToken cancellationToken)
    {
        if (await PreflightAsync(session, item.ChannelId, requireEditorProof: false, cancellationToken) is { } refused)
        {
            return refused;
        }

        var result = await writeClient.ReadSetEntriesAsync(session.Token!, attempt.Evidence.SetId, cancellationToken: cancellationToken);
        var pause = Report(session, result.Status);
        if (!await session.HoldsLockAsync(cancellationToken))
        {
            if (pause is { } pending)
            {
                await session.PauseAsync(pending, cancellationToken);
            }

            return QueueStep.Stop;
        }

        if (!result.IsOk || result.Value is not { } entries)
        {
            // The attempt stays open: the resumption of a later pass reads again (2.5a (e)).
            logger.LogInformation("Wish bot: the read after the removal of wish {WishId} came to {Outcome}; it is resumed later.", item.WishId, result.Outcome);
            if (pause is { } pending)
            {
                await session.PauseAsync(pending, cancellationToken);
            }

            return QueueStep.Continue;
        }

        if (attempt.Evidence.Classify(entries) == WishBotRemovalRead.Owned)
        {
            logger.LogInformation("Wish bot: the read after the removal of wish {WishId} still shows the bot's entry; it is resumed later.", item.WishId);
            return QueueStep.Continue;
        }

        var missing = attempt.Evidence.MissingOthers(entries);
        DateTime? phase = null;
        var booking = await BookWishAsync(session, item.ChannelId, item.WishId, pause, async (locked, now, deadline) =>
        {
            if (!attempt.Matches(locked.Wish))
            {
                return false;
            }

            await AuditSuspectRemovalAsync(locked, attempt.Evidence, entries, now, deadline, cancellationToken);
            if (missing.Count > 0)
            {
                phase = ToDatabasePrecision(now);
                ToRepairReview(locked.Wish, phase.Value, now);
                return true;
            }

            BookBotRemoval(locked, now, resumed: false);
            ClearRemovalEvidence(locked.Wish);
            return true;
        }, cancellationToken);

        if (booking == Booking.Fenced)
        {
            return QueueStep.Stop;
        }

        if (booking != Booking.Hit || phase is not { } repairPhase)
        {
            return QueueStep.Continue;
        }

        if (!await session.HoldsLockAsync(cancellationToken))
        {
            return QueueStep.Stop;
        }

        return await RepairAsync(session, item, repairPhase, attempt.Evidence, missing, cancellationToken);
    }

    // The detection limit (spec 8, AK77): a live local slot of the emote under the wish's alias of another generation than
    // the bot's, with no entry of that alias left after the removal, means the bot most likely removed a replacement a sync
    // had seen. A hint in the audit log, never a repair.
    private async Task AuditSuspectRemovalAsync(
        LockedWish locked,
        WishBotRemovalEvidence evidence,
        IReadOnlyList<SevenTvSetEntry> entries,
        DateTime now,
        BotAccountLockDeadline deadline,
        CancellationToken cancellationToken)
    {
        var wish = locked.Wish;
        if (entries.Any(e => string.Equals(e.Alias, evidence.Alias, StringComparison.Ordinal)))
        {
            return;
        }

        await deadline.ApplyAsync(db, cancellationToken);
        var slots = await db.EmoteSlots.AsNoTracking()
            .Where(s => !s.IsArchived
                && s.Name == evidence.Alias
                && s.AddedAt != null
                && s.Emote.ChannelId == wish.ChannelId
                && s.Emote.SevenTvEmoteId == evidence.EmoteId)
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.AddedAt })
            .ToListAsync(cancellationToken);
        var ownMicroseconds = ToDatabasePrecision(evidence.AddedAtUtc).Ticks;
        foreach (var slot in slots.Where(s => ToDatabasePrecision(s.AddedAt!.Value).Ticks != ownMicroseconds))
        {
            logger.LogWarning("Wish bot: the removal of wish {WishId} probably took a replacement the sync had seen as slot {SlotId}.", wish.Id, slot.Id);
            db.AddAuditEntry(
                AuditActor.System,
                AuditActions.WishBotSuspectRemoval,
                channelName: locked.ChannelName,
                targetType: "wish",
                targetId: wish.Id.ToString(CultureInfo.InvariantCulture),
                details: new { wishId = wish.Id, slotId = slot.Id },
                occurredAtUtc: now);
        }
    }

    // ------------------------------------------------------------------------------------ (4) the repair

    private async Task<QueueStep> RepairAsync(
        IBotAccountSession session,
        WishBotQueueItem item,
        DateTime phase,
        WishBotRemovalEvidence evidence,
        IReadOnlyList<WishBotRemovalOtherEntry> missing,
        CancellationToken cancellationToken)
    {
        // Anything that keeps the repair from being sent now leaves the phase unclear: it is resolved read only later.
        if (session.Token is null || !session.TokenUsable)
        {
            return QueueStep.Continue;
        }

        await session.WaitForWriteSlotAsync(cancellationToken);
        if (!await session.HoldsLockAsync(cancellationToken))
        {
            return QueueStep.Stop;
        }

        var sent = false;
        var credentialChanged = false;
        string? announce = null;
        TimeSpan? unreportedPause = null;
        var repaired = missing[0];
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deadline = new BotAccountLockDeadline(timeProvider);
            var (locking, locked) = await LockWishRowsAsync(session, item.ChannelId, item.WishId, deadline, cancellationToken);
            if (locking == Booking.Fenced)
            {
                session.ReportLockLost();
                return QueueStep.Stop;
            }

            if (locking != Booking.Hit)
            {
                return QueueStep.Continue;
            }

            var refusal = RecheckRepair(session, locked!, phase, evidence, deadline, Now());
            if (refusal != RemovalRefusal.None)
            {
                logger.LogInformation("Wish bot: the repair for wish {WishId} is not sent ({Refusal}); the phase is resolved read only.", item.WishId, refusal);
                credentialChanged = refusal == RemovalRefusal.CredentialChanged;
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                await deadline.ApplyAsync(db, cancellationToken);
                session.MarkWriteSent();
                sent = true;
                var result = await writeClient.AddEmoteAsync(session.Token!, evidence.SetId, evidence.EmoteId, repaired.Alias, cancellationToken);
                logger.LogInformation("Wish bot: the repair for wish {WishId} came to {Outcome}.", item.WishId, result.Outcome);
                unreportedPause = result.Status.RetryAfter;
                if (RepairOutcomeOf(result.Outcome) is { } outcome)
                {
                    var now = Now();
                    AuditRepair(locked!, outcome, now);
                    foreach (var _ in missing.Skip(1))
                    {
                        AuditRepair(locked!, RepairOutcomes.NeedsReview, now);
                    }

                    ClearRepairPhase(locked!.Wish);
                    if (result.Outcome == SevenTvWriteOutcome.LackingPrivileges && locked.Settings is { } settings)
                    {
                        Suspend(settings, locked.ChannelName, WishBotChannelReasons.NotEditor, now);
                    }

                    if (result.Outcome == SevenTvWriteOutcome.Unauthorized)
                    {
                        session.ReportUnauthorized();
                    }

                    announce = locked.ChannelName;
                }

                if (announce is not null || unreportedPause is not null)
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
                }
            }
        }
        catch (Exception ex) when (BotAccountLockDeadline.IsExpiry(ex, cancellationToken))
        {
            logger.LogWarning(
                "Wish bot: the repair transaction of wish {WishId} ran out of its lock deadline or failed ({ExceptionType}); the phase is resolved read only.",
                item.WishId, ex.GetType().Name);
            announce = null;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        if (unreportedPause is { } pending)
        {
            await session.PauseAsync(pending, cancellationToken);
        }

        if (announce is not null)
        {
            await changedPublisher.PublishAsync(announce, cancellationToken);
        }

        return credentialChanged || (sent && !await session.HoldsLockAsync(cancellationToken)) ? QueueStep.Stop : QueueStep.Continue;
    }

    private RemovalRefusal RecheckRepair(
        IBotAccountSession session, LockedWish locked, DateTime phase, WishBotRemovalEvidence evidence, BotAccountLockDeadline deadline, DateTime now)
    {
        var wish = locked.Wish;
        // Bound to the phase's stamp alone (K6d-1): nobody writes the evidence while the phase is open.
        if (wish.BotRepairAtUtc != phase || wish.BotRemoveEvidence is null)
        {
            return RemovalRefusal.AttemptGone;
        }

        if (locked.Settings is not { BotModeEnabled: true } settings)
        {
            return RemovalRefusal.ModeOff;
        }

        if (!string.Equals(locked.ActiveEmoteSetId, evidence.SetId, StringComparison.Ordinal))
        {
            return RemovalRefusal.SetChanged;
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
            return RemovalRefusal.CredentialChanged;
        }

        if (preflight != WishBotPreflightOutcome.Pass)
        {
            return RemovalRefusal.Preflight;
        }

        return deadline.Fits(BotAccountRegistration.WriteClientTimeout) ? RemovalRefusal.None : RemovalRefusal.NoTimeLeft;
    }

    // ------------------------------------------------------------------------------------ an open attempt (2.5a (e))

    private async Task<QueueStep> RunRemovalAttemptAsync(
        IBotAccountSession session, WishBotQueueItem item, CancellationToken cancellationToken)
    {
        var seen = await db.Wishes.AsNoTracking().FirstOrDefaultAsync(w => w.Id == item.WishId && w.ChannelId == item.ChannelId, cancellationToken);
        if (seen is not { BotRemoveAtUtc: { } stamp, BotAttemptVersion: { } version }
            || WishBotRemovalEvidence.TryParse(seen.BotRemoveEvidence) is not { } evidence)
        {
            // Without evidence nothing could be read: the deadline step resolves it.
            return QueueStep.Continue;
        }

        if (await PreflightAsync(session, item.ChannelId, requireEditorProof: false, cancellationToken) is { } refused)
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

        logger.LogWarning("Wish bot: wish {WishId} carries a removal attempt of an earlier pass; it is resolved by a read, never sent again.", seen.Id);
        var attempt = new RemovalAttempt(stamp, version, seen.Status, evidence);
        var result = await writeClient.ReadSetEntriesAsync(session.Token!, evidence.SetId, cancellationToken: cancellationToken);
        var pause = Report(session, result.Status);
        if (!await session.HoldsLockAsync(cancellationToken))
        {
            if (pause is { } pending)
            {
                await session.PauseAsync(pending, cancellationToken);
            }

            return QueueStep.Stop;
        }

        if (!result.IsOk || result.Value is not { } entries)
        {
            if (pause is { } pending)
            {
                await session.PauseAsync(pending, cancellationToken);
            }

            return QueueStep.Continue;
        }

        var stillThere = evidence.Classify(entries) == WishBotRemovalRead.Owned;
        var missing = evidence.MissingOthers(entries);
        var booking = await BookWishAsync(session, item.ChannelId, item.WishId, pause, async (locked, now, deadline) =>
        {
            var wish = locked.Wish;
            if (!attempt.Matches(wish))
            {
                return false;
            }

            if (stillThere)
            {
                // Not removed: the attempt goes, the queue stays (no decision counted).
                ClearRemovalAttempt(wish);
                return true;
            }

            await AuditSuspectRemovalAsync(locked, evidence, entries, now, deadline, cancellationToken);
            BookBotRemoval(locked, now, resumed: true);
            if (missing.Count > 0)
            {
                // Resolved read only later (2.5a (h)): never an addEmote from an unclear send.
                wish.BotRepairAtUtc = ToDatabasePrecision(now);
            }
            else
            {
                ClearRemovalEvidence(wish);
            }

            return true;
        }, cancellationToken);

        return booking == Booking.Fenced ? QueueStep.Stop : QueueStep.Continue;
    }

    // ------------------------------------------------------------------------------------ a repair check (2.5a (h))

    private async Task<QueueStep> RunRepairCheckAsync(
        IBotAccountSession session, WishBotQueueItem item, CancellationToken cancellationToken)
    {
        var seen = await db.Wishes.AsNoTracking().FirstOrDefaultAsync(w => w.Id == item.WishId && w.ChannelId == item.ChannelId, cancellationToken);
        if (seen is not { BotRepairAtUtc: { } phase, BotRemoveAtUtc: null }
            || WishBotRemovalEvidence.TryParse(seen.BotRemoveEvidence) is not { } evidence)
        {
            return QueueStep.Continue;
        }

        // A read needs a usable token and no kill switch or pause; the channel's state does not matter.
        if (session.Token is null || !SwitchesOn)
        {
            return QueueStep.Continue;
        }

        switch (BotAccountPreflight.Check(session.Account, session.Account.CredentialVersion, session.TokenUsable, Now()))
        {
            case BotAccountPreflightOutcome.Pass:
                break;
            case BotAccountPreflightOutcome.CredentialChanged:
                return QueueStep.Stop;
            default:
                return QueueStep.Continue;
        }

        if (!session.TryTakeAction())
        {
            return QueueStep.Exhausted;
        }

        if (!await session.HoldsLockAsync(cancellationToken))
        {
            return QueueStep.Stop;
        }

        var result = await writeClient.ReadSetEntriesAsync(session.Token!, evidence.SetId, cancellationToken: cancellationToken);
        var pause = Report(session, result.Status);
        if (!await session.HoldsLockAsync(cancellationToken))
        {
            if (pause is { } pending)
            {
                await session.PauseAsync(pending, cancellationToken);
            }

            return QueueStep.Stop;
        }

        if (!result.IsOk || result.Value is not { } entries)
        {
            // The phase stays; no deadline clears it.
            if (pause is { } pending)
            {
                await session.PauseAsync(pending, cancellationToken);
            }

            return QueueStep.Continue;
        }

        var nothingRemoved = evidence.Classify(entries) == WishBotRemovalRead.Owned;
        var missing = nothingRemoved ? [] : evidence.MissingOthers(entries);
        var booking = await BookWishAsync(session, item.ChannelId, item.WishId, pause, (locked, now) =>
        {
            // K6d-1: bound to the phase's stamp alone; DELETE …/wishes/bot, re-add and retry leave it standing.
            if (locked.Wish.BotRepairAtUtc != phase)
            {
                return false;
            }

            foreach (var _ in missing)
            {
                AuditRepair(locked, RepairOutcomes.NeedsReview, now);
            }

            ClearRepairPhase(locked.Wish);
            return true;
        }, cancellationToken);

        return booking == Booking.Fenced ? QueueStep.Stop : QueueStep.Continue;
    }

    // ------------------------------------------------------------------------------------ a stuck order

    private async Task<QueueStep> PresentStuckRemovalAsync(
        IBotAccountSession session, WishBotQueueItem item, WishBotRemovalGeneration generation, CancellationToken cancellationToken)
    {
        var aborts = _stuckRemovals.AbortsOf(generation);
        _stuckRemovals.Reset(item.WishId);
        var booking = await BookWishAsync(session, item.ChannelId, item.WishId, null, (locked, now) =>
        {
            var wish = locked.Wish;
            if (wish.RemovalQueuedVersion != generation.QueuedVersion
                || wish.RemovalQueuedAtUtc != generation.QueuedAtUtc
                || wish.DecisionVersion != generation.QueuedVersion
                || wish.BotRemoveAtUtc is not null
                || WishBotAttemptResolution.HasOpenAdd(wish))
            {
                return false;
            }

            return WishBotFallback.ApplyTo(wish, now);
        }, cancellationToken);

        if (booking == Booking.Hit)
        {
            logger.LogWarning(
                "Wish bot: the removal of wish {WishId} was aborted {Aborts} times in a row without a send; it goes to the managers.",
                item.WishId, aborts);
        }

        return booking == Booking.Fenced ? QueueStep.Stop : QueueStep.Continue;
    }

    // ------------------------------------------------------------------------------------ field rules

    // The bot removed its entry (2.7 transaction 3, 2.5a (e)): removed, one decision counted, queue and ownership gone,
    // the attempt cleared, wish.botRemove (with the overdue expiry, as K2 (e); outcome resumed for a resumption).
    private void BookBotRemoval(LockedWish locked, DateTime now, bool resumed)
    {
        var wish = locked.Wish;
        var previousExpiry = wish.ExpiresAtUtc is { } expiry && expiry <= now ? expiry : (DateTime?)null;
        wish.Status = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Remove)
            ?? throw new InvalidOperationException("The transition table does not allow removing this wish.");
        wish.StatusReason = null;
        wish.StatusChangedAtUtc = now;
        wish.EndedAtUtc = now;
        wish.DecisionVersion++;
        wish.RemovalQueuedAtUtc = null;
        wish.RemovalQueuedVersion = null;
        EndBotOwnership(wish);
        wish.BotRemoveAtUtc = null;
        wish.BotAttemptVersion = null;
        db.AddAuditEntry(
            AuditActor.System,
            AuditActions.WishBotRemove,
            channelName: locked.ChannelName,
            targetType: "wish",
            targetId: wish.Id.ToString(CultureInfo.InvariantCulture),
            details: RemoveDetailsOf(wish, resumed, previousExpiry),
            occurredAtUtc: now);
    }

    // The bot's entry was changed or renamed in 7TV, or the active set moved (spec 8): presented (ownership_lost), no send.
    private void OwnershipLost(Wish wish, DateTime now)
    {
        logger.LogInformation("Wish bot: wish {WishId} lost the bot's ownership before its removal; it goes to the managers.", wish.Id);
        if (string.Equals(wish.Status, WishStatus.Active, StringComparison.Ordinal))
        {
            wish.Status = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Present)!;
        }

        if (!string.Equals(wish.StatusReason, WishStatusReason.OwnershipLost, StringComparison.Ordinal))
        {
            wish.StatusChangedAtUtc = now;
        }

        wish.StatusReason = WishStatusReason.OwnershipLost;
        wish.DecisionVersion++;
        wish.RemovalQueuedAtUtc = null;
        wish.RemovalQueuedVersion = null;
        EndBotOwnership(wish);
    }

    // The alias is gone from the set (2.7 transaction 1 and 2): removed_externally like the reconcile books it.
    private void RemovedExternally(Wish wish, DateTime now)
    {
        logger.LogInformation("Wish bot: the entry of wish {WishId} was removed outside the bot.", wish.Id);
        wish.Status = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.ExternalRemoval)!;
        wish.StatusChangedAtUtc = now;
        wish.EndedAtUtc = now;
        wish.DecisionVersion++;
        wish.RemovalQueuedAtUtc = null;
        wish.RemovalQueuedVersion = null;
        EndBotOwnership(wish);
    }

    private void AuditRepair(LockedWish locked, string outcome, DateTime now) =>
        db.AddAuditEntry(
            AuditActor.System,
            AuditActions.WishBotRepair,
            channelName: locked.ChannelName,
            targetType: "wish",
            targetId: locked.Wish.Id.ToString(CultureInfo.InvariantCulture),
            details: new { wishId = locked.Wish.Id, sevenTvEmoteId = locked.Wish.SevenTvEmoteId, outcome },
            occurredAtUtc: now);

    // Spec 8 "Vor dem Schreiben erneut prüfen" (E25): queued at the decision version, active past its expiry or presented
    // with an order, owned, the start authoritative.
    private static bool RemovalEligible(Wish wish, DateTime now) =>
        wish.RemovalQueuedAtUtc is not null
        && wish.RemovalQueuedVersion == wish.DecisionVersion
        && ((string.Equals(wish.Status, WishStatus.Active, StringComparison.Ordinal) && wish.ExpiresAtUtc is { } expiresAt && expiresAt <= now)
            || string.Equals(wish.Status, WishStatus.Presented, StringComparison.Ordinal))
        && wish.BotOwned
        && wish.BotOwnedAddedAtUtc is not null
        && wish.StartsAtAuthoritative;

    // Another entry of the emote went with the bot's (2.7 "Reparatur"): presented (repair_review), queue empty, the phase
    // with the evidence kept; the bot's ownership ended with its entry.
    private static void ToRepairReview(Wish wish, DateTime phase, DateTime now)
    {
        if (string.Equals(wish.Status, WishStatus.Active, StringComparison.Ordinal))
        {
            wish.Status = WishTransitions.Target(wish.Status, wish.StatusReason, WishAction.Present)!;
        }

        if (!string.Equals(wish.StatusReason, WishStatusReason.RepairReview, StringComparison.Ordinal))
        {
            wish.StatusChangedAtUtc = now;
        }

        wish.StatusReason = WishStatusReason.RepairReview;
        wish.DecisionVersion++;
        wish.RemovalQueuedAtUtc = null;
        wish.RemovalQueuedVersion = null;
        EndBotOwnership(wish);
        wish.BotRemoveAtUtc = null;
        wish.BotAttemptVersion = null;
        wish.BotRepairAtUtc = phase;
    }

    private static void EndBotOwnership(Wish wish)
    {
        wish.BotOwned = false;
        wish.BotOwnedAddedAtUtc = null;
        wish.BotSetId = null;
    }

    // "Versuch leeren" for a removal: the attempt and its evidence go, the queue stays, no decision counted.
    private static void ClearRemovalAttempt(Wish wish)
    {
        wish.BotRemoveAtUtc = null;
        wish.BotAttemptVersion = null;
        ClearRemovalEvidence(wish);
    }

    // The evidence goes only where no repair phase holds it (2.5a (h)).
    private static void ClearRemovalEvidence(Wish wish)
    {
        if (wish.BotRepairAtUtc is null)
        {
            wish.BotRemoveEvidence = null;
        }
    }

    private static void ClearRepairPhase(Wish wish)
    {
        wish.BotRepairAtUtc = null;
        if (wish.BotRemoveAtUtc is null)
        {
            wish.BotRemoveEvidence = null;
        }
    }

    // wish.botRemove's details (ids, never the alias); outcome and previousExpiresAtUtc only where they apply.
    private static Dictionary<string, object> RemoveDetailsOf(Wish wish, bool resumed, DateTime? previousExpiry)
    {
        var details = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["wishId"] = wish.Id,
            ["sevenTvEmoteId"] = wish.SevenTvEmoteId,
        };
        if (resumed)
        {
            details["outcome"] = "resumed";
        }

        if (previousExpiry is { } expiry)
        {
            details["previousExpiresAtUtc"] = expiry;
        }

        return details;
    }

    // The definite answers of the repair's addEmote (2.5a (h)); null for an unclear one (the phase stays).
    private static string? RepairOutcomeOf(SevenTvWriteOutcome outcome) => outcome switch
    {
        SevenTvWriteOutcome.Ok => RepairOutcomes.Ok,
        SevenTvWriteOutcome.Rejected => RepairOutcomes.Rejected,
        SevenTvWriteOutcome.LackingPrivileges => RepairOutcomes.LackingPrivileges,
        SevenTvWriteOutcome.Unauthorized => RepairOutcomes.Unauthorized,
        SevenTvWriteOutcome.RateLimited => RepairOutcomes.RateLimited,
        _ => null,
    };

    // A read that failed counts towards a stuck order unless the account has its own way for it (2.7).
    private static bool Counts(SevenTvWriteOutcome outcome) =>
        outcome is not (SevenTvWriteOutcome.Unauthorized or SevenTvWriteOutcome.RateLimited);

    // The time a read under the locks may still take: until the budget end (F − 3 s), less what the write after it needs.
    private static Func<TimeSpan> HttpTimeLeft(BotAccountLockDeadline deadline, TimeSpan reserve) =>
        () => deadline.Remaining - BotAccountLockDeadline.BookingReserve - reserve;

    private enum RemovalStep
    {
        // Nothing changed and nothing counts (gone, resolved elsewhere, a repair phase first, the fallback's channel).
        Untouched,

        // The attempt is committed, or the removal was sent: go on.
        Sendable,

        // Booked an end (removed_externally, ownership_lost, not eligible): the count ends.
        Booked,

        // Not sent for a reason with its own way (Unauthorized, RateLimited, the preflight, changed evidence): the count ends.
        Refused,

        // Not sent at a read or a deadline: counts towards a stuck order.
        Aborted,

        // The credential changed under the send: the pass ends.
        CredentialChanged,

        // The lock or the epoch is lost: the pass ends.
        Stop,
    }

    private enum RemovalRefusal
    {
        None,

        // The attempt is no longer this one: nothing to take back.
        AttemptGone,
        ModeOff,
        SetChanged,
        CredentialChanged,
        Preflight,
        NoTimeLeft,
    }

    // The values of wish.botRepair's outcome.
    private static class RepairOutcomes
    {
        public const string Ok = "ok";
        public const string Rejected = "rejected";
        public const string LackingPrivileges = "lacking_privileges";
        public const string Unauthorized = "unauthorized";
        public const string RateLimited = "rate_limited";
        public const string NeedsReview = "needs_review";
    }

    // The attempt as committed in transaction 1: its stamp, the decision version it is bound to, the status it was
    // committed in and its evidence.
    private sealed record RemovalAttempt(DateTime StampUtc, int Version, string Status, WishBotRemovalEvidence Evidence)
    {
        public bool Matches(Wish wish) =>
            wish.BotRemoveAtUtc == StampUtc
            && wish.BotAttemptVersion == Version
            && wish.DecisionVersion == Version
            && string.Equals(wish.Status, Status, StringComparison.Ordinal);
    }
}
