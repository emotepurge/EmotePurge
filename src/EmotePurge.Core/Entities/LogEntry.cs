namespace EmotePurge.Core.Entities;

/// <summary>One captured application log line (Information and above), kept for seven days.</summary>
public class LogEntry
{
    public long Id { get; set; }

    /// <summary>Stable producer-side id; makes writing idempotent.</summary>
    public Guid CaptureId { get; set; }

    public DateTime TimestampUtc { get; set; }
    public LogSource Source { get; set; }
    public string InstanceId { get; set; } = string.Empty;
    public DateTime ProcessStartedUtc { get; set; }
    public LogEntryLevel Level { get; set; }
    public string Category { get; set; } = string.Empty;
    public int? EventId { get; set; }
    public string? EventName { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ExceptionType { get; set; }
    public string? Exception { get; set; }

    /// <summary>JSON object text (stored as jsonb); Core stays BCL-only.</summary>
    public string? Properties { get; set; }

    public bool Truncated { get; set; }
}
