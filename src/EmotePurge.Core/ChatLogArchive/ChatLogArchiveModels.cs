namespace EmotePurge.Core.ChatLogArchive;

/// <summary>
/// One PRIVMSG line from the archive, already IRCv3-tag-decoded and ACTION-unwrapped the same way
/// the live path's TwitchLib <c>ChatMessage</c> presents it to <c>TwitchChatManager</c>.
/// <paramref name="UserId"/> and the entries of <paramref name="Badges"/> are nullable/possibly
/// empty on purpose: the archive can omit both without that being a client error (see
/// <c>JustlogRawLineParser</c>'s class comment) — deciding what an absence means is the harness's
/// job (design doc, "Rückfall ohne Badges"), not this client's.
/// <paramref name="HasOtherSourceMarkers"/> carries only the *presence* of the Shared Chat markers
/// other than <see cref="EmotePurge.Core.Chat.SharedChatRule.SourceRoomIdTag"/> — see
/// <see cref="EmotePurge.Core.Chat.SharedChatRule"/>, which this field feeds together with
/// <paramref name="RoomId"/> and <paramref name="SourceRoomId"/>.
/// </summary>
public record ChatLogMessage(
    DateTime SentAtUtc,
    string? UserId,
    IReadOnlyList<KeyValuePair<string, string>> Badges,
    string? RoomId,
    string? SourceRoomId,
    bool HasOtherSourceMarkers,
    string Text);

/// <summary>Outcome of one <see cref="IChatLogArchiveClient.ReadDayAsync"/> call.</summary>
public enum ChatLogDayStatus
{
    /// <summary>The full day was read; <see cref="ChatLogDayResult.BodySha256Hex"/> is set.</summary>
    Complete,

    /// <summary>404 — no log exists for this channel-day. The normal case for most days (T8).</summary>
    NoLogDay,

    /// <summary>429 — the archive throttled this request. The body was never read.</summary>
    RateLimited,

    /// <summary>
    /// The response body did not finish within the configured body timeout — a deadline for the
    /// whole body transfer (not a stall detector: a body that keeps making slow-but-steady
    /// progress past the deadline hits this too), separate from <c>HttpClient.Timeout</c>, which
    /// only covers the header phase once <c>HttpCompletionOption.ResponseHeadersRead</c> is used.
    /// </summary>
    BodyTimeout,

    /// <summary>The <c>maxBytes</c> cap was exceeded; the response was discarded mid-stream.</summary>
    ByteCapExceeded,

    /// <summary>A non-2xx status other than 404/429, or a transport error mid-request or mid-body.</summary>
    TransportFailure,

    /// <summary>
    /// More than half of the body's lines failed to parse as any recognized IRC line — the wire
    /// format has likely changed underneath this client (Failure Mode "Wurzelform anders als
    /// angenommen"), not that the channel happens to have an unusually moderation-heavy day.
    /// </summary>
    MalformedResponse,

    /// <summary>
    /// A single line of a range read exceeded <c>MaxLineBytes</c> before its terminator arrived — a
    /// delimiter-free or otherwise hostile body. The body is discarded without being read further, so
    /// no line (and no memory) beyond the limit is ever held. Only <c>ReadRangeAsync</c> produces
    /// this; <c>ReadDayAsync</c> keeps its <c>StreamReader</c> path and never returns it.
    /// </summary>
    LineTooLong,

    /// <summary>
    /// The caller's token was cancelled while the body was being read (a <c>docker stop</c> or
    /// Ctrl-C mid-transfer). Returned rather than thrown so <see cref="ChatLogDayResult.BytesReceived"/>
    /// can carry the bytes that had already arrived — they left the archive and count against the
    /// caller's byte budget like any other aborted transfer. A cancellation before the response
    /// headers arrived still propagates as <see cref="OperationCanceledException"/>: no body was
    /// read, so there is no count to hand back.
    /// </summary>
    Cancelled
}

/// <summary>
/// Result of reading one channel-day. <paramref name="BodySha256Hex"/> is only populated for
/// <see cref="ChatLogDayStatus.Complete"/> — every other status means the body was partial or
/// never read, so a digest of it would be meaningless.
/// </summary>
public record ChatLogDayResult(
    ChatLogDayStatus Status,
    long BytesReceived,
    string? BodySha256Hex,
    int MessageCount,
    int NonPrivmsgLines,
    int MalformedLines,
    int? HttpStatusCode);

/// <summary>
/// Result of reading one UTC range. Same status vocabulary as <see cref="ChatLogDayResult"/>, but no
/// body digest — a range is identified by its request parameters alone. <paramref name="RetryAfter"/>
/// is only set for <see cref="ChatLogDayStatus.RateLimited"/> and only when the 429 carried a
/// parseable <c>Retry-After</c> header (delta-seconds or HTTP-date); otherwise it is null.
/// <paramref name="BytesReceived"/> counts decompressed bytes, which is what the line parser sees.
/// </summary>
public record ChatLogRangeResult(
    ChatLogDayStatus Status,
    long BytesReceived,
    int MessageCount,
    int NonPrivmsgLines,
    int MalformedLines,
    int? HttpStatusCode,
    TimeSpan? RetryAfter);
