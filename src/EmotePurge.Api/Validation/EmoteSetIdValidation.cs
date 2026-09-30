using System.Text.RegularExpressions;

namespace EmotePurge.Api.Validation;

/// <summary>
/// Format check for an inbound 7TV emote-set id (spec 2026-09-20, E14): not empty, 1-32 characters,
/// <c>[0-9A-Za-z]</c> only, ordinal — deliberately wider than either id shape 7TV actually issues
/// (26-character ULIDs today, 24-character hex ObjectIDs on older sets), because this check only
/// ever has to reject unfit input (empty, path characters, quotes), never validate 7TV's own format.
/// </summary>
internal static class EmoteSetIdValidation
{
    private const int MaxLength = 32;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // \z, not $: without RegexOptions.Multiline, $ also matches before a trailing "\n", and the id is never trimmed.
    private static readonly Regex Pattern = new(
        @"^[0-9A-Za-z]{1,32}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>No normalization step, unlike <c>ChannelNameValidation</c> — a set id is compared
    /// ordinally everywhere it is used and carries no canonical casing to fold onto.</summary>
    public static bool IsValid(string emoteSetId)
    {
        if (string.IsNullOrEmpty(emoteSetId) || emoteSetId.Length > MaxLength)
            return false;

        try
        {
            return Pattern.IsMatch(emoteSetId);
        }
        catch (RegexMatchTimeoutException)
        {
            // Cannot realistically happen for a 32-character linear pattern; if it ever does, the id is unfit, not a 500.
            return false;
        }
    }
}
