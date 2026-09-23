using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// Operator-supplied imprint/privacy policy (issue #247). Both routes are anonymous on purpose —
/// requirement 3 is that the pages are reachable without being logged in, before the Twitch OAuth
/// redirect even happens — so, unlike every other group in this folder, there is no
/// <c>IEndpointFilter</c> here at all.
/// </summary>
public static class LegalEndpoints
{
    public static void MapLegalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/legal");

        // Tells the frontend which footer links to show at all — never which *language* exists,
        // that is what GetDocumentAsync's German-fallback flag is for. Reused PublicHealth is the
        // only anonymous, IP-partitioned policy this app already has; its budget (30/min per the
        // default appsettings.json) comfortably covers a footer link's traffic, which is at most one
        // hit per page load.
        group.MapGet("/availability", async (ILegalContentService legalContent, CancellationToken ct) =>
        {
            var availability = await legalContent.GetAvailabilityAsync(ct);
            return Results.Ok(new
            {
                imprintAvailable = availability.ImprintAvailable,
                privacyAvailable = availability.PrivacyAvailable,
            });
        }).RequireRateLimiting(RateLimitPolicyNames.PublicHealth);

        group.MapGet("/{kind}/{language}", async (
            string kind, string language, ILegalContentService legalContent, CancellationToken ct) =>
        {
            if (!TryParseKind(kind, out var parsedKind) || !TryParseLanguage(language, out var parsedLanguage))
            {
                return Results.NotFound(new { errorCode = ApiErrorCodes.LegalDocumentNotFound });
            }

            var document = await legalContent.GetDocumentAsync(parsedKind, parsedLanguage, ct);
            if (document is null)
            {
                return Results.NotFound(new { errorCode = ApiErrorCodes.LegalDocumentNotFound });
            }

            return Results.Ok(new { html = document.Html, isGermanFallback = document.IsGermanFallback });
        }).RequireRateLimiting(RateLimitPolicyNames.PublicHealth);
    }

    private static bool TryParseKind(string value, out LegalDocumentKind kind)
    {
        switch (value)
        {
            case "imprint":
                kind = LegalDocumentKind.Imprint;
                return true;
            case "privacy":
                kind = LegalDocumentKind.Privacy;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static bool TryParseLanguage(string value, out LegalLanguage language)
    {
        switch (value)
        {
            case "de":
                language = LegalLanguage.German;
                return true;
            case "en":
                language = LegalLanguage.English;
                return true;
            default:
                language = default;
                return false;
        }
    }
}
