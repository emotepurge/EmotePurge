namespace EmotePurge.Core.Entities;

/// <summary>A saved, global log filter (name plus filter string).</summary>
public class LogFilter
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Normalized <see cref="Name"/> (<see cref="LogFilterNames.NormalizeKey"/>); unique.</summary>
    public string NameKey { get; set; } = string.Empty;

    public string Filter { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
