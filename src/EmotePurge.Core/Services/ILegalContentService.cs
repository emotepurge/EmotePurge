namespace EmotePurge.Core.Services;

/// <summary>
/// Which operator-supplied legal document (imprint, privacy policy). A closed vocabulary because
/// the endpoint route parameter (<c>GET /api/legal/{kind}/{language}</c>) is parsed into this rather
/// than passed through as a free string.
/// </summary>
public enum LegalDocumentKind
{
    Imprint,
    Privacy,
}

/// <summary>The two locales the app itself supports (<c>web/core/i18n/language.service.ts</c>).</summary>
public enum LegalLanguage
{
    German,
    English,
}

/// <summary>
/// Which of the two documents are configured at all, independent of language — the frontend uses
/// this to decide whether to show a footer link, never whether a specific language exists (issue
/// #247, requirement 4: unconfigured must not silently 404 or show broken content, so the link
/// itself must not appear in the first place).
/// </summary>
/// <param name="ImprintAvailable">The imprint's German file exists (German is authoritative; see
/// <see cref="ILegalContentService"/> for what "available" requires).</param>
/// <param name="PrivacyAvailable">The privacy policy's German file exists.</param>
public sealed record LegalDocumentAvailability(bool ImprintAvailable, bool PrivacyAvailable);

/// <param name="Html">
/// Markdown rendered to HTML with raw HTML disabled at render time (Markdig <c>DisableHtml()</c>) —
/// nothing from the operator's file can inject a tag. Still bound through Angular's sanitizer on the
/// way in, as defence in depth, not because this alone is assumed insufficient.
/// </param>
/// <param name="IsGermanFallback">
/// True when <see cref="LegalLanguage.English"/> was requested but no English file exists, so the
/// German text was served instead — the frontend uses this to show "only available in German".
/// </param>
public sealed record LegalDocument(string Html, bool IsGermanFallback);

/// <summary>
/// Reads operator-supplied Markdown files from a configured directory (<c>Legal:ContentPath</c>) and
/// renders them to sanitised HTML. Never ships content itself — the repository is public and
/// self-hostable, so a fork must not carry the original operator's imprint text (issue #247).
/// </summary>
/// <remarks>
/// German is authoritative: a document counts as <b>configured</b> only if its German file exists.
/// An English file with no German counterpart is not a valid configuration and is treated the same
/// as no file at all — the alternative (silently promoting a translation to the source of truth)
/// contradicts the one authority rule this service exists to enforce. English is optional on top of
/// that: if it is missing, <see cref="LegalLanguage.English"/> requests fall back to the German text
/// with <see cref="LegalDocument.IsGermanFallback"/> set.
/// </remarks>
public interface ILegalContentService
{
    Task<LegalDocumentAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);

    /// <returns>
    /// <see langword="null"/> if the document is not configured at all — no content path set, or no
    /// German file for this <paramref name="kind"/>. The caller (the endpoint) turns that into 404;
    /// there is no distinct "misconfigured" status because the frontend cannot act on the difference.
    /// </returns>
    Task<LegalDocument?> GetDocumentAsync(
        LegalDocumentKind kind, LegalLanguage language, CancellationToken cancellationToken);
}
