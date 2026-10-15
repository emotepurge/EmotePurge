namespace EmotePurge.Infrastructure.Logging.Query;

/// <summary>
/// Options of the log viewer's read paths, section <c>LogViewer</c>. <see cref="QueryTimeoutMilliseconds"/> is
/// the statement time limit of every log query (spec 8.4: 5 s). It exists as a setting only so a test or a
/// local acceptance run can force timeouts (AK 20, <c>LogViewer__QueryTimeoutMilliseconds=1</c>); it is
/// deliberately not in the compose files.
/// </summary>
public sealed class LogViewerOptions
{
    public const string SectionName = "LogViewer";

    public int QueryTimeoutMilliseconds { get; set; } = 5000;

    /// <summary>Throws unless the time limit is usable; called at startup so a typo stops the container with a readable message.</summary>
    public void Validate()
    {
        if (QueryTimeoutMilliseconds <= 0)
        {
            throw new InvalidOperationException(
                $"Invalid log viewer configuration: '{SectionName}:{nameof(QueryTimeoutMilliseconds)}' must be greater than 0, but is {QueryTimeoutMilliseconds}.");
        }
    }
}
