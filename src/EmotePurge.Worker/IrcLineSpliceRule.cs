using System.Text;

namespace EmotePurge.Worker;

/// <summary>
/// Detects an IRC line that can only exist because two lines were spliced together on the same
/// socket (issue #114): TwitchLib's inline <c>RECONNECT</c> handling leaves the old read loop
/// running alongside the new one for a brief window, and both can hand fragments of different
/// lines to the same parser.
/// <para>
/// This reads <c>ChatMessage.RawIrcMessage</c>, not <c>ChatMessage.UndocumentedTags</c> (E6). A splice
/// that lands inside the *value* of a typed tag — e.g. <c>subscriber=0@badge-info=</c>, where the
/// second line's tag block starts mid-value — never surfaces as a second dictionary key: TwitchLib's
/// tag parser splits the tag block on <c>;</c>, so the extra <c>@…</c> stays swallowed inside the
/// value of the tag it landed in. Only the raw, unparsed line still shows the second <c>@</c>.
/// </para>
/// <para>
/// A bare second <c>@</c> is *not* the marker — that was the first attempt and it was wrong. The
/// IRCv3 tag escaping only replaces <c>;</c>, space, <c>\</c>, CR and LF, so <c>@</c> survives
/// unescaped inside a tag value, and Twitch's reply UI puts one there in every single reply:
/// <c>reply-parent-msg-body</c> carries the parent message verbatim, and that message usually
/// starts with the <c>@nutzername</c> the UI prepended. Two things separate the two cases:
/// <list type="number">
/// <item>A splice continues with a *new tag block*, i.e. a tag key followed by <c>=</c>
/// (<c>@badge-info=</c>); a mention continues with a login and then hits a separator without ever
/// reaching an <c>=</c>. Hence <see cref="ContainsTagBlockStart"/>.</item>
/// <item>The value of a free-text tag (<c>*msg-body</c>) is chat text under a stranger's control
/// and can imitate anything, including <c>@badge-info=</c>. It is therefore skipped entirely —
/// see the blind spot below.</item>
/// </list>
/// </para>
/// <para>
/// The rule cannot fire on the two other places a line legitimately carries a second <c>@</c>: the
/// IRC prefix (<c>nick!user@host</c>) and an <c>@user</c> mention in the message text both sit
/// after the first space, i.e. outside the tag block this rule inspects.
/// </para>
/// <para>
/// Known lower bound: a splice severe enough to make the line unparsable never reaches
/// <see cref="TwitchChatManager.OnMessageReceived"/> at all (TwitchLib drops it before raising the
/// event), so this rule — and the counter it feeds — undercounts the true splice rate. Skipping
/// free-text values widens that undercount by one case: a splice landing inside a
/// <c>reply-parent-msg-body</c> value goes unseen. That is deliberate — the alternative is a
/// sentinel any chatter can trigger by typing <c>@badge-info=</c>, which would make the instrument
/// useless in the other direction. It proves the defect exists; it does not measure it exhaustively.
/// </para>
/// </summary>
public static class IrcLineSpliceRule
{
    /// <summary>
    /// Default cap for <see cref="TagBlockForLog"/>: long enough to show a full, legitimate tag
    /// block, short enough to bound a single log line even for a pathologically long splice.
    /// </summary>
    public const int DefaultMaxTagBlockLengthForLog = 512;

    /// <summary>
    /// Replaces the value of every free-text or identifying tag in <see cref="TagBlockForLog"/>.
    /// German because it is log output, like the message it is interpolated into.
    /// </summary>
    private const string RedactedValue = "<entfernt>";

    /// <summary>
    /// A tag key ending in this carries chat text written by someone else — today that is
    /// <c>reply-parent-msg-body</c>, and the suffix also covers the thread-parent variant Twitch
    /// may add. Matched as a suffix rather than a fixed list so a new sibling tag is excluded by
    /// default instead of leaking on its first appearance.
    /// </summary>
    private const string FreeTextKeySuffix = "msg-body";

    /// <summary>
    /// Exact tag keys whose value identifies the chatter or reveals their badges (#246) —
    /// <c>display-name</c>/<c>login</c>/<c>user-id</c> for the sender, <c>badges</c>/<c>badge-info</c>
    /// for their sub/mod/VIP status, and the Shared Chat (#73) <c>source-*</c> equivalents of the
    /// badge tags. Redacted the same way as a free-text tag: the key stays, only the value is
    /// replaced, so the diagnostic structure (which tags were present, in which order) survives.
    /// </summary>
    private static readonly string[] IdentifyingKeys =
    [
        "display-name",
        "login",
        "user-id",
        "badges",
        "badge-info",
        "source-badges",
        "source-badge-info",
    ];

