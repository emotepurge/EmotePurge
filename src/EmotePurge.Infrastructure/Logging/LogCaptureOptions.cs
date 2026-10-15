namespace EmotePurge.Infrastructure.Logging;

/// <summary>Configuration section <c>LogCapture</c>.</summary>
public sealed class LogCaptureOptions
{
    public const string SectionName = "LogCapture";

    /// <summary>When <c>false</c> no sink and no buffer are registered, only a status heartbeat.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Entries admitted per process and UTC day; Information stops at 80 %, the rest is kept for Warning and above.</summary>
    public int DailyEntryBudget { get; set; } = 20000;
}
