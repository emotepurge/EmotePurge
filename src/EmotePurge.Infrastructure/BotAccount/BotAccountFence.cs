using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// The runner epoch's fence (plan #487 2.5a (g), R2-3): every runner transaction that books or sends reads the credential
/// row <c>FOR SHARE</c> with its own epoch. A successor raises the epoch right after it took the advisory lock; that
/// update waits for this share lock, so a send transaction of the predecessor ends completely before the successor's
/// first step, and afterwards every fence of the predecessor fails, even on an intact working connection.
/// <para>
/// In the lock order of the bot's send transactions (plan 2.5a (c)) the credential row comes last: channel row
/// <c>FOR SHARE</c>, then the wish row <c>FOR UPDATE</c>, then this. The kill switch and the credential version the send
/// checks are read from the row this returns.
/// </para>
/// </summary>
internal static class BotAccountFence
{
    /// <summary>
    /// Locks the credential row <c>FOR SHARE</c> and returns it (untracked) while its <c>RunnerEpoch</c> is
    /// <paramref name="runnerEpoch"/>; <c>null</c> when the epoch moved on (or the row is gone): then the caller rolls back
    /// and ends its pass. Needs an open transaction, like every row lock.
    /// </summary>
    public static async Task<BotAccountCredential?> LockBotAccountForShareAsync(
        this AppDbContext db, long runnerEpoch, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The runner epoch fence needs an open transaction.");
        }

        // Materialised with ToListAsync and nothing composed on top, so FOR SHARE stays on the outermost SELECT.
        var rows = await db.BotAccountCredentials
            .FromSql(
                $"""SELECT * FROM "BotAccountCredentials" WHERE "Id" = {BotAccountCredential.SingletonId} AND "RunnerEpoch" = {runnerEpoch} FOR SHARE""")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }
}
