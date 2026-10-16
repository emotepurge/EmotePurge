namespace EmotePurge.Core.Entities;

/// <summary>Severity of a captured log entry; the values equal Microsoft.Extensions.Logging's LogLevel (Core does not reference it).</summary>
public enum LogEntryLevel : short
{
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}
