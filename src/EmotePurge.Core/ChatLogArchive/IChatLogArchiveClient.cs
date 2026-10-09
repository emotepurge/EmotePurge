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
    /// response body, enforced while the bytes arrive rather than per line; exceeding it aborts the
    /// read with <see cref="ChatLogDayStatus.ByteCapExceeded"/> instead of continuing. Cancelling
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

    /// <summary>
    /// Fetches the UTC range <c>[fromUtc, toUtcExclusive)</c> of one channel in a single request and
    /// invokes <paramref name="onMessage"/> once per PRIVMSG line, in archive order. Same contract as
    /// <see cref="ReadDayAsync"/> for the byte cap, cancellation and the sequential pacing, with
    /// three differences: no body digest is computed, a line longer than the configured maximum ends
    /// the read with <see cref="ChatLogDayStatus.LineTooLong"/> (memory per call is bounded by one
    /// line, not by the body), and a 429 carries the parsed <c>Retry-After</c> in
    /// <see cref="ChatLogRangeResult.RetryAfter"/>. Both bounds are taken as UTC instants and sent
    /// with second precision.
    /// </summary>
    Task<ChatLogRangeResult> ReadRangeAsync(
        string twitchChannelId,
        DateTime fromUtc,
        DateTime toUtcExclusive,
        long maxBytes,
        Func<ChatLogMessage, ValueTask> onMessage,
        CancellationToken ct);
}
