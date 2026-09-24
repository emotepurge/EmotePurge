namespace EmotePurge.Infrastructure.Contact;

/// <summary>
/// Bound from the <c>Contact</c> configuration section. Like <c>LegalContentOptions</c>, deliberately
/// a plain bag with no <c>Validate()</c> that throws at startup: an unconfigured contact form is a
/// supported operating mode (a fresh self-hosted instance before the operator has picked an SMTP
/// account), not a startup error — see <see cref="IsAvailable"/>.
/// </summary>
public sealed class ContactOptions
{
    public const string SectionName = "Contact";

    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>The envelope/header From address — the operator's own mailbox, never the visitor's.</summary>
    public string? FromAddress { get; set; }

    /// <summary>Where every submission is delivered — the operator's chosen inbox.</summary>
    public string? ToAddress { get; set; }

    public TurnstileOptions Turnstile { get; set; } = new();

    /// <summary>
    /// The feature is "available" — <c>GET /api/contact/config</c> reports it, and <c>POST
    /// /api/contact</c> accepts submissions — only once every one of these five values is set. A
    /// partial configuration (say, SMTP without a Turnstile secret) would either 500 on first use or
    /// accept unverified submissions; neither is a state this app should ever be in silently.
    /// </summary>
    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(Smtp.Host)
        && !string.IsNullOrWhiteSpace(FromAddress)
        && !string.IsNullOrWhiteSpace(ToAddress)
        && !string.IsNullOrWhiteSpace(Turnstile.SiteKey)
        && !string.IsNullOrWhiteSpace(Turnstile.SecretKey);

    public sealed class SmtpOptions
    {
        public string? Host { get; set; }

        public int Port { get; set; } = 587;

        /// <summary>Unset means no authentication — the local Development catcher (docs/Operations.md).</summary>
        public string? Username { get; set; }

        public string? Password { get; set; }

        /// <summary>
        /// One of <c>StartTls</c>, <c>SslOnConnect</c>, <c>Auto</c>, <c>None</c> (case-insensitive) —
        /// mirrors MailKit's <c>SecureSocketOptions</c> vocabulary directly rather than inventing a
        /// new one, since the operator picks this value while reading MailKit's own SMTP provider
        /// examples. <c>None</c> exists only for the local Development catcher (no real provider on
        /// the public internet should ever use it). Anything unrecognized falls back to
        /// <c>StartTls</c>, the common case for a real mail provider on port 587 (see
        /// <c>ContactMailSender.ResolveSecureSocketOptions</c>).
        /// </summary>
        public string Security { get; set; } = "StartTls";
    }

    public sealed class TurnstileOptions
    {
        /// <summary>Public by design — shipped to the browser via <c>GET /api/contact/config</c>.</summary>
        public string? SiteKey { get; set; }

        /// <summary>Never leaves the server — used only by <c>TurnstileVerifier</c>'s siteverify call.</summary>
        public string? SecretKey { get; set; }
    }
}
