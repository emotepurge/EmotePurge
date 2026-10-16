using System.Text;
using System.Text.RegularExpressions;

namespace EmotePurge.Core.Entities;

public static partial class LogFilterNames
{
    /// <summary>
    /// Uniqueness key of a saved filter name: trimmed, whitespace runs collapsed to one space, NFC,
    /// invariant lower case. Done in code because Postgres lower() folds only ASCII under the C locale.
    /// </summary>
    public static string NormalizeKey(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var collapsed = WhitespaceRun().Replace(name.Trim(), " ");
        return collapsed.Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
