using System.Text.RegularExpressions;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// Last line of defence for captured log text: replaces a small, fixed set of secret shapes with
/// <c>[redacted]</c>. It does not replace redaction at the source (#246) and deliberately knows only
/// four patterns so it does not destroy diagnostic text.
/// </summary>
internal static partial class SecretScrubber
{
    private const string Redacted = "[redacted]";

    // Keeps the named group (the key part) and replaces only what follows it.
    private const string KeepKeyAndRedact = "${key}" + Redacted;

    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        try
        {
            text = PasswordPattern().Replace(text, KeepKeyAndRedact);
            text = OAuthPattern().Replace(text, KeepKeyAndRedact);
            text = BearerPattern().Replace(text, KeepKeyAndRedact);
            return NamedSecretPattern().Replace(text, KeepKeyAndRedact);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: a text the scrubber cannot finish is not stored.
            return Redacted;
        }
    }

    [GeneratedRegex(@"(?<key>\bPassword\s*=)[^;\r\n]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex PasswordPattern();

    [GeneratedRegex(@"(?<key>\boauth:)[A-Za-z0-9_\-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex OAuthPattern();

    // A Bearer value only counts as a token from 16 token characters on, so prose such as
    // "Bearer of bad news" survives while JWTs and opaque tokens do not.
    [GeneratedRegex(@"(?<key>\bBearer\s+)[A-Za-z0-9._~+/=\-]{16,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?<key>\b(?:access_token|refresh_token|client_secret)\s*=)[^&;\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex NamedSecretPattern();
}
