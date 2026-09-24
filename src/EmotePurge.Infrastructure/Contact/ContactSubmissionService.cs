using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmotePurge.Infrastructure.Contact;

/// <summary>
/// The one place that decides what happens to a contact-form submission: availability, then the
/// provider-wide send budget, then the Turnstile token, then — only if all three pass — the SMTP
/// send. See <see cref="ContactSubmissionOutcome"/> for what each step's failure maps to.
/// </summary>
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

        // Charged before the Turnstile call and before the SMTP send — a caller must not be able to
        // spend either resource once the global ceiling for this window is already gone.
        if (!sendBudget.TryCharge())
        {
            logger.LogWarning("Contact form global send budget exhausted for the current window.");
            return ContactSubmissionOutcome.GlobalLimitReached;
        }

        var verification = await turnstileVerifier.VerifyAsync(turnstileToken, remoteIp, cancellationToken);
        switch (verification)
        {
            case TurnstileVerificationResult.Failed:
                return ContactSubmissionOutcome.CaptchaFailed;
            case TurnstileVerificationResult.Unavailable:
                return ContactSubmissionOutcome.Unavailable;
        }

        var sent = await mailSender.SendAsync(submission.Name, submission.Email, submission.Message, cancellationToken);
        if (!sent)
        {
            return ContactSubmissionOutcome.Unavailable;
        }

        // Outcome only — never the name, e-mail or message, per the "logs never contain the
        // message, name or e-mail address" requirement.
        logger.LogInformation("Contact form message sent.");
        return ContactSubmissionOutcome.Sent;
    }
}
