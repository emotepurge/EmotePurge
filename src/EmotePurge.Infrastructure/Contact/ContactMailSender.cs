using EmotePurge.Core.Services;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace EmotePurge.Infrastructure.Contact;

/// <summary>
/// Sends the contact-form message over plain SMTP with MailKit — provider-neutral by construction:
/// nothing here assumes Gmail, Proton, or any other specific mailbox, only the STARTTLS/SSL vocabulary
/// every SMTP provider shares (<c>ContactOptions.SmtpOptions.Security</c>).
/// </summary>
/// <remarks>
/// A fresh <see cref="SmtpClient"/> per call, never held across requests: sends are rare enough
/// (bounded by <see cref="ContactSendBudget"/> and the endpoint's own rate-limit policy) that a
/// pooled, long-lived connection would buy nothing and would have to survive the SMTP server's own
/// idle timeout for no benefit.
/// </remarks>
public sealed class ContactMailSender(
    IOptions<ContactOptions> options,
    ILogger<ContactMailSender> logger,
    Func<ISmtpClient>? smtpClientFactory = null) : IContactMailSender
{
    // Fixed, never derived from user input (spec requirement) — a subject built from the visitor's
    // name or message would let one construct a subject line indistinguishable from a different kind
    // of notification, or simply an unreadable one.
    private const string Subject = "EmotePurge contact form";

    // Defaults to a real MailKit client; overridable only for ContactMailSenderTests (a substituted
    // ISmtpClient — MailKit's own public seam for this, implemented by SmtpClient) so the
    // send-succeeded-but-disconnect-failed path below can be exercised without a real SMTP server.
    // Same shape as ContactSendBudget's optional TimeProvider: nothing registers a Func<ISmtpClient>,
    // so DI always falls through to this default.
    private readonly Func<ISmtpClient> _smtpClientFactory = smtpClientFactory ?? (() => new SmtpClient());

    public async Task<bool> SendAsync(string? name, string email, string message, CancellationToken cancellationToken)
    {
        var config = options.Value;

        using var client = _smtpClientFactory();
        try
        {
            // Message construction (a malformed but non-blank From/To address throws here — MimeKit's
            // MailboxAddress.Parse — Codex P2, docs/DECISIONS.md 2026-09-24 revision) is deliberately
            // inside this try: ContactOptions.IsAvailable already rejects an unparseable address before
            // this method is ever reached in production, but this is the defence-in-depth half of that
            // fix, so a caller that skips the availability check gets contact_unavailable rather than a
            // 500.
            var mime = BuildMessage(
                config.FromAddress ?? throw new InvalidOperationException("Contact:FromAddress ist nicht konfiguriert."),
                config.ToAddress ?? throw new InvalidOperationException("Contact:ToAddress ist nicht konfiguriert."),
                name,
                email,
                message);

            var smtpHost = config.Smtp.Host
                ?? throw new InvalidOperationException("Contact:Smtp:Host ist nicht konfiguriert.");

            await client.ConnectAsync(
                smtpHost, config.Smtp.Port, ResolveSecureSocketOptions(config.Smtp.Security), cancellationToken);

            if (!string.IsNullOrWhiteSpace(config.Smtp.Username))
            {
                await client.AuthenticateAsync(config.Smtp.Username, config.Smtp.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
        }
        catch (Exception ex)
        {
            // Never the name, e-mail or message here — only that the send failed, per the "logs
            // never contain the message, name or e-mail address" requirement.
            logger.LogWarning(ex, "Contact form message could not be sent over SMTP.");
            return false;
        }

        // The message has already reached the server at this point — a failure disconnecting
        // afterwards (a dropped connection, the server closing first) is the server's problem, not
        // the visitor's, and must not turn an already-successful send into a reported failure that
        // has the visitor retry into a duplicate mail (Codex P2, docs/DECISIONS.md 2026-09-24
        // revision). Logged, never rethrown, and never turns `true` back into `false`.
        try
        {
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Contact form message was sent, but disconnecting from the SMTP server afterwards failed.");
        }

        return true;
    }

    /// <summary>
    /// The pure part of composing the message — no network, no MailKit <c>SmtpClient</c> — so
    /// <c>ContactMailSenderTests</c> can pin the Reply-To/subject/header-injection guarantees without
    /// a real SMTP server. Internal, exposed to <c>EmotePurge.Infrastructure.Tests</c> via
    /// <c>InternalsVisibleTo</c> like every other internal type this project tests directly.
    /// </summary>
    internal static MimeMessage BuildMessage(string fromAddress, string toAddress, string? name, string email, string message)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(fromAddress));
        mime.To.Add(MailboxAddress.Parse(toAddress));

        // Built through MimeKit's own mailbox type, never string-concatenated into a raw header —
        // MimeMessage encodes both the display name and the address itself when it serializes the
        // message, so a name or address containing a stray CR/LF or colon cannot fabricate a second
        // header line. The endpoint's own validation (ContactValidation) already rejects control
        // characters in both fields before this is ever called; this is defence in depth, not the
        // only guard.
        var displayName = string.IsNullOrWhiteSpace(name) ? email : name;
        mime.ReplyTo.Add(new MailboxAddress(displayName, email));

        mime.Subject = Subject;
        mime.Body = new TextPart("plain")
        {
            Text = $"Name: {(string.IsNullOrWhiteSpace(name) ? "(not provided)" : name)}\n"
                + $"Email: {email}\n\n{message}",
        };

        return mime;
    }

    // Case-insensitive: an operator's environment variable (CONTACT_SMTP_SECURITY) is just as likely
    // typed "starttls" as "StartTls". "None" is for the local Development catcher only (appsettings.
    // Development.json) — a real provider on the public internet needs STARTTLS or implicit TLS.
    private static SecureSocketOptions ResolveSecureSocketOptions(string security) => security.Trim() switch
    {
        var value when value.Equals("SslOnConnect", StringComparison.OrdinalIgnoreCase) => SecureSocketOptions.SslOnConnect,
        var value when value.Equals("Auto", StringComparison.OrdinalIgnoreCase) => SecureSocketOptions.Auto,
        var value when value.Equals("None", StringComparison.OrdinalIgnoreCase) => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };
}
