using EmotePurge.Core.Services;

namespace EmotePurge.Api.Validation;

/// <summary>
/// Reads the set-selecting query parameters of <c>GET /usage-stats/daily</c> into one
/// <see cref="EmoteSetScope"/>. The route takes <i>either</i> <c>emoteSetId=&lt;id&gt;</c> <i>or</i>
/// <c>setScope=all</c> <i>or</i> neither (the channel's active set) — an explicit scope parameter
/// rather than a reserved id, because <c>all</c> is itself a well-formed set id. Kept out of
/// <see cref="EmoteSetIdValidationFilter"/>, which hangs on routes that have no <c>setScope</c>.
/// </summary>
internal static class EmoteSetScopeParser
{
    private const string ActiveScope = "active";
    private const string AllScope = "all";

    /// <summary>
    /// <paramref name="setScope"/> is compared ordinally and only the lowercase words
    /// <c>active</c> and <c>all</c> count; anything else, including an empty value, fails. Combining
    /// either word with an <paramref name="emoteSetId"/> fails too — the two parameters answer the
    /// same question, so one may not silently win. The id's own format is the filter's business, not
    /// checked here.
    /// </summary>
    public static bool TryParse(string? setScope, string? emoteSetId, out EmoteSetScope scope)
    {
        scope = EmoteSetScope.ActiveSet;

        if (setScope is null)
        {
            if (emoteSetId is null)
            {
                return true;
            }

            if (emoteSetId.Length == 0)
            {
                return false;
            }

            scope = EmoteSetScope.Set(emoteSetId);
            return true;
        }

        if (emoteSetId is not null)
        {
            return false;
        }

        if (string.Equals(setScope, ActiveScope, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(setScope, AllScope, StringComparison.Ordinal))
        {
            scope = EmoteSetScope.AllSets;
            return true;
        }

        return false;
    }
}
