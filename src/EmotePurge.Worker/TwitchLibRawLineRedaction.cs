namespace EmotePurge.Worker;

/// <summary>
/// Reduces a raw IRC line to a diagnosable but safe form for <see cref="RedactingTwitchClientLoggerFactory"/>
/// (#246): the tag block's <em>keys</em> only — never a value, since a value can carry chat text, a
/// display name, a login, a numeric user id, or (on a reply) the quoted parent message — plus,
/// best-effort, the IRC command word. Never throws on malformed input: the whole point is
/// diagnosing lines TwitchLib itself failed to parse, so the input here can be anything, including
/// a line truncated mid-tag by the splice defect (#114).
/// <para>
/// Tag values are IRCv3-escaped, so a well-formed line has no raw space before the true end of the
/// tag block — but a line that reaches here failed to parse, so that assumption cannot be trusted.
/// If it does not hold, the naive "first space in the line" cut can land inside a tag's value, and
/// the text after it — which this class would otherwise report as the IRC command — is then a
/// fragment of that value, not a command word. <see cref="IsLikelyIrcCommand"/> guards against
/// exactly that: a real IRC command is always upper-case letters and/or digits (<c>PRIVMSG</c>,
/// <c>JOIN</c>, the three-digit numeric replies); anything else found in the command position is
/// reported as unparsed rather than logged verbatim. Tag <em>keys</em> need no such guard — they are
/// always taken up to the first <c>=</c> or <c>;</c>, so a value that runs past where a key was
/// expected simply never gets read.
/// </para>
/// </summary>
public static class TwitchLibRawLineRedaction
{
    private const string NoCommandMarker = "<none>";
    private const string UnparsedCommandMarker = "<unparsed>";
    private const int MaxTagKeys = 64;
    private const int MaxCommandLength = 32;

    public static string Redact(string? rawLine)
    {
        if (string.IsNullOrEmpty(rawLine))
        {
            return $"command={NoCommandMarker}, tagKeys=[]";
        }

        var remaining = rawLine.AsSpan();
        var tagKeys = string.Empty;

        if (remaining.Length > 0 && remaining[0] == '@')
        {
            var tagBlockEnd = remaining.IndexOf(' ');
            var tagBlock = tagBlockEnd < 0 ? remaining[1..] : remaining[1..tagBlockEnd];
            tagKeys = string.Join(',', ExtractKeys(tagBlock));
            remaining = tagBlockEnd < 0 ? [] : remaining[(tagBlockEnd + 1)..];
        }

        remaining = remaining.TrimStart(' ');

        // An IRC prefix (":nick!user@host") never carries a value worth keeping here — the
        // command word alone already identifies the message shape (PRIVMSG, JOIN, NOTICE, ...).
        if (remaining.Length > 0 && remaining[0] == ':')
        {
            var prefixEnd = remaining.IndexOf(' ');
            remaining = prefixEnd < 0 ? [] : remaining[(prefixEnd + 1)..];
        }

        remaining = remaining.TrimStart(' ');
        var commandEnd = remaining.IndexOf(' ');
        var command = commandEnd < 0 ? remaining : remaining[..commandEnd];
        if (command.Length > MaxCommandLength)
        {
            command = command[..MaxCommandLength];
        }

        string commandText;
        if (command.IsEmpty)
        {
            commandText = NoCommandMarker;
        }
        else if (IsLikelyIrcCommand(command))
        {
            commandText = command.ToString();
        }
        else
        {
            commandText = UnparsedCommandMarker;
        }

        return $"command={commandText}, tagKeys=[{tagKeys}]";
    }

    /// <summary>
    /// True if every character is an upper-case ASCII letter or digit — the shape of every real IRC
    /// command token, including Twitch's numeric replies (<c>001</c>, <c>372</c>, ...). Chat text or
    /// a tag value accidentally landing here almost always contains a lower-case letter, punctuation
    /// or a space and therefore fails this check (see the class-level remarks).
    /// </summary>
    private static bool IsLikelyIrcCommand(ReadOnlySpan<char> candidate)
    {
        foreach (var c in candidate)
        {
            if (!char.IsAsciiDigit(c) && !char.IsAsciiLetterUpper(c))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> ExtractKeys(ReadOnlySpan<char> tagBlock)
    {
        var keys = new List<string>();
        var remaining = tagBlock;
        while (!remaining.IsEmpty && keys.Count < MaxTagKeys)
        {
            var separator = remaining.IndexOf(';');
            var tag = separator < 0 ? remaining : remaining[..separator];
            var equalsIndex = tag.IndexOf('=');
            var key = equalsIndex < 0 ? tag : tag[..equalsIndex];
            if (!key.IsEmpty)
            {
                keys.Add(key.ToString());
            }

            remaining = separator < 0 ? [] : remaining[(separator + 1)..];
        }

        return keys;
    }
}
