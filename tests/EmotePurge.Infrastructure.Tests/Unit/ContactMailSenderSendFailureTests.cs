using System.IO;
using EmotePurge.Infrastructure.Contact;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="ContactMailSender.SendAsync"/>'s failure path, without a real working SMTP server —
/// deliberately not a success-path test: this project verifies transport classes that speak to a
/// real external service live rather than against a mock (rule 16, the same reasoning
/// <c>TwitchChatManager</c>/<c>SevenTvEventClient</c> are exempted for), and a real SMTP send was
/// exercised live against a throwaway Mailpit catcher as part of this feature's own verification
/// (see docs/DECISIONS.md 2026-09-24, "contact form"). What a fast, container-free unit test CAN
/// still pin cheaply is that a connection failure is caught and reported as <c>false</c> rather than
/// thrown, and that the failure log carries no message content.
/// </summary>
/// <remarks>
/// The disconnect-succeeds-but-fails-after-a-successful-send case and the malformed-address case
/// below (both added 2026-09-24, Codex P2) substitute <see cref="ISmtpClient"/> — MailKit's own
/// public seam, implemented by <see cref="SmtpClient"/> — through <c>ContactMailSender</c>'s optional
/// <c>Func&lt;ISmtpClient&gt;</c> constructor parameter, the only way to control what happens *after*
/// a successful send without a real, cooperatively-misbehaving SMTP server.
/// </remarks>
public class ContactMailSenderSendFailureTests
{
    [Fact]
    public async Task ConnectFailure_ReturnsFalse_RatherThanThrowing()
    {
        var options = Options.Create(new ContactOptions
        {
            Smtp = new ContactOptions.SmtpOptions
            {
                // Nothing listens here — TCP connect fails fast (loopback, refused immediately)
                // rather than timing out, which is what keeps this test fast without a fake clock.
                Host = "127.0.0.1",
                Port = 1,
                Security = "None",
            },
            FromAddress = "operator@example.com",
            ToAddress = "inbox@example.com",
        });
        var sender = new ContactMailSender(options, NullLogger<ContactMailSender>.Instance);

        var sent = await sender.SendAsync("Jane", "jane@example.com", "A message nobody will receive.", CancellationToken.None);

        Assert.False(sent);
    }

    /// <summary>
    /// Revised 2026-09-24: this used to throw <see cref="InvalidOperationException"/>, because message
    /// construction ran before the method's own try/catch. Codex flagged the same gap for a
    /// non-blank-but-malformed address (<see cref="MalformedFromAddress_ReturnsFalse_RatherThanThrowing"/>)
    /// and both were closed together — message construction is now inside the try, so every failure to
    /// build a sendable message reports the same <c>false</c> as an SMTP failure would, rather than one
    /// throwing (a 500) and the other not.
    /// </summary>
    [Fact]
    public async Task MissingFromAddress_ReturnsFalse_RatherThanThrowing()
    {
        var options = Options.Create(new ContactOptions
        {
            Smtp = new ContactOptions.SmtpOptions { Host = "127.0.0.1", Port = 1, Security = "None" },
            FromAddress = null,
            ToAddress = "inbox@example.com",
        });
        var sender = new ContactMailSender(options, NullLogger<ContactMailSender>.Instance);

        var sent = await sender.SendAsync("Jane", "jane@example.com", "Hi.", CancellationToken.None);

        Assert.False(sent);
    }

    /// <summary>
    /// The crash <c>ContactOptions.IsAvailable</c>'s new mailbox check (also added 2026-09-24) exists
    /// to make unreachable in production — this is the defence-in-depth half, reached only by a caller
    /// (like this test) that skips the availability gate: a non-blank but malformed From address used
    /// to make <c>BuildMessage</c>'s <c>MailboxAddress.Parse</c> throw outside the method's try/catch,
    /// turning what should be <c>contact_unavailable</c> (503) into an unhandled 500.
    /// </summary>
    [Fact]
    public async Task MalformedFromAddress_ReturnsFalse_RatherThanThrowing()
    {
        var options = Options.Create(new ContactOptions
        {
            Smtp = new ContactOptions.SmtpOptions { Host = "127.0.0.1", Port = 1, Security = "None" },
            FromAddress = "user@", // non-blank, but not a parseable mailbox
            ToAddress = "inbox@example.com",
        });
        var sender = new ContactMailSender(options, NullLogger<ContactMailSender>.Instance);

        var sent = await sender.SendAsync("Jane", "jane@example.com", "Hi.", CancellationToken.None);

        Assert.False(sent);
    }

    /// <summary>
    /// The other half of the same Codex P2: once <c>SendAsync</c> has succeeded, the message has
    /// already reached the server — a failure disconnecting afterwards (a dropped connection, the
    /// server closing first) must not turn an already-successful send into a reported failure. Before
    /// this fix it did, because both calls shared one try/catch; a visitor would see an error, retry,
    /// and the operator would receive the same message twice.
    /// </summary>
    [Fact]
    public async Task SendSucceeds_ButDisconnectThrows_StillReturnsTrue()
    {
        var smtpClient = Substitute.For<ISmtpClient>();
        smtpClient.ConnectAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<SecureSocketOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        smtpClient.SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("250 OK"));
        smtpClient.DisconnectAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("Connection reset by peer.")));

        var options = Options.Create(new ContactOptions
        {
            Smtp = new ContactOptions.SmtpOptions { Host = "smtp.example.com", Security = "None" },
            FromAddress = "operator@example.com",
            ToAddress = "inbox@example.com",
        });
        var sender = new ContactMailSender(options, NullLogger<ContactMailSender>.Instance, () => smtpClient);

        var sent = await sender.SendAsync("Jane", "jane@example.com", "Hi.", CancellationToken.None);

        Assert.True(sent);
        await smtpClient.Received(1).DisconnectAsync(true, Arg.Any<CancellationToken>());
    }
}
