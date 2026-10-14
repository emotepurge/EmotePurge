namespace EmotePurge.Worker;

/// <summary>
/// The one shared answer to "may this IRC tag key appear in a log line?" for both redaction sites,
/// <see cref="TwitchLibRawLineRedaction"/> and <see cref="IrcLineSpliceRule.TagBlockForLog"/> (#246).
/// A key passes only if it satisfies the IRCv3 key grammar (optional <c>+</c>, optional
/// <c>vendor/</c>, then <c>[A-Za-z0-9-]</c>, at most <see cref="MaxKeyLength"/> characters) <em>and</em>
/// is a documented Twitch tag: a fixed name from <see cref="FixedKeys"/> or one of the
/// <see cref="Prefixes"/> followed by a lower-case rest of 1 to <see cref="MaxPrefixRestLength"/>
/// characters from <c>[a-z0-9-]</c>. Everything else — a bare fragment, an invented key, a
/// grammar violation — is withheld together with its value, because after a splice (#114) such a
/// "key" can be a piece of chat text or a display name.
/// <para>
/// Compared with Twitch's IRC tag reference (dev.twitch.tv/docs/chat/irc, checked 2026-10-14) the
/// list additionally carries the whisper tags <c>message-id</c>/<c>thread-id</c>, the undocumented
/// but real <c>first-msg</c>, <c>flags</c>, <c>returning-chatter</c>, <c>client-nonce</c>,
/// <c>animation-id</c>, <c>custom-reward-id</c>, and the documented <c>gifs</c> tag. The
/// camel-case raid/subscription keys <c>msg-param-displayName</c>, <c>msg-param-viewerCount</c>,
/// <c>msg-param-profileImageURL</c> and <c>msg-param-copoReward</c> cannot match the lower-case prefix rest, so they are listed as
/// fixed names.
/// </para>
/// </summary>
internal static class TwitchTagKeyAllowlist
{
    internal const int MaxKeyLength = 64;
    internal const int MaxPrefixRestLength = 48;

    internal static readonly string[] FixedKeys =
    [
        "badge-info", "badges", "bits", "client-nonce", "color", "display-name", "emote-only", "emote-sets",
        "emotes", "first-msg", "flags", "followers-only", "gifs", "id", "login", "message-id", "mod", "msg-id",
        "r9k", "returning-chatter", "room-id", "slow", "subs-only", "subscriber", "system-msg", "target-msg-id",
        "target-user-id", "ban-duration", "thread-id", "tmi-sent-ts", "turbo", "user-id", "user-type", "vip",
        "animation-id", "custom-reward-id",
        "msg-param-displayName", "msg-param-viewerCount", "msg-param-profileImageURL", "msg-param-copoReward",
    ];

    internal static readonly string[] Prefixes =
    [
        "msg-param-", "reply-parent-", "reply-thread-parent-", "pinned-chat-paid-", "source-",
    ];

    private static readonly HashSet<string> FixedKeySet = new(FixedKeys, StringComparer.Ordinal);

    public static bool IsAllowed(ReadOnlySpan<char> key)
    {
        if (!SatisfiesGrammar(key))
        {
            return false;
        }

        if (FixedKeySet.Contains(key.ToString()))
        {
            return true;
        }

        foreach (var prefix in Prefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return IsValidPrefixRest(key[prefix.Length..]);
            }
        }

        return false;
    }

    private static bool SatisfiesGrammar(ReadOnlySpan<char> key)
    {
        if (key.IsEmpty || key.Length > MaxKeyLength)
        {
            return false;
        }

        var rest = key;
        if (rest[0] == '+')
        {
            rest = rest[1..];
        }

        var slash = rest.IndexOf('/');
        if (slash >= 0)
        {
            var vendor = rest[..slash];
            if (vendor.IsEmpty)
            {
                return false;
            }

            foreach (var c in vendor)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-')
                {
                    return false;
                }
            }

            rest = rest[(slash + 1)..];
        }

        if (rest.IsEmpty)
        {
            return false;
        }

        foreach (var c in rest)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidPrefixRest(ReadOnlySpan<char> rest)
    {
        if (rest.IsEmpty || rest.Length > MaxPrefixRestLength)
        {
            return false;
        }

        foreach (var c in rest)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }
}
