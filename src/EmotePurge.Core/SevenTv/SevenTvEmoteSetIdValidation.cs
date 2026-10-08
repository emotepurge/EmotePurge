using System.Text.RegularExpressions;

namespace EmotePurge.Core.SevenTv;

/// <summary>
/// Format check for a 7TV emote-set id: not empty, 1-32 characters, <c>[0-9A-Za-z]</c> only, ordinal
/// - deliberately wider than either id shape 7TV actually issues (26-character ULIDs, 24-character hex
/// ObjectIDs), because it only has to reject unfit input (empty, path characters, quotes), never
/// validate 7TV's own format. Lives in Core so the API edge and the tag service share one rule.
/// </summary>
public static class SevenTvEmoteSetIdValidation
{
    private const int MaxLength = 32;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // \z, not $: without RegexOptions.Multiline, $ also matches before a trailing "\n", and the id is never trimmed.
    private static readonly Regex Pattern = new(
        @"^[0-9A-Za-z]{1,32}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>No normalization step - a set id is compared ordinally everywhere it is used.</summary>
    public static bool IsValid(string? emoteSetId)
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
