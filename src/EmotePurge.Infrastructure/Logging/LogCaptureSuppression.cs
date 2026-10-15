namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// Execution-context switch that makes the capture sink drop everything logged inside it, silently and
/// without counting a drop: the writer's own loop and the viewer's read paths run under it so their EF,
/// Npgsql and Redis diagnostics are never captured (recursion layers 2 and 4). It flows with the
/// <see cref="ExecutionContext"/>, so tasks started inside a scope are suppressed too; scopes nest.
/// </summary>
public static class LogCaptureSuppression
{
    private static readonly AsyncLocal<int> Depth = new();

    public static bool IsActive => Depth.Value > 0;

    public static IDisposable Enter()
    {
        var previous = Depth.Value;
        Depth.Value = previous + 1;
        return new Scope(previous);
    }

    private sealed class Scope(int previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Depth.Value = previous;
            }
        }
    }
}
