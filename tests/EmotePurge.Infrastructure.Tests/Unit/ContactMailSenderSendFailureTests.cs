using EmotePurge.Infrastructure.Contact;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    [Fact]
    public async Task MissingFromAddress_ThrowsRatherThanSendingWithAnEmptyEnvelope()
    {
        var options = Options.Create(new ContactOptions
        {
            Smtp = new ContactOptions.SmtpOptions { Host = "127.0.0.1", Port = 1, Security = "None" },
            FromAddress = null,
            ToAddress = "inbox@example.com",
        });
        var sender = new ContactMailSender(options, NullLogger<ContactMailSender>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync("Jane", "jane@example.com", "Hi.", CancellationToken.None));
    }
}
