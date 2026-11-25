using System.Globalization;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// The lock deadline of one runner transaction that takes row locks (plan #487 2.5a (c); Revision 6e K6e-3, 6f K6f-1),
/// next to <see cref="BotAccountFence"/>. From the acquisition of the transaction's <b>first</b> row lock a monotonic
/// deadline runs, <b>F = acquisition + 28 s</b> (<see cref="TimeProvider.GetTimestamp"/>, never the wall clock), so every
/// writer waiting for those rows (the full sync, the channel routes, <c>PUT /kill-switch</c>) stays under Npgsql's 30 s
/// command timeout.
/// <list type="bullet">
/// <item><b>Before every statement</b> of the transaction, <b>before every HTTP call under the locks</b> and <b>before the
/// commit</b> the caller runs <see cref="ApplyAsync"/>: one statement that sets <c>statement_timeout</c> and
/// <c>idle_in_transaction_session_timeout</c> per <c>SET LOCAL</c> (<c>set_config(…, true)</c>). Before the first lock
/// they are 10 s and 28 s (the first lock waits at most 10 s, it holds nothing before); after it both are the time left
/// until F, so Postgres counts any lock wait into it, and a client that hangs idle (a stub without its time limit, the
/// thread pool, a GC) has its session ended by the server at F, which frees every lock. With nothing left it throws
/// <see cref="BotAccountLockDeadlineExceededException"/> without sending anything. Postgres 16 has no
/// <c>transaction_timeout</c> (17+), hence the idle timeout as the backstop.</item>
/// <item><b>HTTP</b> under the locks must end, with its full time limit, by <b>F − 3 s</b> (<see cref="Fits"/>; 25 s
/// after the first lock): the 3 s are left for booking and commit. A paginated read gives each page the smaller of its
/// time limit and the time left (<see cref="PageTimeLimit"/>), and none under 1 s.</item>
/// <item><b>Expiry</b> (<see cref="IsExpiry"/>): this deadline, a statement timeout, a session the server ended, any
/// other database error on the way and an error at the commit itself all take the same path: roll back, take nothing
/// as booked (every booking is conditional, 2.5a (f)), resolve what may have been sent by a read (2.5a (e), 2.5b).</item>
/// </list>
/// The lock probe never runs while this deadline's locks are held (K6f-1): before the transaction and after its end.
/// Pure apart from <see cref="ApplyAsync"/>; one instance per transaction.
/// </summary>
internal sealed class BotAccountLockDeadline(TimeProvider clock)
{
    /// <summary>F: from the first lock to the end of the transaction (28 s).</summary>
    public static readonly TimeSpan Span = TimeSpan.FromSeconds(28);

    /// <summary>How long the first lock may wait (10 s).</summary>
    public static readonly TimeSpan FirstLockWait = TimeSpan.FromSeconds(10);

    /// <summary>What every HTTP call under the locks leaves for booking and commit (3 s): it must end by F − 3 s.</summary>
    public static readonly TimeSpan BookingReserve = TimeSpan.FromSeconds(3);

    /// <summary>The budget for HTTP under the locks from the first lock on (25 s, plan K6d-5).</summary>
    public static readonly TimeSpan HttpBudget = Span - BookingReserve;

    /// <summary>The shortest time limit a page of a read under the locks is still requested with (K6e-5).</summary>
    public static readonly TimeSpan MinimumPageTimeLimit = TimeSpan.FromSeconds(1);

    // A statement timeout of 0 would switch the limit off: below one millisecond nothing is sent any more.
    private static readonly TimeSpan Resolution = TimeSpan.FromMilliseconds(1);

    private long? _firstLockAt;

    /// <summary>Whether the first lock was acquired (<see cref="Start"/>).</summary>
    public bool IsStarted => _firstLockAt is not null;

    /// <summary>The time left until F; the whole <see cref="Span"/> before the first lock, never negative.</summary>
    public TimeSpan Remaining
    {
        get
        {
            if (_firstLockAt is not { } start)
            {
                return Span;
            }

            var left = Span - clock.GetElapsedTime(start);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Whether the deadline is used up: after the first lock, less than a millisecond left.</summary>
    public bool HasExpired => IsStarted && Remaining < Resolution;

    /// <summary>
    /// Marks the acquisition of the first lock; call it right after that lock's statement returned. Monotone: a later
    /// call keeps the first instant.
    /// </summary>
    public void Start() => _firstLockAt ??= clock.GetTimestamp();

    /// <summary>
    /// Whether an HTTP call with <paramref name="timeLimit"/> still ends by F − 3 s. Before the first lock the whole budget
    /// counts.
    /// </summary>
    public bool Fits(TimeSpan timeLimit) => timeLimit <= Remaining - BookingReserve;

    /// <summary>
    /// The time limit for the next page of a read under the locks: the smaller of <paramref name="perRequestLimit"/> and the
    /// time left until F − 3 s − <paramref name="reserve"/> (room for a write after the read); null when that is under
    /// <see cref="MinimumPageTimeLimit"/> and no page may be requested any more.
    /// </summary>
    public TimeSpan? PageTimeLimit(TimeSpan perRequestLimit, TimeSpan reserve = default)
    {
        var left = Remaining - BookingReserve - reserve;
        var limit = left < perRequestLimit ? left : perRequestLimit;
        return limit >= MinimumPageTimeLimit ? limit : null;
    }

    /// <summary>
    /// The two timeouts <see cref="ApplyAsync"/> sets: before the first lock 10 s for the statement and 28 s idle, after it
    /// both the time left until F.
    /// </summary>
    public (TimeSpan Statement, TimeSpan Idle) Timeouts()
    {
        if (!IsStarted)
        {
            return (FirstLockWait, Span);
        }

        var left = Remaining;
        return (left, left);
    }

    /// <summary>
    /// Sets both timeouts for the next statement, the next HTTP call under the locks or the commit (one statement, local
    /// to the open transaction). Throws <see cref="BotAccountLockDeadlineExceededException"/> without sending anything when
    /// the deadline is used up; needs an open transaction.
    /// </summary>
    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A lock deadline applies to an open transaction.");
        }

        if (HasExpired)
        {
            throw new BotAccountLockDeadlineExceededException();
        }

        var (statement, idle) = Timeouts();
        var statementTimeout = Milliseconds(statement);
        var idleTimeout = Milliseconds(idle);
        await db.Database.ExecuteSqlAsync(
            $"SELECT set_config('statement_timeout', {statementTimeout}, true), set_config('idle_in_transaction_session_timeout', {idleTimeout}, true)",
            cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="exception"/> takes the path of an expired deadline: this deadline, or any database error
    /// (a statement timeout, a session the server ended, a broken connection, an error at the commit). Never the caller's
    /// own cancellation.
    /// </summary>
    public static bool IsExpiry(Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is BotAccountLockDeadlineExceededException or NpgsqlException)
            {
                return true;
            }
        }

        return false;
    }

    // Whole milliseconds, at least one: a zero would mean "no limit" to Postgres.
    private static string Milliseconds(TimeSpan value) =>
        Math.Max(1L, (long)value.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";
}

/// <summary>The lock deadline of a runner transaction is used up (<see cref="BotAccountLockDeadline"/>); nothing was sent.</summary>
internal sealed class BotAccountLockDeadlineExceededException()
    : Exception("The lock deadline of the bot account's transaction is used up.");
