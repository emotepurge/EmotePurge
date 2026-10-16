using System.ComponentModel;
using System.Diagnostics;
using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// Who wrote a captured entry: the source, a random per-process instance id in the format of
/// <c>WorkerIdentity.InstanceId</c> (8 hex characters; the two ids differ on purpose) and the start time
/// of the operating-system process, which in a container is practically the container start.
/// </summary>
public sealed class LogCaptureIdentity(LogSource source)
{
    public LogSource Source { get; } = source;

    public string InstanceId { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// UTC, cut to whole microseconds so the value read back from a <c>timestamptz</c> column equals the
    /// in-memory one (status rows and <c>instance:latest</c> compare it).
    /// </summary>
    public DateTime ProcessStartedUtc { get; } = ReadProcessStart();

    private static DateTime ReadProcessStart()
    {
        DateTime started;
        try
        {
            using var process = Process.GetCurrentProcess();
            started = process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            started = DateTime.UtcNow;
        }

        return new DateTime(started.Ticks - (started.Ticks % 10), DateTimeKind.Utc);
    }
}