    /// <summary>
    /// True if <paramref name="rawLine"/> is an IRC line whose tag block (the segment up to the
    /// first space, or the whole line if there is no space) contains an <c>@</c> that starts a
    /// second tag block. Values of free-text tags are excluded. Allocation-free: spans only, no
    /// <c>Split</c>/<c>Substring</c>.
    /// </summary>
    public static bool IsSpliced(string? rawLine)
    {
        if (string.IsNullOrEmpty(rawLine))
        {
            return false;
        }

        if (rawLine[0] != '@')
        {
            return false;
        }

        // Everything after the leading '@' and before the first space. A ';' inside a tag value is
        // always escaped ("\:"), so a raw ';' is unambiguously a tag separator.
        var remaining = TagBlockSpan(rawLine)[1..];
        while (!remaining.IsEmpty)
        {
            var separator = remaining.IndexOf(';');
            var tag = separator < 0 ? remaining : remaining[..separator];

            if (!IsFreeTextTag(tag) && ContainsTagBlockStart(tag))
            {
                return true;
            }

            remaining = separator < 0 ? [] : remaining[(separator + 1)..];
        }

        return false;
    }

    /// <summary>
    /// Extracts the tag block of <paramref name="rawLine"/> for logging: the segment up to the
    /// first space, or the whole line if there is no space, with the value of every free-text tag
    /// and every identifying tag (<see cref="IdentifyingKeys"/>, #246) replaced by
    /// <see cref="RedactedValue"/>, and the result capped at <paramref name="maxLength"/>
    /// characters. Used by the splice sentinel (#114) to log enough of the line to diagnose the
    /// defect without ever including message text or the chatter's identity — neither this line's
    /// text (it sits after the first space) nor a stranger's (that is what the free-text redaction
    /// is for) nor the sender's own display name/login/user id/badges (the identifying-tag
    /// redaction) — and without letting a pathologically long tag block blow up a log line.
    /// <c>null</c> or empty input yields an empty string, so callers never need to guard the result.
    /// </summary>
    public static string TagBlockForLog(string? rawLine, int maxLength = DefaultMaxTagBlockLengthForLog)
    {
        if (string.IsNullOrEmpty(rawLine))
        {
            return string.Empty;
        }

        var tagBlock = RedactValues(TagBlockSpan(rawLine));
        return tagBlock.Length > maxLength ? tagBlock[..maxLength] : tagBlock;
    }

    /// <summary>
    /// The segment up to the first space, or the whole line if there is no space — including the
    /// leading <c>@</c> if the line has one.
    /// </summary>
    private static ReadOnlySpan<char> TagBlockSpan(string rawLine)
    {
        var tagBlockEnd = rawLine.IndexOf(' ');
        return tagBlockEnd < 0 ? rawLine : rawLine.AsSpan(0, tagBlockEnd);
    }

