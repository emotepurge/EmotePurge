using Npgsql;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>Classifies database failures the services retry on.</summary>
internal static class DatabaseErrors
{
    /// <summary>
    /// True for a Postgres deadlock (40P01). It surfaces either straight from a raw or locking query
    /// (<see cref="PostgresException"/>) or from a save (<c>DbUpdateException</c> around it), so the
    /// whole inner-exception chain is searched.
    /// </summary>
    public static bool IsDeadlock(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })
            {
                return true;
            }
        }

        return false;
    }
}
