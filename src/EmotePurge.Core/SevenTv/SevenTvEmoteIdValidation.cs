using System.Text.RegularExpressions;

namespace EmotePurge.Core.SevenTv;

/// <summary>
/// Format check for an inbound 7TV emote id: not empty, 1-32 characters, <c>[0-9A-Za-z]</c> only,
/// ordinal. The same rule as the Api's set-id check (<c>EmoteSetIdValidation</c>) and for the same
/// reason deliberately wider than either id shape 7TV issues (26-character ULIDs today, 24-character
/// hex ObjectIDs on older emotes): it only has to reject unfit input (empty, path characters, quotes,
/// anything longer than the 32-character column), never validate 7TV's own format. Lives in Core
/// rather than next to the set-id check because a service, not a filter, applies it — the emote ids
/// arrive in a request body, and the service is the single authority for its status codes.
/// </summary>
public static class SevenTvEmoteIdValidation
{
    public const int MaxLength = 32;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // \z, not $: without RegexOptions.Multiline, $ also matches before a trailing "\n", and the id is never trimmed.
    private static readonly Regex Pattern = new(
        @"^[0-9A-Za-z]{1,32}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>No normalization step — a 7TV emote id is compared ordinally everywhere it is used.</summary>
    public static bool IsValid(string? sevenTvEmoteId)
    {
        if (string.IsNullOrEmpty(sevenTvEmoteId) || sevenTvEmoteId.Length > MaxLength)
            return false;

        try
        {
            return Pattern.IsMatch(sevenTvEmoteId);
        }
        catch (RegexMatchTimeoutException)
        {
            // Cannot realistically happen for a 32-character linear pattern; if it ever does, the id is unfit, not a 500.
            return false;
        }
    }
}
