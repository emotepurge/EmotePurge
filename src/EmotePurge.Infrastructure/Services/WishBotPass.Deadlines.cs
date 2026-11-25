using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

// Step 0 of the bot pass, the deadline step (#487 plan 2.5, R2-7; T9 the add part, T10 the removal part): every pass,
// before the lock is tried, local only (no token, no 7TV, no pause, no budget, no fence). An open add past its
// confirmation deadline (WishBotConfirmSchedule.DeadlineOf: unconfirmed with BotConfirmDeadlineAtUtc reached, or a crash
// remnant whose write is older than the window) is resolved without 7TV by WishBotAttemptResolution: presented
// (unconfirmed), BotSetId, BotWriteAtUtc and BotAddAckAtUtc kept for recheck. Like the fallback run only the wish row is
// locked (never the channel row, so no cycle with the send transactions, which take channel then wish), each wish in a
// short transaction of its own under a BotAccountLockDeadline (K6e-3), re-read and re-decided behind the lock; a row gone
// by then is nothing to do (K6b-17). The decision version stays (2.5a (e)). One wish.changed per channel after its last
// commit.
public sealed partial class WishBotPass
{
    public async Task RunDeadlineStepAsync(CancellationToken cancellationToken = default)
    {
        var now = Now();
        var due = await DeadlineCandidates(db.Wishes.AsNoTracking(), now)
            .OrderBy(w => w.Id)
            .Select(w => new { w.Id, w.ChannelId })
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return;
        }

        var changedChannels = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var wish in due)
        {
            if (await ResolveDeadlineAsync(wish.Id, cancellationToken))
            {
                changedChannels.Add(wish.ChannelId);
            }
        }

        foreach (var channelId in changedChannels)
        {
            var channelName = await db.Channels.AsNoTracking()
                .Where(c => c.Id == channelId)
                .Select(c => c.ChannelName)
                .FirstOrDefaultAsync(cancellationToken);
            if (channelName is not null)
            {
                await changedPublisher.PublishAsync(channelName, cancellationToken);
            }
        }
    }

    // The wishes whose deadline may have passed; the row decides again behind its lock (IsDeadlineDue). T10 adds the open
    // removal attempts (BotRemoveAtUtc + 15 min) here and in IsDeadlineDue.
    private static IQueryable<Wish> DeadlineCandidates(IQueryable<Wish> wishes, DateTime nowUtc)
    {
        var writtenBefore = nowUtc - WishLimits.BotConfirmWindow;
        return wishes.Where(w =>
            (w.Status == WishStatus.Unconfirmed
                && (w.BotConfirmDeadlineAtUtc <= nowUtc || (w.BotConfirmDeadlineAtUtc == null && w.BotWriteAtUtc <= writtenBefore)))
            || (w.Status == WishStatus.Approved && w.BotWriteAtUtc != null && w.BotWriteAtUtc <= writtenBefore));
    }

    private static bool IsDeadlineDue(Wish wish, DateTime nowUtc) =>
        WishBotConfirmSchedule.DeadlineOf(wish) is { } deadline && deadline <= nowUtc;

    // True when the wish changed and committed.
    private async Task<bool> ResolveDeadlineAsync(long wishId, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deadline = new BotAccountLockDeadline(timeProvider);
            await deadline.ApplyAsync(db, cancellationToken);
            if ((await db.LockWishesByIdsAsync([wishId], cancellationToken)).Count == 0)
            {
                return false;
            }

            deadline.Start();
            await deadline.ApplyAsync(db, cancellationToken);
            var wish = await db.Wishes.FirstOrDefaultAsync(w => w.Id == wishId, cancellationToken);
            var now = Now();
            if (wish is null || !IsDeadlineDue(wish, now) || !WishBotAttemptResolution.Apply(wish, now))
            {
                return false;
            }

            await deadline.ApplyAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await deadline.ApplyAsync(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Wish bot: wish {WishId} passed its confirmation deadline and goes to the managers.", wishId);
            return true;
        }
        catch (Exception ex) when (BotAccountLockDeadline.IsExpiry(ex, cancellationToken))
        {
            logger.LogWarning(
                "Wish bot: the deadline step for wish {WishId} ran out of its lock deadline or failed ({ExceptionType}); the next pass retries it.",
                wishId, ex.GetType().Name);
            return false;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}
