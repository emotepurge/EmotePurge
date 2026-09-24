using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmotePurge.Infrastructure.Contact;

/// <summary>
/// The one place that decides what happens to a contact-form submission: availability, then the
/// Turnstile token, then the provider-wide send budget, then — only if all three pass — the SMTP
/// send. See <see cref="ContactSubmissionOutcome"/> for what each step's failure maps to.
/// </summary>
/// <remarks>
/// The budget is charged only after Turnstile succeeds, not before — revised 2026-09-24 (Codex P1):
/// charging first let thirty shape-valid requests carrying an invalid token, sent from a rotating
/// pool of IPs, exhaust the one shared, provider-wide budget for a whole hour without ever costing a
/// real Turnstile verification or an SMTP round trip. A send that is reserved but then fails at the
/// SMTP step refunds its permit (<see cref="ContactSendBudget.Release"/>) — only a message that
/// actually left for the operator's mailbox should count against the ceiling.
/// </remarks>
public sealed class ContactSubmissionService(
    ITurnstileVerifier turnstileVerifier,
    IContactMailSender mailSender,
    ContactSendBudget sendBudget,
    IOptions<ContactOptions> options,
    ILogger<ContactSubmissionService> logger) : IContactSubmissionService
{
    public ContactAvailability GetAvailability()
    {
        var config = options.Value;
        return config.IsAvailable
            ? new ContactAvailability(Available: true, config.Turnstile.SiteKey)
            : new ContactAvailability(Available: false, TurnstileSiteKey: null);
    }

    public async Task<ContactSubmissionOutcome> SubmitAsync(
        ContactSubmission submission,
        string turnstileToken,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        if (!options.Value.IsAvailable)
        {
            return ContactSubmissionOutcome.Unavailable;
        }

        var verification = await turnstileVerifier.VerifyAsync(turnstileToken, remoteIp, cancellationToken);
        switch (verification)
        {
            case TurnstileVerificationResult.Failed:
                return ContactSubmissionOutcome.CaptchaFailed;
            case TurnstileVerificationResult.Unavailable:
                return ContactSubmissionOutcome.Unavailable;
        }

        // Only once Turnstile has actually verified this caller does a permit get spent — see the
        // class remarks for why the order matters. Still before the SMTP send: a caller must not be
        // able to spend that round trip without first reserving a place in the window.
        if (!sendBudget.TryCharge())
        {
            logger.LogWarning("Contact form global send budget exhausted for the current window.");
            return ContactSubmissionOutcome.GlobalLimitReached;
        }

        var sent = await mailSender.SendAsync(submission.Name, submission.Email, submission.Message, cancellationToken);
        if (!sent)
        {
            // The reservation above was for a send that never happened — give it back rather than
            // letting an SMTP hiccup also cost the visitor (and everyone behind them) a permit.
            sendBudget.Release();
            return ContactSubmissionOutcome.Unavailable;
        }

        // Outcome only — never the name, e-mail or message, per the "logs never contain the
        // message, name or e-mail address" requirement.
        logger.LogInformation("Contact form message sent.");
        return ContactSubmissionOutcome.Sent;
    }
}
