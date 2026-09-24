using System.Text.RegularExpressions;

namespace EmotePurge.Api.Validation;

/// <summary>
/// Shape checks for <c>POST /api/contact</c>'s body, run before the request ever reaches
/// <c>IContactSubmissionService</c> — pure format validation with no infrastructure dependency,
/// same split as <see cref="ChannelNameValidation"/>.
/// </summary>
internal static class ContactValidation
{
    private const int MaxNameLength = 100;
    private const int MaxEmailLength = 254;
    private const int MinMessageLength = 10;
    private const int MaxMessageLength = 5000;

    // Deliberately permissive (no full RFC 5322 grammar): one @, something on both sides, no
    // whitespace or control characters. Turnstile plus the mail send itself are the real gate on
    // whether the address is genuine; this only rejects input that cannot be a mailbox at all.
    private static readonly Regex EmailPattern = new(
        @"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled);

    /// <summary>
    /// True if <paramref name="name"/> (trimmed) is within <see cref="MaxNameLength"/> and free of
    /// control characters — the field is optional, so an empty or whitespace-only value is valid too.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        return name.Length <= MaxNameLength && !ContainsControlCharacter(name);
    }

    public static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > MaxEmailLength)
        {
            return false;
        }

        return !ContainsControlCharacter(email) && EmailPattern.IsMatch(email);
    }

    /// <summary>Length is checked on the trimmed value — leading/trailing whitespace pads neither bound.</summary>
    public static bool IsValidMessage(string message)
    {
        if (message is null)
        {
            return false;
        }

        var trimmedLength = message.Trim().Length;
        return trimmedLength is >= MinMessageLength and <= MaxMessageLength;
    }

    // Catches CR/LF specifically (the header-injection vector the spec calls out) along with every
    // other C0/C1 control character — a name or e-mail has no legitimate reason to carry any of them.
    private static bool ContainsControlCharacter(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }
}
