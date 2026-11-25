using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

// Step 2 of the bot pass (#487 plan 2.5b, 2.9; T8c): open acceptance attempts are resolved by an editor read for their
// bound bot id, activation requests are confirmed or ended, and an open invitation is accepted for a request (case (a))
// or an activated channel (case (b)) only ([B] F7). What to do is WishBotInvitationPolicy's; this file reads, sends and
// books.
//
// Reads go through IBotAccountEditorCheck (unauthenticated, one per minute and bot id), the lock probed before and after
// each. A resolution, a confirmation or the end of a request is one short booking: channel row (FOR NO KEY UPDATE), then
// the credential row FOR SHARE with the runner epoch (the fence), then the settings row; the policy decides again on that
// row and on the account as the fence shows it, so the booking is bound to the identity and bot id the read was made for
// (plan 2.3), and nothing is booked unless it decides the same.
//
// An acceptance (plan 2.5b "Ablauf"): budget and write spacing first, no lock (K6c-4); the lock probe; the attempt stamp in
// a committed transaction of its own (S6-1); the probe again; then the send transaction: channel row FOR SHARE (the first
// lock; RowLocks.LockChannelsForShareByIdsAsync, K6b-6), settings, the fence; the re-check (stamp unchanged and at most
// 30 s old, the request or the activation unchanged, the consent bound to the current account, coverage, the account
// stages, the HTTP budget of the lock deadline); MarkWriteSent; exactly one 7TV call; the booking of its answer under the
// same locks; the probe after. A "no" before sending takes the never-sent attempt back in a short transaction of its own.
// Every transaction with row locks runs under a BotAccountLockDeadline (K6e-3, K6f-1); an expired one, or any database
// error, leaves what may have been sent open for the read to resolve.
public sealed partial class WishBotPass
{
    // How often step 2 decides again for one channel in one pass (a resolution, then a confirmation; a fresh read).
    private const int MaxDecisionsPerChannel = 6;

    private bool SwitchesOn => wishOptions.Enabled && botAccountOptions.Enabled;

    private TimeSpan EditorVisibility => TimeSpan.FromSeconds(botAccountOptions.EditorVisibilitySeconds);

    // Continue: on to step 3. Stop: the pass ends without the fallback (lock or epoch lost, or the credential changed
    // between the preflight and the send, plan 2.5 revision 3).
    private async Task<PassStep> RunInvitationsAsync(
        IBotAccountSession session, BotAccountAvailabilityState account, CancellationToken cancellationToken)
    {
        var candidates = await db.ChannelWishSettings.AsNoTracking()
            .Where(s => s.BotInvitationAttemptAtUtc != null || s.BotModeEnabled || s.BotActivationRequestedAtUtc != null)
            .OrderBy(s => s.ChannelId)
            .Select(s => new { s.ChannelId, s.Channel!.TwitchChannelId })
            .ToListAsync(cancellationToken);

        var reads = new EditorReads(editorCheck, session, account.IdentityVersion, logger);
        foreach (var candidate in candidates)
        {
            if (await HandleChannelAsync(session, account, candidate.ChannelId, candidate.TwitchChannelId, reads, cancellationToken)
                == PassStep.Stop)
            {
                return PassStep.Stop;
            }
        }

        return PassStep.Continue;
    }

