namespace EmotePurge.Core.ChatLogArchive;

/// <summary>
/// Reads exactly one channel-day of chat history from a third-party log archive, streaming
/// <see cref="ChatLogMessage"/>s to a callback instead of buffering the whole (potentially
/// hundreds-of-MB) response body.
/// <para>
/// The implementation (<c>ChatLogArchiveClient</c> in <c>EmotePurge.Infrastructure</c>) is
/// strictly sequential by contract: at most one call may be in flight at a time, and it enforces
/// a minimum delay between the start of consecutive requests. A second, concurrent call while one
/// is already running is a caller error, not something this interface guards against.
/// </para>
/// </summary>
public interface IChatLogArchiveClient
{
    /// <summary>
    /// Fetches one channel-day and invokes <paramref name="onMessage"/> once per PRIVMSG line, in
    /// the order the lines appear in the archive. <paramref name="maxBytes"/> is a hard cap on the
    /// response body; exceeding it aborts the read with
    /// <see cref="ChatLogDayStatus.ByteCapExceeded"/> instead of continuing. Cancelling
    /// <paramref name="ct"/> while the body is being read returns
    /// <see cref="ChatLogDayStatus.Cancelled"/> with the bytes received so far; cancelling it before
    /// the body throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ChatLogDayResult> ReadDayAsync(
        string twitchChannelId,
        DateOnly day,
        long maxBytes,
        Func<ChatLogMessage, ValueTask> onMessage,
        CancellationToken ct);
}
