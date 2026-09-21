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

    private static readonly Regex Pattern = new("^[0-9A-Za-z]{1,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>No normalization step, unlike <c>ChannelNameValidation</c> — a set id is compared
    /// ordinally everywhere it is used and carries no canonical casing to fold onto.</summary>
    public static bool IsValid(string emoteSetId) =>
        !string.IsNullOrEmpty(emoteSetId) && emoteSetId.Length <= MaxLength && Pattern.IsMatch(emoteSetId);
}
