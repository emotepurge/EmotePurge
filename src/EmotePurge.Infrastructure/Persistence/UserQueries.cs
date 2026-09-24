using EmotePurge.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>The two row-lock strengths <see cref="UserQueries.LockUserAsync"/> takes.</summary>
internal enum UserRowLock
{
    /// <summary>
    /// <c>FOR SHARE</c>: for a writer that only needs the row to stay put until it commits (an audit
    /// entry naming the user). Several can hold it at once; it blocks a deletion, and waits for one.
    /// </summary>
    ForShare,

    /// <summary><c>FOR UPDATE</c>: for the account deletion, which excludes every other locker.</summary>
    ForUpdate
}

/// <summary>
/// Row-locking lookups for <see cref="User"/> — the counterpart of <see cref="ChannelQueries"/> for the
/// places where a deletion can race a write naming the same user (data-retention plan, "Zeilensperren
/// statt Hoffnung"). The ordinary unlocked loaders stay the norm for every read-only path.
/// </summary>
internal static class UserQueries
{
    /// <summary>
    /// Loads the user and locks the row until the surrounding transaction ends, or returns <c>null</c>
    /// when there is no row — including when a deletion that held the lock committed while this call
    /// waited for it: under READ COMMITTED, Postgres re-checks a row it had to wait for and skips it
    /// if it is gone. Tracked, so a caller can remove or modify what it loaded.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No transaction is open on <paramref name="db"/>. A row lock outside an explicit transaction is
    /// released the moment its autocommit statement ends — it would look like a guard and guard nothing.
    /// </exception>
    public static async Task<User?> LockUserAsync(
        this AppDbContext db, string twitchUserId, UserRowLock mode, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("LockUserAsync needs an open transaction; the row lock would be released immediately otherwise.");
        }

        // Two literal statements rather than a spliced lock clause, so FromSql keeps parameterising the
        // id. Materialised with ToListAsync and nothing composed on top: EF would otherwise wrap the
        // statement in a subquery, and a locking clause is best kept on the outermost SELECT.
        var query = mode == UserRowLock.ForUpdate
            ? db.Users.FromSql($"""SELECT * FROM "Users" WHERE "Id" = {twitchUserId} FOR UPDATE""")
            : db.Users.FromSql($"""SELECT * FROM "Users" WHERE "Id" = {twitchUserId} FOR SHARE""");

        var rows = await query.ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }
}
