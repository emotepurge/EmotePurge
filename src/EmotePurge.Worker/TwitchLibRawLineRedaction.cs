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
/// fragment of that value, not a command word: e.g. a corrupted
/// <c>reply-parent-msg-body=hello SECRET</c> (an unescaped space inside the value) leaves "SECRET"
/// sitting exactly where the command goes. <see cref="IsLikelyIrcCommand"/> guards against exactly
/// that — not with a generic "looks like a command" shape check (an all-upper-case word is just as
/// easily a chat-text fragment, as "SECRET" shows), but a fixed allow-list of the tokens TwitchLib
/// itself recognises (decompiled from <c>TwitchLib.Client.Parsing.IrcParser.ParseCommand</c>,
/// TwitchLib.Client 4.0.1 — anything else parses to <c>IrcCommand.Unknown</c> there and is exactly
/// what reaches <c>TwitchClient.UnaccountedFor</c>, i.e. <c>LogUnaccountedFor</c>, in the first
/// place). Twitch's three-digit numeric replies (<c>001</c>, <c>372</c>, ...) are accepted by shape
/// (three ASCII digits) rather than as a fixed list, since IRC servers are free to send others.
/// Anything that matches neither is reported as unparsed rather than logged verbatim. Tag
/// <em>keys</em> need no such guard — they are always taken up to the first <c>=</c> or <c>;</c>, so
/// a value that runs past where a key was expected simply never gets read.
/// </para>
/// </summary>
public static class TwitchLibRawLineRedaction
{
    private const string NoCommandMarker = "<none>";
    private const string UnparsedCommandMarker = "<unparsed>";
    private const int MaxTagKeys = 64;
    private const int MaxCommandLength = 32;

    // Every token TwitchLib.Client 4.0.1's IrcParser.ParseCommand switch recognises — decompiled,
    // not guessed (see the class-level remarks). NICK/PASS never occur on an already-authenticated
    // connection and SERVERCHANGE/366/375/376 are login-sequence-only, but they are kept here
    // anyway: this is an allow-list of what TwitchLib itself calls a command, not a claim about
    // which of those this Worker's connection can currently receive.
    private static readonly HashSet<string> KnownIrcCommands = new(StringComparer.Ordinal)
    {
        "PRIVMSG", "NOTICE", "PING", "PONG", "CLEARCHAT", "CLEARMSG", "USERSTATE", "GLOBALUSERSTATE",
        "NICK", "JOIN", "PART", "PASS", "CAP", "WHISPER", "SERVERCHANGE", "RECONNECT", "ROOMSTATE",
        "USERNOTICE", "MODE",
    };

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
    /// True if <paramref name="candidate"/> is a token TwitchLib itself treats as an IRC command —
    /// an exact (ordinal) member of <see cref="KnownIrcCommands"/>, or three ASCII digits (a numeric
    /// reply). Deliberately not "every character is upper-case" — that shape is just as easily an
    /// all-caps word from chat text that ended up in the command position (see the class-level
    /// remarks), which is exactly the leak this guard exists to close.
    /// </summary>
    private static bool IsLikelyIrcCommand(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length == 3 && IsAllAsciiDigits(candidate))
        {
            return true;
        }

        return KnownIrcCommands.Contains(candidate.ToString());
    }

    private static bool IsAllAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
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
