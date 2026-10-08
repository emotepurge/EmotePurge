namespace EmotePurge.Core.Services;

/// <summary>
/// Sends exactly one contact-form message over SMTP. Implemented with MailKit in Infrastructure
/// (<c>ContactMailSender</c>) — this interface is what keeps <c>ContactSubmissionService</c> and its
/// tests free of MailKit's own types.
/// </summary>
public interface IContactMailSender
{
    /// <param name="name">
    /// The visitor's name, optional. Never placed in the subject line (fixed, never derived from
    /// user input) — only in the plain-text body and as the display name of the Reply-To address.
    /// </param>
    /// <param name="email">
    /// The visitor's address. Already validated by the endpoint (syntax, length, no control
    /// characters) before this is called, but the Reply-To header is still built through a proper
    /// mailbox type rather than string concatenation — defence in depth, not the only guard.
    /// </param>
    /// <param name="message">The visitor's message, placed in the body only, never in a header.</param>
    /// <returns><see langword="false"/> on any SMTP failure — connect, authenticate, or send.</returns>
    Task<bool> SendAsync(string? name, string email, string message, CancellationToken cancellationToken);
}