    /// <summary>
    /// True if <paramref name="tag"/> is a <c>key=value</c> pair whose key marks free chat text
    /// (see <see cref="FreeTextKeySuffix"/>).
    /// </summary>
    private static bool IsFreeTextTag(ReadOnlySpan<char> tag)
    {
        var equalsIndex = tag.IndexOf('=');
        return equalsIndex >= 0 && tag[..equalsIndex].EndsWith(FreeTextKeySuffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// True if <paramref name="tag"/> is a <c>key=value</c> pair whose key identifies the chatter or
    /// their badges. Three shapes, all exact-key or exact-suffix matches (never a bare substring, so
    /// an unrelated tag that merely contains e.g. <c>login</c> is not swept in):
    /// <list type="number">
    /// <item>An exact key from <see cref="IdentifyingKeys"/>.</item>
    /// <item>A <c>reply-parent-*</c> or <c>reply-thread-parent-*</c> tag ending in <c>user-id</c>,
    /// <c>user-login</c> or <c>display-name</c> — the reply's <em>parent</em> message is written by a
    /// different chatter than the one <see cref="IdentifyingKeys"/> already covers, e.g.
    /// <c>reply-parent-user-login</c>, <c>reply-thread-parent-display-name</c>.</item>
    /// <item>A <c>source-*</c> tag ending in <c>user-id</c> or <c>login</c> — Shared Chat (#73) does
    /// not send one today (only the badge/room/message ids <see cref="IdentifyingKeys"/> already
    /// lists), but the shape matches Twitch's naming convention for the rest of its per-user tags,
    /// so a future addition is redacted by default instead of leaking on first appearance.</item>
    /// </list>
    /// </summary>
    private static bool IsIdentifyingTag(ReadOnlySpan<char> tag)
    {
        var equalsIndex = tag.IndexOf('=');
        if (equalsIndex < 0)
        {
            return false;
        }

        var key = tag[..equalsIndex];
        foreach (var identifyingKey in IdentifyingKeys)
        {
            if (key.Equals(identifyingKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (key.StartsWith("reply-parent-", StringComparison.Ordinal) ||
            key.StartsWith("reply-thread-parent-", StringComparison.Ordinal))
        {
            return key.EndsWith("user-id", StringComparison.Ordinal) ||
                   key.EndsWith("user-login", StringComparison.Ordinal) ||
                   key.EndsWith("display-name", StringComparison.Ordinal);
        }

        if (key.StartsWith("source-", StringComparison.Ordinal))
        {
            return key.EndsWith("user-id", StringComparison.Ordinal) ||
                   key.EndsWith("login", StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// True if <paramref name="tag"/> contains an <c>@</c> followed by a non-empty tag key and an
    /// <c>=</c> — the shape of a second tag block spliced into the first one. An <c>@nutzername</c>
    /// mention fails this: a login is followed by a space (escaped as <c>\s</c>) or the end of the
    /// value, never by <c>=</c>.
    /// </summary>
    private static bool ContainsTagBlockStart(ReadOnlySpan<char> tag)
    {
        var index = tag.IndexOf('@');
        while (index >= 0)
        {
            var rest = tag[(index + 1)..];

            var keyLength = 0;
            while (keyLength < rest.Length && IsTagKeyChar(rest[keyLength]))
            {
                keyLength++;
            }

            if (keyLength > 0 && keyLength < rest.Length && rest[keyLength] == '=')
            {
                return true;
            }

            var next = rest.IndexOf('@');
            if (next < 0)
            {
                return false;
            }

            index += 1 + next;
        }

        return false;
    }

    /// <summary>
    /// IRCv3 allows a vendor prefix (<c>example.com/key</c>) and a client prefix (<c>+key</c>) on
    /// top of this, but the two lines that get spliced here are both Twitch's, and Twitch only ever
    /// sends plain lowercase keys. Staying with the narrow set keeps a chat text like
    /// <c>@foo.bar=</c> from looking like a tag block.
    /// </summary>
    private static bool IsTagKeyChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '-';

    /// <summary>
    /// Rebuilds <paramref name="tagBlock"/> with the value of every free-text tag (<see cref="IsFreeTextTag"/>)
    /// and every identifying tag (<see cref="IsIdentifyingTag"/>, #246) replaced. Only ever runs on
    /// the rare sentinel-hit path, so the allocation is irrelevant — unlike the free-text-only
    /// version this replaced, it has no fast path back to the unredacted span, because at least one
    /// identifying tag (typically <c>user-id</c>, <c>display-name</c> and <c>badges</c>) is present
    /// on almost every ordinary message.
    /// </summary>
    private static string RedactValues(ReadOnlySpan<char> tagBlock)
    {
        var builder = new StringBuilder(tagBlock.Length);
        var remaining = tagBlock;

        if (!remaining.IsEmpty && remaining[0] == '@')
        {
            builder.Append('@');
            remaining = remaining[1..];
        }

        var isFirst = true;
        while (true)
        {
            var separator = remaining.IndexOf(';');
            var tag = separator < 0 ? remaining : remaining[..separator];

            if (!isFirst)
            {
                builder.Append(';');
            }

            isFirst = false;

            if (IsFreeTextTag(tag) || IsIdentifyingTag(tag))
            {
                builder.Append(tag[..(tag.IndexOf('=') + 1)]).Append(RedactedValue);
            }
            else
            {
                builder.Append(tag);
            }

            if (separator < 0)
            {
                break;
            }

            remaining = remaining[(separator + 1)..];
        }

        return builder.ToString();
    }
}
