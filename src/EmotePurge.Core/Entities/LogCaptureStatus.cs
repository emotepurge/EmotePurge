namespace EmotePurge.Core.Entities;

/// <summary>Heartbeat row of one process instance of the log capture (key: source + instance).</summary>
public class LogCaptureStatus
{
    public LogSource Source { get; set; }
    public string InstanceId { get; set; } = string.Empty;
    public DateTime ProcessStartedUtc { get; set; }
    public DateTime LastFlushUtc { get; set; }
    public bool CaptureEnabled { get; set; }

    /// <summary>Latest moment from which error capture of this process is not gapless; set at process start, only ever raised.</summary>
    public DateTime LastIncompleteAtUtc { get; set; }

    public DateTime? CleanShutdownAtUtc { get; set; }
}
