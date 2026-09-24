namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Bound from the <c>Legal</c> configuration section. Deliberately a single value, not an
/// <c>IOptions</c> with per-field validation like <c>RateLimitingOptions</c>: there is nothing to
/// fail fast on here — an unset or wrong <see cref="ContentPath"/> is a valid, supported state
/// (issue #247 requirement 4: unconfigured is a normal operating mode, not a startup error), and
/// <see cref="LegalContentService"/> is the one place that interprets it.
/// </summary>
public sealed class LegalContentOptions
{
    public const string SectionName = "Legal";

    /// <summary>
    /// Directory holding <c>imprint.de.md</c>, <c>imprint.en.md</c>, <c>privacy.de.md</c>,
    /// <c>privacy.en.md</c>. Unset (the default) means no legal content is configured at all —
    /// deliberately not baked into the image or the repository, which is public and self-hostable
    /// (a fork must not ship the original operator's imprint text).
    /// </summary>
    public string? ContentPath { get; set; }
}
