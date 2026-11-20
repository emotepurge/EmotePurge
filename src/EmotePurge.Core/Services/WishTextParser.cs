using System.Text;
using EmotePurge.Core.Entities;
using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Services;

/// <summary>What <see cref="WishTextParser.Parse"/> read from a redemption text.</summary>
/// <param name="MatchKind">One of <see cref="WishMatchKind"/>: <c>link</c>, <c>name</c> or <c>none</c>.</param>
/// <param name="SevenTvEmoteId">Only for <c>link</c>: the canonical id (<see cref="SevenTvEmoteIdCanonical"/>).</param>
/// <param name="RequestedName">Only for <c>name</c>: the token as written in the text.</param>
public readonly record struct WishTextParseResult(string MatchKind, string? SevenTvEmoteId, string? RequestedName)
{
    public static readonly WishTextParseResult None = new(WishMatchKind.None, null, null);
}

/// <summary>
/// Reads the text a viewer typed into a wish reward (spec K6 "Text auswerten"): a 7TV emote link anywhere in
/// the text wins, otherwise the first token that can be an emote name. Pure and BCL only. The text is
/// viewer-controlled, so the scan is a linear walk (no regular expression, nothing that backtracks), and no
/// 7TV request is made.
/// </summary>
public static class WishTextParser
{
    private const string LinkPath = "7tv.app/emotes/";
    private const int NameMinLength = 2;
    private const string EdgePunctuation = ".,;!?\"'";

    public static WishTextParseResult Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return WishTextParseResult.None;

        var id = FindLinkId(text);
        if (id is not null)
            return new WishTextParseResult(WishMatchKind.Link, SevenTvEmoteIdCanonical.Canonicalize(id), null);

        var name = FindName(text);
        return name is null
            ? WishTextParseResult.None
            : new WishTextParseResult(WishMatchKind.Name, null, name);
    }

    private static string? FindLinkId(string text)
    {
        var from = 0;
        while (from < text.Length)
        {
            var at = text.IndexOf(LinkPath, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return null;

            from = at + 1;
            if (!HostIsAtBoundary(text, at))
                continue;

            var start = at + LinkPath.Length;
            var end = start;
            while (end < text.Length && char.IsAsciiLetterOrDigit(text[end]))
                end++;

            var id = text.Substring(start, end - start);
            if ((id.Length == 24 && IsHex(id)) || id.Length == 26)
                return id;
        }

        return null;
    }

    /// <summary>
    /// The host may carry <c>www.</c> or <c>old.</c> and a <c>http(s)://</c> scheme; anything else in front
    /// (another subdomain, a path segment, a longer word) makes it a different address.
    /// </summary>
    private static bool HostIsAtBoundary(string text, int hostAt)
    {
        var start = hostAt;
        if (EndsWithAt(text, start, "www.") || EndsWithAt(text, start, "old."))
            start -= 4;

        if (EndsWithAt(text, start, "https://"))
            start -= 8;
        else if (EndsWithAt(text, start, "http://"))
            start -= 7;

        if (start == 0)
            return true;

        var before = text[start - 1];
        return !(char.IsAsciiLetterOrDigit(before) || before is '.' or '-' or '/' or '@' or ':' or '_');
    }

    private static bool EndsWithAt(string text, int end, string suffix) =>
        end >= suffix.Length && string.Compare(text, end - suffix.Length, suffix, 0, suffix.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }

        return true;
    }

    private static string? FindName(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
                index++;

            if (index == start)
                break;

            var token = text.AsSpan(start, index - start);
            if (IsAddressOrMention(token))
                continue;

            var name = token.Trim(EdgePunctuation);
            if (IsName(name))
                return name.ToString();
        }

        return null;
    }

    private static bool IsAddressOrMention(ReadOnlySpan<char> token) =>
        token[0] == '@'
        || token.Contains("://", StringComparison.Ordinal)
        || token.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    /// <summary>2 to <see cref="WishLimits.RequestedNameMaxLength"/> code points, at least one letter or digit.</summary>
    private static bool IsName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || name.Length > WishLimits.RequestedNameMaxLength * 2)
            return false;

        var count = 0;
        var hasLetterOrDigit = false;
        foreach (var rune in name.EnumerateRunes())
        {
            count++;
            hasLetterOrDigit |= Rune.IsLetterOrDigit(rune);
        }

        return count is >= NameMinLength and <= WishLimits.RequestedNameMaxLength && hasLetterOrDigit;
    }
}
