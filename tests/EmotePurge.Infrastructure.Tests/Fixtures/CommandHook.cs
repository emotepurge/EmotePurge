using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EmotePurge.Infrastructure.Tests.Fixtures;

/// <summary>
/// Records every command text and runs <c>afterMatch</c> right after each execution of a matching
/// command (1-based occurrence) — a query or a non-query such as a raw upsert — inside the caller's own
/// flow, so a contender acts at exactly that point while the caller's transaction and locks are held.
/// </summary>
public sealed class CommandHook(Func<string, bool> match, Func<int, Task> afterMatch) : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();
    private int _matches;

    public int Count(Func<string, bool> predicate) => _commands.Count(predicate);

    /// <summary>The 0-based position of the first recorded command matching <paramref name="predicate"/>, or -1.</summary>
    public int IndexOf(Func<string, bool> predicate) => _commands.ToList().FindIndex(c => predicate(c));

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        await RecordAsync(command.CommandText);
        return result;
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await RecordAsync(command.CommandText);
        return result;
    }

    private async Task RecordAsync(string commandText)
    {
        _commands.Enqueue(commandText);
        if (match(commandText))
        {
            await afterMatch(Interlocked.Increment(ref _matches));
        }
    }
}