    private async Task<PassStep> HandleChannelAsync(
        IBotAccountSession session,
        BotAccountAvailabilityState account,
        string channelId,
        string? twitchChannelId,
        EditorReads reads,
        CancellationToken cancellationToken)
    {
        var accepted = false;
        var readAgain = false;
        DateTime? firstRequest = null;
        for (var round = 0; round < MaxDecisionsPerChannel; round++)
        {
            var settings = await db.ChannelWishSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
            if (settings is null)
            {
                return PassStep.Continue;
            }

            // The rounds of one pass decide on the request of the first load (plan 2.9 "Bestätigung im Worker"): a read
            // made in a round (the forced one, the one after an acceptance) began for it. A request that shows up later
            // with another stamp was made anew meanwhile; it waits for a decision of its own in the next pass.
            if (round == 0)
            {
                firstRequest = settings.BotActivationRequestedAtUtc;
            }
            else if (settings.BotActivationRequestedAtUtc is { } request && request != firstRequest)
            {
                return PassStep.Continue;
            }

            var covered = CoveredForInvitation(settings, twitchChannelId);
            BotAccountEditorRead? read = null;
            if (ReadIdFor(settings, account, covered) is { } readId)
            {
                read = await reads.GetAsync(readId, startedAfterUtc: null, cancellationToken);
                if (read is null)
                {
                    return PassStep.Stop;
                }
            }

            var decision = WishBotInvitationPolicy.Decide(settings, twitchChannelId, account, covered, read, EditorVisibility, Now());
            switch (decision.Action)
            {
                case WishBotInvitationAction.None:
                    return PassStep.Continue;

                case WishBotInvitationAction.ReadAgain:
                    // A cached read began too early to decide a negative answer: one fresh read, never a loop of them.
                    if (readAgain)
                    {
                        return PassStep.Continue;
                    }

                    if (await reads.GetAsync(read!.BotSevenTvUserId, decision.ReadStartedAfterUtc, cancellationToken) is null)
                    {
                        return PassStep.Stop;
                    }

                    readAgain = true;
                    continue;

                case WishBotInvitationAction.Accept:
                    if (accepted)
                    {
                        return PassStep.Continue;
                    }

                    accepted = true;
                    var sent = await AcceptAsync(session, settings, twitchChannelId, covered, read!, decision, cancellationToken);
                    if (sent.Step == PassStep.Stop)
                    {
                        return PassStep.Stop;
                    }

                    if (sent.OkAtUtc is not { } stamp)
                    {
                        return PassStep.Continue;
                    }

                    // After Ok one fresh read in the same pass (plan 2.5b): it resolves the attempt and confirms or resumes.
                    if (await reads.GetAsync(read!.BotSevenTvUserId, stamp, cancellationToken) is null)
                    {
                        return PassStep.Stop;
                    }

                    continue;

                default:
                    var booking = await BookDecisionAsync(
                        session, channelId, twitchChannelId, read, decision, settings.BotActivationRequestedAtUtc, cancellationToken);
                    if (booking == Booking.Fenced)
                    {
                        session.ReportLockLost();
                        return PassStep.Stop;
                    }

                    if (booking != Booking.Hit
                        || decision.Action is WishBotInvitationAction.Confirm or WishBotInvitationAction.EndRequest)
                    {
                        return PassStep.Continue;
                    }

                    continue;
            }
        }

        return PassStep.Continue;
    }

    // E8: the pilot list, or a global admin by id: the activator of an activation, the requester of a request.
    private bool CoveredForInvitation(ChannelWishSettings settings, string? twitchChannelId)
    {
        if (wishOptions.PilotList.Covers(twitchChannelId))
        {
            return true;
        }

        return settings.BotModeEnabled
            ? WishBotChannelStateReader.ActivatorIsAdminById(admins, settings)
            : settings.BotActivationRequestedByTwitchUserId is { Length: > 0 } requester && admins.IsAdminById(requester);
    }

    // ------------------------------------------------------------------------------------ Bookings without 7TV

