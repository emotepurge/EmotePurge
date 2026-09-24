namespace EmotePurge.Core.Services;

/// <summary>
/// What the frontend needs to decide whether to render the contact form at all (<c>GET
/// /api/contact/config</c>). <see cref="TurnstileSiteKey"/> is public by design — Turnstile site
/// keys are meant to ship to the browser, unlike <c>Contact:Turnstile:SecretKey</c>, which never
/// leaves the server.
/// </summary>
/// <param name="Available">
/// True only once every one of SMTP host, from-address, to-address, Turnstile site key and
/// Turnstile secret key is configured (<c>ContactOptions.IsAvailable</c>) — a half-configured form
/// would either fail every submission or, worse, accept messages it cannot answer for.
/// </param>
public sealed record ContactAvailability(bool Available, string? TurnstileSiteKey);

/// <summary>The three fields of one contact-form submission, already shape-validated by the endpoint.</summary>
public sealed record ContactSubmission(string? Name, string Email, string Message);

/// <summary>
/// The one constant the endpoint (<c>ContactEndpoints</c>) and the budget it reports on
/// (Infrastructure's <c>ContactSendBudget</c>) both need — kept here, in Core, rather than duplicated
/// or referenced from Api straight into a concrete Infrastructure class, which the layering table in
/// the project's CLAUDE.md reserves for service interfaces.
/// </summary>
public static class ContactRateLimits
{
    /// <summary>
    /// A fixed heuristic, not a measured remaining cooldown — <c>ContactSendBudget</c>'s rolling
    /// window has no single next boundary to report, same reasoning <c>LiveStreamQuotaExhausted</c>
    /// documents for its own <c>retryAfterSeconds</c>.
    /// </summary>
    public const int GlobalLimitRetryAfterSeconds = 300;
}

/// <summary>
/// What <see cref="IContactSubmissionService.SubmitAsync"/> decided. The endpoint maps each value to
/// its own status code and error code — see <c>ContactEndpoints</c>.
/// </summary>
public enum ContactSubmissionOutcome
{
    /// <summary>The message was handed to the SMTP server successfully.</summary>
    Sent,

    /// <summary>The Turnstile token failed verification (a real answer from Cloudflare, not a timeout).</summary>
    CaptchaFailed,

    /// <summary>
    /// The in-process, provider-wide sending ceiling (<c>ContactSendBudget</c>) is exhausted for the
    /// current rolling window — distinct from the per-IP ASP.NET Core policy the endpoint's own
    /// <c>RequireRateLimiting</c> already enforces, same split as <c>ForeignEmoteLookup</c>'s
    /// per-user policy vs. its provider-wide budget.
    /// </summary>
    GlobalLimitReached,

    /// <summary>
    /// The feature is not configured at all, Turnstile could not be reached to verify the token, or
    /// the SMTP send itself failed. One outcome for all three: the caller cannot act on the
    /// difference between them any more than on the other "not now" codes in this codebase
    /// (<c>ForeignChannelSevenTvUnavailable</c> is the precedent).
    /// </summary>
    Unavailable,
}

/// <summary>
/// Orchestrates one contact-form submission (docs/DECISIONS.md 2026-09-24, "contact form"): checks
/// availability, verifies the Turnstile token, spends the provider-wide send budget, and — only once
/// all three pass — sends the e-mail. The budget is charged only after Turnstile succeeds (revised
/// 2026-09-24, Codex P1) — charging it first let shape-valid requests with an invalid token exhaust
/// the budget without ever verifying anything. Nothing here touches the database; the whole feature
/// is stateless besides the in-process <c>ContactSendBudget</c>.
/// </summary>
public interface IContactSubmissionService
{
    /// <summary>Synchronous and cheap — reads already-bound, already-validated configuration only.</summary>
    ContactAvailability GetAvailability();

    Task<ContactSubmissionOutcome> SubmitAsync(
        ContactSubmission submission,
        string turnstileToken,
        string? remoteIp,
        CancellationToken cancellationToken);
}
