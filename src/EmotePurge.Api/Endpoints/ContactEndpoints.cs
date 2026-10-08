using System.Globalization;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// The contact form (docs/DECISIONS.md 2026-09-24, "contact form"): a second, electronic route to
/// reach the operator, next to the e-mail address in the imprint — required by § 5 DDG /
/// EuGH C-298/07. Both routes are anonymous, like <c>LegalEndpoints</c>: reachable before login,
/// which is the whole point.
/// </summary>
public static class ContactEndpoints
{
    public static void MapContactEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/contact");

        // Shares PublicLegal's budget rather than getting one of its own: this is the same shape of
        // traffic as GET /api/legal/availability — a cheap, anonymous, once-per-page-view read that
        // only tells the frontend whether to render something — not the POST route below, which is
        // the one that can trigger a real Turnstile call and an SMTP send.
        group.MapGet("/config", (IContactSubmissionService contactService) =>
        {
            var availability = contactService.GetAvailability();
            return Results.Ok(new
            {
                available = availability.Available,
                turnstileSiteKey = availability.TurnstileSiteKey,
            });
        }).RequireRateLimiting(RateLimitPolicyNames.PublicLegal);

        group.MapPost("", async (
            ContactRequest request,
            HttpContext httpContext,
            IContactSubmissionService contactService,
            CancellationToken ct) =>
        {
            // Honeypot (spec requirement): answered as an unconditional success, before anything
            // else runs — no shape validation, no availability check, no Turnstile call. A bot that
            // fills every field, including this hidden one, gets no signal that it was caught.
            if (!string.IsNullOrEmpty(request.Website))
            {
                return Results.NoContent();
            }

            var name = request.Name?.Trim();
            var email = request.Email?.Trim() ?? string.Empty;
            var message = request.Message ?? string.Empty;

            if (!ContactValidation.IsValidName(name)
                || !ContactValidation.IsValidEmail(email)
                || !ContactValidation.IsValidMessage(message)
                || string.IsNullOrWhiteSpace(request.TurnstileToken))
            {
                return Results.Json(
                    new { errorCode = ApiErrorCodes.ContactInvalid }, statusCode: StatusCodes.Status400BadRequest);
            }

            // The client IP as this app already resolves it behind the reverse proxy — correct only
            // because of the ForwardedHeadersMiddleware trust configuration in this same file, same
            // source the rate limiter's own IP partition (RateLimitRejection.ResolveUserKey) uses.
            var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();

            var outcome = await contactService.SubmitAsync(
                new ContactSubmission(name, email, message.Trim()), request.TurnstileToken, remoteIp, ct);

            switch (outcome)
            {
                case ContactSubmissionOutcome.Sent:
                    return Results.NoContent();
                case ContactSubmissionOutcome.CaptchaFailed:
                    return Results.Json(
                        new { errorCode = ApiErrorCodes.ContactCaptchaFailed }, statusCode: StatusCodes.Status400BadRequest);
                case ContactSubmissionOutcome.GlobalLimitReached:
                    // Shaped like ChannelResyncCooldown's 429 (ChannelEndpoints.cs): a body plus a
                    // Retry-After header, distinct from the per-IP policy's bare 429 the rate limiter
                    // middleware already answers for a spent Contact budget — reusing that same
                    // errorCode because a caller cannot act on the two any differently.
                    httpContext.Response.Headers.RetryAfter = ContactRateLimits.GlobalLimitRetryAfterSeconds
                        .ToString(CultureInfo.InvariantCulture);
                    return Results.Json(
                        new
                        {
                            errorCode = ApiErrorCodes.RateLimitExceeded,
                            retryAfterSeconds = ContactRateLimits.GlobalLimitRetryAfterSeconds,
                        },
                        statusCode: StatusCodes.Status429TooManyRequests);
                default:
                    return Results.Json(
                        new { errorCode = ApiErrorCodes.ContactUnavailable }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireRateLimiting(RateLimitPolicyNames.Contact);
    }
}

internal sealed record ContactRequest(string? Name, string? Email, string? Message, string? TurnstileToken, string? Website);
