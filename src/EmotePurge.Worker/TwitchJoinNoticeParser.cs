using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

/// <summary>
/// Recognises the JOIN rejections Twitch states in a NOTICE — the concurrent channel limit, a ban and a
/// suspended channel — in a raw received IRC line. The first two reach no TwitchLib failure callback at
/// all. <c>msg_channel_suspended</c> does, but only after TwitchLib has already sent the next queued
/// JOIN (possibly a retry for the same channel), so the raw line is the one place that still knows which
/// attempt the rejection ended; the callback books nothing for it (see
/// <see cref="TwitchChatManager.OnFailureToReceiveJoinConfirmation"/>).
/// <para>
/// Pure and allocation-free on the hot path: <see cref="IsNotice"/> is the cheap per-line test the
/// receive handler runs on every frame, and <see cref="TryParse"/> runs only on a hit. Neither throws.
/// The <c>msg-id</c> tag is found wherever it sits in the tag block, because Twitch does not
/// guarantee tag order.
/// </para>
/// </summary>
public static class TwitchJoinNoticeParser
{
    private const string NoticeCommand = "NOTICE";
    private const string MsgIdKey = "msg-id";
    private const string ConcurrentLimitMsgId = "msg_concurrent_channel_limit_reached";
    private const string BannedMsgId = "msg_banned";
    private const string SuspendedMsgId = "msg_channel_suspended";

    /// <summary>True when the line's IRC command is exactly <c>NOTICE</c>.</summary>
    public static bool IsNotice(ReadOnlySpan<char> line) =>
        TryGetCommand(line, out var command, out _) && command.SequenceEqual(NoticeCommand);

    /// <summary>
    /// True only for a NOTICE that carries <c>msg-id=msg_concurrent_channel_limit_reached</c>,
    /// <c>msg_banned</c> or <c>msg_channel_suspended</c> and targets a concrete channel. <paramref name="channel"/> is lowercase,
    /// without the leading <c>#</c>.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> line, out string reason, out string channel)
    {
        reason = string.Empty;
        channel = string.Empty;

        if (!TryGetCommand(line, out var command, out var afterCommand) || !command.SequenceEqual(NoticeCommand))
        {
            return false;
        }

        var msgId = FindMsgId(line);
        string matchedReason;
        if (msgId.SequenceEqual(ConcurrentLimitMsgId))
        {
            matchedReason = TwitchJoinFailureReasons.ConcurrentChannelLimit;
        }
        else if (msgId.SequenceEqual(BannedMsgId))
        {
            matchedReason = TwitchJoinFailureReasons.Banned;
        }
        else if (msgId.SequenceEqual(SuspendedMsgId))
        {
            matchedReason = TwitchJoinFailureReasons.ChannelSuspended;
        }
        else
        {
            return false;
        }

        // The first parameter is the target: "#channel", or "*" for a connection-level notice.
        var target = NextToken(afterCommand);
        if (target.Length < 2 || target[0] != '#')
        {
            return false;
        }

        reason = matchedReason;
        channel = target[1..].ToString().ToLowerInvariant();
        return true;
    }

    // Skips the optional tag block ("@… ") and prefix (":… "), then returns the command token.
    private static bool TryGetCommand(ReadOnlySpan<char> line, out ReadOnlySpan<char> command, out ReadOnlySpan<char> rest)
    {
        command = default;
        rest = default;

        var span = line;
        if (!span.IsEmpty && span[0] == '@')
        {
            var space = span.IndexOf(' ');
            if (space < 0)
            {
                return false;
            }

            span = span[(space + 1)..];
        }

        span = span.TrimStart(' ');
        if (!span.IsEmpty && span[0] == ':')
        {
            var space = span.IndexOf(' ');
            if (space < 0)
            {
                return false;
            }

            span = span[(space + 1)..];
        }

        span = span.TrimStart(' ');
        if (span.IsEmpty)
        {
            return false;
        }

        var end = span.IndexOf(' ');
        command = (end < 0 ? span : span[..end]).TrimEnd("\r\n");
        rest = end < 0 ? default : span[(end + 1)..];
        return !command.IsEmpty;
    }

    private static ReadOnlySpan<char> FindMsgId(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty || line[0] != '@')
        {
            return default;
        }

        var blockEnd = line.IndexOf(' ');
        var tags = blockEnd < 0 ? line[1..] : line[1..blockEnd];

        while (!tags.IsEmpty)
        {
            var semi = tags.IndexOf(';');
            var tag = semi < 0 ? tags : tags[..semi];
            tags = semi < 0 ? default : tags[(semi + 1)..];

            var eq = tag.IndexOf('=');
            if (eq > 0 && tag[..eq].SequenceEqual(MsgIdKey))
            {
                return tag[(eq + 1)..];
            }
        }

        return default;
    }

    private static ReadOnlySpan<char> NextToken(ReadOnlySpan<char> span)
    {
        span = span.TrimStart(' ');
        var end = span.IndexOf(' ');
        return (end < 0 ? span : span[..end]).TrimEnd("\r\n");
    }
}