    // Resolution, confirmation, end of a request: decided again behind the locks, on the account as the fence shows it,
    // and bound to the request the decision was made for (plan 2.9 "Bestätigung im Worker", Sol-P2-6): a request cancelled
    // and made anew while the read ran is another request, which this read never checked.
    private async Task<Booking> BookDecisionAsync(
        IBotAccountSession session,
        string channelId,
        string? twitchChannelId,
        BotAccountEditorRead? read,
        WishBotInvitationDecision expected,
        DateTime? checkedRequestAtUtc,
        CancellationToken cancellationToken)
    {
        string? announce = null;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deadline = new BotAccountLockDeadline(timeProvider);
            await deadline.ApplyAsync(db, cancellationToken);
            if ((await db.LockChannelsByIdsAsync([channelId], cancellationToken)).Count == 0)
            {
                return Booking.Missed;
            }

            deadline.Start();
            await deadline.ApplyAsync(db, cancellationToken);
            var row = await db.LockBotAccountForShareAsync(session.RunnerEpoch, cancellationToken);
            if (row is null)
            {
                logger.LogWarning("Bot account: runner epoch {Epoch} was superseded; this pass books nothing more.", session.RunnerEpoch);
                return Booking.Fenced;
            }

            await deadline.ApplyAsync(db, cancellationToken);
            var settings = await db.ChannelWishSettings.FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
            await deadline.ApplyAsync(db, cancellationToken);
            var channelName = await db.Channels.AsNoTracking()
                .Where(c => c.Id == channelId)
                .Select(c => c.ChannelName)
                .FirstOrDefaultAsync(cancellationToken);
            if (settings is null || channelName is null || settings.BotActivationRequestedAtUtc != checkedRequestAtUtc)
            {
                return Booking.Missed;
            }

            var now = Now();
            var lockedAccount = BotAccountAvailabilityState.Of(
                SwitchesOn, row.Status, row.ExpiresAtUtc, row.KillSwitch, row.BotSevenTvUserId, row.IdentityVersion, now);
            var decision = WishBotInvitationPolicy.Decide(
                settings, twitchChannelId, lockedAccount, CoveredForInvitation(settings, twitchChannelId), read, EditorVisibility, now);
            if (decision != expected with { OwnerSevenTvUserId = decision.OwnerSevenTvUserId })
            {
                return Booking.Missed;
            }

            var visible = expected.Action switch
            {
                WishBotInvitationAction.ResolveAccepted => await ResolveAcceptedAsync(settings, channelName, decision, now, deadline, cancellationToken),
                WishBotInvitationAction.ResolveNotAccepted => ResolveNotAccepted(settings, channelName, decision, now),
                WishBotInvitationAction.Confirm => Confirm(settings, channelName, read!, now),
                WishBotInvitationAction.EndRequest => EndRequest(settings, channelName, decision.Error!),
                _ => throw new InvalidOperationException($"No booking for {expected.Action}."),
            };

            await deadline.ApplyAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await deadline.ApplyAsync(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            announce = visible ? channelName : null;
        }
        catch (Exception ex) when (BotAccountLockDeadline.IsExpiry(ex, cancellationToken))
        {
            logger.LogWarning(
                "Bot account: a booking for channel {ChannelId} ran out of its lock deadline or failed ({ExceptionType}); nothing was booked.",
                channelId, ex.GetType().Name);
            return Booking.Expired;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        if (announce is not null)
        {
            await changedPublisher.PublishAsync(announce, cancellationToken);
        }

        return Booking.Hit;
    }

    // The acceptance took effect (also ACCEPTED without "manage", K6d-2): attempt and a Rejected wait cleared. One
    // wish.botAcceptInvitation for the attempt, written here only when none was booked since its stamp (its answer was
    // unclear: resolved: true). Consent ended meanwhile → the bot became an editor without consent:
    // invitation_accepted_after_abort, binding cleared. Returns whether the managers see a change.
    private async Task<bool> ResolveAcceptedAsync(
        ChannelWishSettings settings,
        string channelName,
        WishBotInvitationDecision decision,
        DateTime now,
        BotAccountLockDeadline deadline,
        CancellationToken cancellationToken)
    {
        var attemptAt = settings.BotInvitationAttemptAtUtc!.Value;
        var trigger = WishBotInvitationPolicy.TriggerOf(settings);
        settings.BotInvitationAttemptAtUtc = null;
        settings.BotInvitationRetryAtUtc = null;

        await deadline.ApplyAsync(db, cancellationToken);
        var booked = await db.AuditLogEntries.AsNoTracking()
            .AnyAsync(
                a => a.Action == AuditActions.WishBotAcceptInvitation && a.ChannelName == channelName && a.OccurredAtUtc >= attemptAt,
                cancellationToken);

        if (decision.ConsentGone)
        {
            settings.BotActivationError = WishBotChannelReasons.InvitationAcceptedAfterAbort;
            settings.BotConsentSevenTvUserId = null;
            if (!booked)
            {
                db.AddAuditEntry(
                    AuditActor.System, AuditActions.WishBotAcceptInvitation, channelName: channelName,
                    details: new { resolved = true, afterAbort = true }, occurredAtUtc: now);
            }

            logger.LogWarning(
                "Bot account: an acceptance in channel {ChannelName} took effect after its request was cancelled; the bot is an editor there without the bot mode.",
                channelName);
            return true;
        }

        if (!booked)
        {
            db.AddAuditEntry(
                AuditActor.System, AuditActions.WishBotAcceptInvitation, channelName: channelName,
                details: new { trigger, resolved = true }, occurredAtUtc: now);
        }

        return false;
    }

    // The acceptance did not take effect, decided by a read that began after the attempt's window. Consent ended meanwhile
    // → attempt, error, binding and wait cleared, one wish.botInvitationUnresolved (K6e-1). The attempt ended in Rejected →
    // "not accepted" like LackingPrivileges: the request ends (invitation_not_accepted), an activated channel is
    // invitation_pending, the wait stays. Otherwise only the attempt goes; the policy then decides as without one.
    private bool ResolveNotAccepted(ChannelWishSettings settings, string channelName, WishBotInvitationDecision decision, DateTime now)
    {
        var attemptAt = settings.BotInvitationAttemptAtUtc!.Value;
        settings.BotInvitationAttemptAtUtc = null;
        if (decision.ConsentGone)
        {
            settings.BotActivationError = null;
            settings.BotConsentSevenTvUserId = null;
            settings.BotInvitationRetryAtUtc = null;
            db.AddAuditEntry(
                AuditActor.System, AuditActions.WishBotInvitationUnresolved, channelName: channelName,
                details: new { attemptedAtUtc = attemptAt }, occurredAtUtc: now);
            return true;
        }

        if (!decision.EndedInRejected)
        {
            return false;
        }

        if (settings.BotModeEnabled)
        {
            return Suspend(settings, channelName, WishBotChannelReasons.InvitationPending, now);
        }

        return EndRequest(settings, channelName, WishBotChannelReasons.InvitationNotAccepted);
    }

    // Plan 2.9: the bot mode is on, the activator is the requester, the editor proof is the read's (bound to its identity,
    // which the policy matched against the fence's row), the request and a wait are cleared; wish.botActivate under the
    // requester (the decision is theirs, the worker only confirms).
    private bool Confirm(ChannelWishSettings settings, string channelName, BotAccountEditorRead read, DateTime now)
    {
        settings.BotModeEnabled = true;
        settings.BotModeActivatedByTwitchUserId = settings.BotActivationRequestedByTwitchUserId;
        settings.BotModeActivatedByLogin = settings.BotActivationRequestedByLogin;
        settings.BotModeActivatedAtUtc = settings.BotActivationRequestedAtUtc;
        settings.BotEditorCheckedAtUtc = read.StartedAtUtc;
        settings.BotEditorCheckedIdentityVersion = read.IdentityVersion;
        settings.BotSuspendedReason = null;
        settings.BotActivationError = null;
        settings.BotInvitationRetryAtUtc = null;
        var actor = new AuditActor(settings.BotActivationRequestedByTwitchUserId ?? string.Empty, settings.BotActivationRequestedByLogin ?? string.Empty);
        ClearRequest(settings);
        db.AddAuditEntry(actor, AuditActions.WishBotActivate, channelName: channelName, occurredAtUtc: now);
        logger.LogInformation("Bot account: the bot mode of channel {ChannelName} is confirmed.", channelName);
        return true;
    }

    private bool EndRequest(ChannelWishSettings settings, string channelName, string error)
    {
        ClearRequest(settings);
        settings.BotActivationError = error;
        logger.LogInformation("Bot account: the activation request of channel {ChannelName} ended ({Error}).", channelName, error);
        return true;
    }

    // One wish.botSuspend per change of the reason (plan 2.8).
    private bool Suspend(ChannelWishSettings settings, string channelName, string reason, DateTime now)
    {
        if (string.Equals(settings.BotSuspendedReason, reason, StringComparison.Ordinal))
        {
            return false;
        }

        settings.BotSuspendedReason = reason;
        db.AddAuditEntry(AuditActor.System, AuditActions.WishBotSuspend, channelName: channelName, details: new { reason }, occurredAtUtc: now);
        logger.LogInformation("Bot account: channel {ChannelName} suspended ({Reason}).", channelName, reason);
        return true;
    }

    // ------------------------------------------------------------------------------------ The acceptance

    private async Task<AcceptResult> AcceptAsync(
        IBotAccountSession session,
        ChannelWishSettings settings,
        string? twitchChannelId,
        bool covered,
        BotAccountEditorRead read,
        WishBotInvitationDecision decision,
        CancellationToken cancellationToken)
    {
        // The account stages from the session, no lock yet: nothing is stamped that could not be sent anyway.
        var preflight = WishBotPreflight.Check(
            SwitchesOn, covered, settings, session.Account, session.Account.CredentialVersion, session.TokenUsable, Now(), requireEditorProof: false);
        if (preflight != WishBotPreflightOutcome.Pass || session.Token is null)
        {
            return AcceptResult.NotSent;
        }

        // Budget and spacing before the stamp (K6c-4): the wait never eats into the 30 s between stamp and send.
        if (!session.TryTakeAction())
        {
            return AcceptResult.NotSent;
        }

        await session.WaitForWriteSlotAsync(cancellationToken);
        if (!await session.HoldsLockAsync(cancellationToken))
        {
            return AcceptResult.Stop;
        }

        var stamped = await StampAttemptAsync(
            session, settings.ChannelId, twitchChannelId, read, decision, settings.BotActivationRequestedAtUtc, cancellationToken);
        if (stamped.Booking == Booking.Fenced)
        {
            session.ReportLockLost();
            return AcceptResult.Stop;
        }

        if (stamped.Attempt is not { } attempt)
        {
            return AcceptResult.NotSent;
        }

        // From here the attempt is durable: a crash or a lost lock leaves it to the read (S6-1), it is never sent blind.
        if (!await session.HoldsLockAsync(cancellationToken))
        {
            return AcceptResult.Stop;
        }

        var sent = await SendAsync(session, settings.ChannelId, twitchChannelId, attempt, decision.OwnerSevenTvUserId!, cancellationToken);
        if (sent.Step == PassStep.Stop || !await session.HoldsLockAsync(cancellationToken))
        {
            return AcceptResult.Stop;
        }

        return sent;
    }

    // The attempt stamp, committed on its own (S6-1), conditional on the situation the policy just decided on and on the
    // request it was decided for (Sol-P2-6).
    private async Task<(Booking Booking, StampedAttempt? Attempt)> StampAttemptAsync(
        IBotAccountSession session,
        string channelId,
        string? twitchChannelId,
        BotAccountEditorRead read,
        WishBotInvitationDecision expected,
        DateTime? checkedRequestAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deadline = new BotAccountLockDeadline(timeProvider);
            await deadline.ApplyAsync(db, cancellationToken);
            if ((await db.LockChannelsByIdsAsync([channelId], cancellationToken)).Count == 0)
            {
                return (Booking.Missed, null);
            }

            deadline.Start();
            await deadline.ApplyAsync(db, cancellationToken);
            var row = await db.LockBotAccountForShareAsync(session.RunnerEpoch, cancellationToken);
            if (row is null)
            {
                logger.LogWarning("Bot account: runner epoch {Epoch} was superseded; this pass books nothing more.", session.RunnerEpoch);
                return (Booking.Fenced, null);
            }

            await deadline.ApplyAsync(db, cancellationToken);
            var settings = await db.ChannelWishSettings.FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
            if (settings is null || settings.BotActivationRequestedAtUtc != checkedRequestAtUtc)
            {
                return (Booking.Missed, null);
            }

            var now = Now();
            var lockedAccount = BotAccountAvailabilityState.Of(
                SwitchesOn, row.Status, row.ExpiresAtUtc, row.KillSwitch, row.BotSevenTvUserId, row.IdentityVersion, now);
            var decision = WishBotInvitationPolicy.Decide(
                settings, twitchChannelId, lockedAccount, CoveredForInvitation(settings, twitchChannelId), read, EditorVisibility, now);
            if (decision != expected)
            {
                return (Booking.Missed, null);
            }

            var stamp = ToDatabasePrecision(now);
            settings.BotInvitationAttemptAtUtc = stamp;
            var attempt = new StampedAttempt(stamp, settings.BotModeEnabled, settings.BotActivationRequestedAtUtc, settings.BotConsentSevenTvUserId!);
            await deadline.ApplyAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await deadline.ApplyAsync(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (Booking.Hit, attempt);
        }
        catch (Exception ex) when (BotAccountLockDeadline.IsExpiry(ex, cancellationToken))
        {
            // Unclear whether the stamp committed: then it stays open and the read resolves it; it was never sent.
            logger.LogWarning(
                "Bot account: recording an acceptance attempt for channel {ChannelId} ran out of its lock deadline or failed ({ExceptionType}).",
                channelId, ex.GetType().Name);
            return (Booking.Expired, null);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task<AcceptResult> SendAsync(
        IBotAccountSession session,
        string channelId,
        string? twitchChannelId,
        StampedAttempt attempt,
        string ownerSevenTvUserId,
        CancellationToken cancellationToken)
    {
        var sent = false;
        var refusal = Refusal.None;
        string? announce = null;
        DateTime? okAt = null;
        TimeSpan? unreportedPause = null;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deadline = new BotAccountLockDeadline(timeProvider);
            await deadline.ApplyAsync(db, cancellationToken);
            if ((await db.LockChannelsForShareByIdsAsync([channelId], cancellationToken)).Count == 0)
            {
                return AcceptResult.NotSent;
            }

            deadline.Start();
            await deadline.ApplyAsync(db, cancellationToken);
            var settings = await db.ChannelWishSettings.FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
            await deadline.ApplyAsync(db, cancellationToken);
            var channelName = await db.Channels.AsNoTracking()
                .Where(c => c.Id == channelId)
                .Select(c => c.ChannelName)
                .FirstOrDefaultAsync(cancellationToken);
            await deadline.ApplyAsync(db, cancellationToken);
            var row = await db.LockBotAccountForShareAsync(session.RunnerEpoch, cancellationToken);
            if (row is null)
            {
                logger.LogWarning("Bot account: runner epoch {Epoch} was superseded; this pass books nothing more.", session.RunnerEpoch);
                session.ReportLockLost();
                return AcceptResult.Stop;
            }

            var now = Now();
            refusal = Recheck(session, settings, channelName, twitchChannelId, row, attempt, deadline, now);
            if (refusal != Refusal.None)
            {
                logger.LogInformation(
                    "Bot account: the acceptance for channel {ChannelId} is not sent ({Refusal}).", channelId, refusal);
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                await deadline.ApplyAsync(db, cancellationToken);
                session.MarkWriteSent();
                sent = true;
                var result = await writeClient.AcceptEditorInvitationAsync(
                    session.Token!, attempt.BotSevenTvUserId, ownerSevenTvUserId, cancellationToken);
                logger.LogInformation(
                    "Bot account: the acceptance for channel {ChannelId} came to {Outcome}.", channelId, result.Outcome);

                var visible = BookAcceptance(session, settings!, channelName!, attempt, result);

                // Plan 2.5a (i), K6c-3, K6e-4: a RetryAfter (RateLimited, or an Ok with a rate limit below it) pauses the
                // account inside this transaction, under its deadline. Should the deadline run out first, the pause is
                // reported after the rollback instead (below), never lost.
                unreportedPause = result.RetryAfter;
                if (result.RetryAfter is { } retryAfter)
                {
                    await deadline.ApplyAsync(db, cancellationToken);
                    unreportedPause = null;
                    await session.PauseAsync(retryAfter, cancellationToken);
                }

                await deadline.ApplyAsync(db, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await deadline.ApplyAsync(db, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                announce = visible ? channelName : null;
                okAt = result.IsOk ? attempt.AtUtc : null;
            }
        }
        catch (Exception ex) when (BotAccountLockDeadline.IsExpiry(ex, cancellationToken))
        {
            logger.LogWarning(
                "Bot account: the send transaction of an acceptance for channel {ChannelId} ran out of its lock deadline or failed ({ExceptionType}){Sent}.",
                channelId, ex.GetType().Name, sent ? "; the attempt stays open for the read" : string.Empty);
            refusal = sent ? Refusal.None : Refusal.Expired;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        if (!sent && refusal != Refusal.None)
        {
            await WithdrawAttemptAsync(session, channelId, attempt.AtUtc, cancellationToken);
        }

        if (unreportedPause is { } pause)
        {
            // No transaction open any more: the session books it in a fenced transaction of its own.
            await session.PauseAsync(pause, cancellationToken);
        }

        if (announce is not null)
        {
            await changedPublisher.PublishAsync(announce, cancellationToken);
        }

        if (refusal == Refusal.CredentialChanged)
        {
            return AcceptResult.Stop;
        }

        return sent ? new AcceptResult(PassStep.Continue, okAt) : AcceptResult.NotSent;
    }

    // The checks behind the send transaction's locks (plan 2.5b "Ablauf"; K6c-4, K6d-5).
    private Refusal Recheck(
        IBotAccountSession session,
        ChannelWishSettings? settings,
        string? channelName,
        string? twitchChannelId,
        BotAccountCredential row,
        StampedAttempt attempt,
        BotAccountLockDeadline deadline,
        DateTime now)
    {
        if (settings is null || channelName is null || settings.BotInvitationAttemptAtUtc != attempt.AtUtc)
        {
            return Refusal.AttemptChanged;
        }

        var situationHolds = attempt.Activated
            ? settings.BotModeEnabled
            : !settings.BotModeEnabled && settings.BotActivationRequestedAtUtc == attempt.RequestedAtUtc;
        if (!situationHolds)
        {
            return Refusal.RequestChanged;
        }

        var account = new BotAccountSnapshot(
            row.Status, row.ExpiresAtUtc, row.BotSevenTvUserId, row.KillSwitch, row.PausedUntilUtc, row.CredentialVersion, row.IdentityVersion);
        var preflight = WishBotPreflight.Check(
            SwitchesOn,
            CoveredForInvitation(settings, twitchChannelId),
            settings,
            account,
            session.Account.CredentialVersion,
            session.TokenUsable,
            now,
            requireEditorProof: false);
        if (preflight == WishBotPreflightOutcome.CredentialChanged)
        {
            return Refusal.CredentialChanged;
        }

        if (preflight != WishBotPreflightOutcome.Pass
            || !string.Equals(settings.BotConsentSevenTvUserId, attempt.BotSevenTvUserId, StringComparison.Ordinal))
        {
            return Refusal.Preflight;
        }

        if (now - attempt.AtUtc > WishBotInvitationPolicy.SendWindow)
        {
            return Refusal.TooOld;
        }

        return deadline.Fits(BotAccountRegistration.WriteClientTimeout) ? Refusal.None : Refusal.NoTimeLeft;
    }

    // The booking of the answer, under the send transaction's locks (plan 2.5b "Ergebnisse der Annahme", 2.5a (i)); the
    // pause of a RetryAfter is the caller's. Returns whether the managers see a change.
    private bool BookAcceptance(
        IBotAccountSession session,
        ChannelWishSettings settings,
        string channelName,
        StampedAttempt attempt,
        SevenTvWriteResult result)
    {
        var now = Now();
        switch (result.Outcome)
        {
            case SevenTvWriteOutcome.Ok:
                // The attempt stays open until a read resolves it; the audit marks it as booked (no second one then).
                db.AddAuditEntry(
                    AuditActor.System, AuditActions.WishBotAcceptInvitation, channelName: channelName,
                    details: new { trigger = attempt.Activated ? WishBotInvitationPolicy.TriggerActivated : WishBotInvitationPolicy.TriggerActivation },
                    occurredAtUtc: now);
                return false;

            case SevenTvWriteOutcome.RateLimited:
                settings.BotInvitationAttemptAtUtc = null;
                return false;

            case SevenTvWriteOutcome.Unauthorized:
                // Plan K6b-3: reported, not booked as invalid; the next pass's verification decides.
                settings.BotInvitationAttemptAtUtc = null;
                session.ReportUnauthorized();
                return false;

            case SevenTvWriteOutcome.LackingPrivileges:
                settings.BotInvitationAttemptAtUtc = null;
                if (attempt.Activated)
                {
                    settings.BotInvitationRetryAtUtc = ToDatabasePrecision(now + WishBotInvitationPolicy.RetryWait);
                    return Suspend(settings, channelName, WishBotChannelReasons.InvitationPending, now);
                }

                return EndRequest(settings, channelName, WishBotChannelReasons.InvitationNotAccepted);

            case SevenTvWriteOutcome.Rejected:
                // K6b-1: not proof of a refusal (7TV answers an already accepted invitation this way); the attempt stays
                // open for the read, the wait marks "ended in Rejected" durably.
                settings.BotInvitationRetryAtUtc = ToDatabasePrecision(now + WishBotInvitationPolicy.RetryWait);
                return false;

            default:
                // Timeout, Unavailable: unclear, the attempt stays open for the read.
                return false;
        }
    }

    // Takes back an attempt that was provably never sent (2.5a (c)), in a short transaction of its own under the same
    // rule. Where the consent ended meanwhile (a DELETE while the attempt was open) the row returns to what that DELETE
    // would have left without an attempt: binding, wait and invitation_accept_unresolved cleared. A failure leaves the
    // attempt open for the read.
    private async Task WithdrawAttemptAsync(IBotAccountSession session, string channelId, DateTime attemptAt, CancellationToken cancellationToken)
    {
        string? announce = null;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deadline = new BotAccountLockDeadline(timeProvider);
            await deadline.ApplyAsync(db, cancellationToken);
            if ((await db.LockChannelsByIdsAsync([channelId], cancellationToken)).Count == 0)
            {
                return;
            }

            deadline.Start();
            await deadline.ApplyAsync(db, cancellationToken);
            if (await db.LockBotAccountForShareAsync(session.RunnerEpoch, cancellationToken) is null)
            {
                logger.LogWarning("Bot account: runner epoch {Epoch} was superseded; this pass books nothing more.", session.RunnerEpoch);
                session.ReportLockLost();
                return;
            }

            await deadline.ApplyAsync(db, cancellationToken);
            var settings = await db.ChannelWishSettings.FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
            if (settings is null || settings.BotInvitationAttemptAtUtc != attemptAt)
            {
                return;
            }

            settings.BotInvitationAttemptAtUtc = null;
            if (!WishBotInvitationPolicy.ConsentStands(settings))
            {
                settings.BotConsentSevenTvUserId = null;
                settings.BotInvitationRetryAtUtc = null;
                if (settings.BotActivationError == WishBotChannelReasons.InvitationAcceptUnresolved)
                {
                    settings.BotActivationError = null;
                    await deadline.ApplyAsync(db, cancellationToken);
                    announce = await db.Channels.AsNoTracking()
                        .Where(c => c.Id == channelId)
                        .Select(c => c.ChannelName)
                        .FirstOrDefaultAsync(cancellationToken);
                }
            }

            await deadline.ApplyAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await deadline.ApplyAsync(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (BotAccountLockDeadline.IsExpiry(ex, cancellationToken))
        {
            announce = null;
            logger.LogWarning(
                "Bot account: taking back a never sent acceptance attempt for channel {ChannelId} failed ({ExceptionType}); the read resolves it.",
                channelId, ex.GetType().Name);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        if (announce is not null)
        {
            await changedPublisher.PublishAsync(announce, cancellationToken);
        }
    }

    // Which bot id's read the policy needs for this channel, if any: the bound id while an attempt is open (also with
    // the account unavailable, plan 2.5b), else the current one where a request or an activation could use it.
    private static string? ReadIdFor(ChannelWishSettings settings, BotAccountAvailabilityState account, bool covered)
    {
        if (settings.BotInvitationAttemptAtUtc is not null)
        {
            return settings.BotConsentSevenTvUserId;
        }

        return account.IsAvailable
            && covered
            && (settings.BotModeEnabled || settings.BotActivationRequestedAtUtc is not null)
            && WishBotPreflight.ConsentApplies(settings, account.BotSevenTvUserId)
                ? account.BotSevenTvUserId
                : null;
    }

    private static void ClearRequest(ChannelWishSettings settings)
    {
        settings.BotActivationRequestedAtUtc = null;
        settings.BotActivationRequestedByTwitchUserId = null;
        settings.BotActivationRequestedByLogin = null;
    }

    // Postgres keeps microseconds; trimmed so the stamp reads back equal and the conditions on it match.
    private static DateTime ToDatabasePrecision(DateTime value)
    {
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
    }

    private enum PassStep
    {
        Continue,
        Stop,
    }

    private enum Refusal
    {
        None,
        AttemptChanged,
        RequestChanged,
        CredentialChanged,
        Preflight,
        TooOld,
        NoTimeLeft,
        Expired,
    }

    // What an acceptance came to: whether the pass goes on, and the attempt's stamp when 7TV answered Ok and it was booked.
    private readonly record struct AcceptResult(PassStep Step, DateTime? OkAtUtc)
    {
        public static AcceptResult NotSent { get; } = new(PassStep.Continue, null);

        public static AcceptResult Stop { get; } = new(PassStep.Stop, null);
    }

    // The attempt as stamped: its instant, which case it is (an activation, or the request of that stamp), its bound id.
    private sealed record StampedAttempt(DateTime AtUtc, bool Activated, DateTime? RequestedAtUtc, string BotSevenTvUserId);

    // The reads of one pass per bot id, each a 7TV call between two lock probes; IBotAccountEditorCheck reuses a read for a
    // minute. Null from GetAsync: the lock is lost.
    private sealed class EditorReads(IBotAccountEditorCheck editorCheck, IBotAccountSession session, int identityVersion, ILogger logger)
    {
        private readonly Dictionary<string, BotAccountEditorRead> _reads = new(StringComparer.Ordinal);

        public async Task<BotAccountEditorRead?> GetAsync(string botSevenTvUserId, DateTime? startedAfterUtc, CancellationToken cancellationToken)
        {
            if (startedAfterUtc is null && _reads.TryGetValue(botSevenTvUserId, out var known))
            {
                return known;
            }

            if (!await session.HoldsLockAsync(cancellationToken))
            {
                return null;
            }

            var read = await editorCheck.ReadAsync(botSevenTvUserId, identityVersion, startedAfterUtc, cancellationToken);
            if (!await session.HoldsLockAsync(cancellationToken))
            {
                return null;
            }

            if (!read.IsAvailable)
            {
                logger.LogInformation("Bot account: the editor read is unavailable; open attempts stay open, requests wait.");
            }

            _reads[botSevenTvUserId] = read;
            return read;
        }
    }
}
