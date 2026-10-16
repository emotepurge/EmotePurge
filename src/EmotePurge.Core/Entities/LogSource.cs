namespace EmotePurge.Core.Entities;

/// <summary>The process that produced a captured log entry. Deliberately not <see cref="SystemLoadSource"/>.</summary>
public enum LogSource : short
{
    Api = 1,
    Worker = 2,
}
