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
    /// Replaces every tag value in <see cref="TagBlockForLog"/> except the format-checked ones.
    /// German because it is log output, like the message it is interpolated into.
    /// </summary>
    private const string RedactedValue = "<entfernt>";

    /// <summary>
    /// Stands in <see cref="TagBlockForLog"/> for a whole fragment whose key is not on the
    /// <see cref="TwitchTagKeyAllowlist"/> (or that has no <c>=</c> at all): key and value are
    /// withheld together, because after a splice such a fragment can be chat text (S2).
    /// </summary>
    private const string WithheldFragment = "<withheld>";

    private const int MaxNumericValueLength = 20;

    /// <summary>
    /// A tag key ending in this carries chat text written by someone else — today that is
    /// <c>reply-parent-msg-body</c>, and the suffix also covers the thread-parent variant Twitch
    /// may add. Matched as a suffix rather than a fixed list so a new sibling tag is excluded by
    /// default instead of leaking on its first appearance. Only <see cref="IsSpliced"/> still needs
    /// it: logging redacts every value regardless.
    /// </summary>
    private const string FreeTextKeySuffix = "msg-body";

    /// <summary>
    /// The message template of the splice warning (#114) in <c>TwitchChatManager</c>, kept here
    /// next to <see cref="ForSpliceWarning"/> so the warning and its tests render the very same
    /// text. German because it is log output.
    /// </summary>
    public const string SpliceWarningTemplate =
        "Gespleißte IRC-Zeile erkannt (#114) in Channel {Channel}, RoomId {RoomId}: {TagBlock}";

    /// <summary>
    /// Tags whose value is a pure number (at most <see cref="MaxNumericValueLength"/> digits) and
    /// therefore safe to log: they let a reader tell which two lines were merged (#114). The room id
    /// names only the channel the line already names.
    /// </summary>
    private static readonly string[] NumericValueKeys = ["tmi-sent-ts", "room-id"];

    /// <summary>
    /// Tags whose value is a yes/no flag (exactly <c>0</c> or <c>1</c>) and carries no identity.
    /// </summary>
    private static readonly string[] FlagValueKeys =
    [
        "subscriber", "mod", "turbo", "vip", "first-msg", "returning-chatter", "emote-only",
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
    /// first space, or the whole line if there is no space, reduced to its <em>keys</em> and capped
    /// at <paramref name="maxLength"/> characters. Every value — at the top level and inside a tag
    /// a splice embedded after an <c>@</c> — is replaced by <see cref="RedactedValue"/>, except the
    /// format-checked ones: <c>tmi-sent-ts</c> and <c>room-id</c> (digits only, at most 20) and the
    /// yes/no flags (exactly <c>0</c> or <c>1</c>). A fragment without <c>=</c> or whose key is not
    /// on the <see cref="TwitchTagKeyAllowlist"/> is replaced as a whole by
    /// <see cref="WithheldFragment"/>. Used by the splice sentinel (#114) to log enough of the line
    /// to diagnose the defect without ever including message text or any identity (#246).
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
    /// Prepares the three arguments of <see cref="SpliceWarningTemplate"/>. <c>ChatMessage.RoomId</c>
    /// is TwitchLib's raw <c>room-id</c> value, which on a splice can carry a second line's tags
    /// (<c>111111111@display-name=…@user-id=…</c>), so it is logged only as up to 20 digits and as
    /// <see cref="RedactedValue"/> otherwise. The channel comes from the IRC parameter, not from the
    /// tag block, and is kept. TwitchLib-free so it can be tested without a parsed message.
    /// </summary>
    public static SpliceWarningFields ForSpliceWarning(string channel, string? roomId, string? rawIrc) =>
        new(channel, IsDigitsUpTo(roomId, MaxNumericValueLength) ? roomId! : RedactedValue, TagBlockForLog(rawIrc));

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
        return equalsIndex >= 0 && IsFreeTextKey(tag[..equalsIndex]);
    }

    private static bool IsFreeTextKey(ReadOnlySpan<char> key) =>
        key.EndsWith(FreeTextKeySuffix, StringComparison.Ordinal);

    /// <summary>
    /// True if <paramref name="tag"/> contains an <c>@</c> followed by a non-empty tag key and an
    /// <c>=</c> — the shape of a second tag block spliced into the first one. An <c>@nutzername</c>
    /// mention fails this: a login is followed by a space (escaped as <c>\s</c>) or the end of the
    /// value, never by <c>=</c>.
    /// </summary>
    private static bool ContainsTagBlockStart(ReadOnlySpan<char> tag) => TryFindEmbeddedKeyStart(tag, out _, out _);

    /// <summary>
    /// Finds the first splice-embedded tag-block start in <paramref name="value"/> — the same shape
    /// <see cref="ContainsTagBlockStart"/> tests for, an <c>@</c> followed by a non-empty tag key and
    /// an <c>=</c> — and returns where the <c>@</c> and the terminating <c>=</c> sit. Used by
    /// <see cref="AppendTag"/> (Codex review finding P1) to redact an embedded
    /// tag's own value, not just top-level ones: <see cref="IsSpliced"/> already proves a splice can
    /// land inside a typed tag's value (<c>subscriber=0@badge-info=...</c>) rather than cleanly
    /// between two <c>;</c>-separated tags, and that embedded fragment can itself be an identifying
    /// or free-text tag whose value must not reach the log any more than a top-level one would.
    /// </summary>
    private static bool TryFindEmbeddedKeyStart(ReadOnlySpan<char> value, out int atIndex, out int equalsIndex)
    {
        var index = value.IndexOf('@');
        while (index >= 0)
        {
            var rest = value[(index + 1)..];

            var keyLength = 0;
            while (keyLength < rest.Length && IsTagKeyChar(rest[keyLength]))
            {
                keyLength++;
            }

            if (keyLength > 0 && keyLength < rest.Length && rest[keyLength] == '=')
            {
                atIndex = index;
                equalsIndex = index + 1 + keyLength;
                return true;
            }

            var next = rest.IndexOf('@');
            if (next < 0)
            {
                break;
            }

            index += 1 + next;
        }

        atIndex = -1;
        equalsIndex = -1;
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
    /// Rebuilds <paramref name="tagBlock"/> as described at <see cref="TagBlockForLog"/>. Only ever
    /// runs on the rare sentinel-hit path, so the allocation is irrelevant.
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
            AppendTag(builder, tag);

            if (separator < 0)
            {
                break;
            }

            remaining = remaining[(separator + 1)..];
        }

        return builder.ToString();
    }

    /// <summary>
    /// Appends one <c>;</c>-separated fragment: withheld as a whole unless it is <c>key=value</c>
    /// with an allowlisted key; otherwise the key and its value, which is replaced unless it is a
    /// format-checked one (<see cref="AppendValue"/>). A free-text value is replaced wholesale
    /// without looking inside: it is chat text and may imitate anything. For every other tag a
    /// splice can land mid-value (<c>subscriber=0@badge-info=…</c>, see <see cref="IsSpliced"/>);
    /// each embedded tag after an <c>@</c> followed by a tag key and <c>=</c> (escaping is not considered, see <see cref="TryFindEmbeddedKeyStart"/>) is treated exactly like a top-level one, so the
    /// splice's shape survives and nothing it dragged along does.
    /// </summary>
    private static void AppendTag(StringBuilder builder, ReadOnlySpan<char> tag)
    {
        if (tag.IsEmpty)
        {
            return;
        }

        var firstEquals = tag.IndexOf('=');
        if (firstEquals < 0 || !TwitchTagKeyAllowlist.IsAllowed(tag[..firstEquals]))
        {
            builder.Append(WithheldFragment);
            return;
        }

        var currentKey = tag[..firstEquals];
        builder.Append(currentKey).Append('=');
        var cursor = tag[(firstEquals + 1)..];

        if (IsFreeTextKey(currentKey))
        {
            builder.Append(RedactedValue);
            return;
        }

        while (true)
        {
            var found = TryFindEmbeddedKeyStart(cursor, out var at, out var eq);
            AppendValue(builder, currentKey, found ? cursor[..at] : cursor);
            if (!found)
            {
                return;
            }

            builder.Append('@');
            var embeddedKey = cursor[(at + 1)..eq];
            if (TwitchTagKeyAllowlist.IsAllowed(embeddedKey))
            {
                builder.Append(embeddedKey).Append('=');
                currentKey = embeddedKey;
            }
            else
            {
                builder.Append(WithheldFragment);
                currentKey = default;
            }

            cursor = cursor[(eq + 1)..];
        }
    }

    /// <summary>
    /// Appends <paramref name="value"/> of <paramref name="key"/> only if it has one of the two
    /// harmless formats (digits for <see cref="NumericValueKeys"/>, exactly <c>0</c>/<c>1</c> for
    /// <see cref="FlagValueKeys"/>), otherwise <see cref="RedactedValue"/>. An empty
    /// <paramref name="key"/> means the value belongs to a withheld fragment and is dropped.
    /// </summary>
    private static void AppendValue(StringBuilder builder, ReadOnlySpan<char> key, ReadOnlySpan<char> value)
    {
        if (key.IsEmpty)
        {
            return;
        }

        var keep = (ContainsKey(NumericValueKeys, key) && IsDigitsUpTo(value, MaxNumericValueLength)) ||
                   (ContainsKey(FlagValueKeys, key) && (value.SequenceEqual("0") || value.SequenceEqual("1")));
        if (keep)
        {
            builder.Append(value);
        }
        else
        {
            builder.Append(RedactedValue);
        }
    }

    private static bool ContainsKey(string[] keys, ReadOnlySpan<char> key)
    {
        foreach (var candidate in keys)
        {
            if (key.Equals(candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDigitsUpTo(ReadOnlySpan<char> value, int maxLength)
    {
        if (value.IsEmpty || value.Length > maxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The arguments of <see cref="IrcLineSpliceRule.SpliceWarningTemplate"/>, already safe to log.
/// </summary>
public sealed record SpliceWarningFields(string Channel, string RoomId, string TagBlock);
