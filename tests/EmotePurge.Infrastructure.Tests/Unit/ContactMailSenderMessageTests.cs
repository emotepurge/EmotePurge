using EmotePurge.Infrastructure.Contact;
using MimeKit;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="ContactMailSender.BuildMessage"/> in isolation — the pure message-composition step,
/// with no network and no MailKit <c>SmtpClient</c> involved. Pins the three guarantees the spec
/// calls out: Reply-To carries the visitor's address, the subject is always the fixed string, and a
/// name/message containing CR/LF cannot fabricate a second header.
/// </summary>
public class ContactMailSenderMessageTests
{
    private const string FromAddress = "operator@example.com";
    private const string ToAddress = "inbox@example.com";

    [Fact]
    public void ReplyTo_IsSetToTheVisitorsAddress_WithTheirNameAsDisplayName()
    {
        var mime = ContactMailSender.BuildMessage(FromAddress, ToAddress, "Jane Visitor", "jane@example.com", "Hello there.");

        var replyTo = Assert.Single(mime.ReplyTo.Mailboxes);
        Assert.Equal("jane@example.com", replyTo.Address);
        Assert.Equal("Jane Visitor", replyTo.Name);
    }

    [Fact]
    public void NoNameProvided_ReplyToDisplayNameFallsBackToTheEmailAddress()
    {
        var mime = ContactMailSender.BuildMessage(FromAddress, ToAddress, null, "jane@example.com", "Hello there.");

        var replyTo = Assert.Single(mime.ReplyTo.Mailboxes);
        Assert.Equal("jane@example.com", replyTo.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Jane Visitor")]
    [InlineData("Someone with a very different name entirely")]
    public void Subject_IsAlwaysTheFixedString_NeverDerivedFromInput(string? name)
    {
        var mime = ContactMailSender.BuildMessage(FromAddress, ToAddress, name, "jane@example.com", "Whatever the visitor typed as a subject: FAKE-SUBJECT");

        Assert.Equal("EmotePurge contact form", mime.Subject);
    }

    [Fact]
    public void FromAndTo_AreTheConfiguredOperatorAddresses()
    {
        var mime = ContactMailSender.BuildMessage(FromAddress, ToAddress, "Jane", "jane@example.com", "Hi.");

        Assert.Equal(FromAddress, Assert.Single(mime.From.Mailboxes).Address);
        Assert.Equal(ToAddress, Assert.Single(mime.To.Mailboxes).Address);
    }

    [Fact]
    public void Body_ContainsNameEmailAndMessage()
    {
        var mime = ContactMailSender.BuildMessage(FromAddress, ToAddress, "Jane Visitor", "jane@example.com", "The actual message body.");

        var text = mime.TextBody;
        Assert.Contains("Jane Visitor", text, StringComparison.Ordinal);
        Assert.Contains("jane@example.com", text, StringComparison.Ordinal);
        Assert.Contains("The actual message body.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Defence in depth: the endpoint's own validation (<c>ContactValidation</c>) already rejects
    /// control characters in name/e-mail before this is ever reached, but this pins that even a name
    /// carrying a raw CR/LF cannot produce a second, real header — MimeKit's mailbox type encodes the
    /// display name rather than splicing it into the raw header text. The name still reaches the body
    /// as plain data (that is by design, so the operator sees it) — what must never happen is a
    /// second entry in the strongly-typed header collection MailKit's <c>SmtpClient</c> actually
    /// sends, which is what this asserts against rather than a raw-string search over the whole
    /// message (the body legitimately contains the string "Bcc:" here, and that is not the bug).
    /// </summary>
    [Fact]
    public void NameContainingCrLf_CannotInjectASecondHeader()
    {
        const string maliciousName = "Jane\r\nBcc: victim@example.com";
        var mime = ContactMailSender.BuildMessage(FromAddress, ToAddress, maliciousName, "jane@example.com", "Hi.");

        Assert.False(mime.Headers.Contains(HeaderId.Bcc));
        Assert.Single(mime.ReplyTo.Mailboxes);
        Assert.Equal("jane@example.com", mime.ReplyTo.Mailboxes.Single().Address);
    }

    [Fact]
    public void MessageContainingCrLf_CannotInjectAHeader_EvenThoughItAppearsAsBodyText()
    {
        const string maliciousMessage = "Hello.\r\nBcc: victim@example.com\r\nActual message.";
        var mime = ContactMailSender.BuildMessage(FromAddress, ToAddress, "Jane", "jane@example.com", maliciousMessage);

        Assert.False(mime.Headers.Contains(HeaderId.Bcc));
        // It is expected to appear in the body — that is the field working as intended, not a leak.
        Assert.Contains("Bcc: victim@example.com", mime.TextBody, StringComparison.Ordinal);
    }
}
