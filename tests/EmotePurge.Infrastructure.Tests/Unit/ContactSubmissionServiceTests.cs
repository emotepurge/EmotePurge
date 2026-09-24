using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Contact;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="ContactSubmissionService"/>'s orchestration: availability, then the global send
/// budget, then Turnstile, then — only if all three pass — the mail send. <see cref="ITurnstileVerifier"/>
/// and <see cref="IContactMailSender"/> are substituted; the real, un-substituted collaborator is
/// <see cref="ContactSendBudget"/>, since its own behaviour is already pinned by
/// <c>ContactSendBudgetTests</c> and this class is exactly the seam that decides when it gets charged.
/// </summary>
public class ContactSubmissionServiceTests
{
    private static readonly ContactSubmission Submission = new("Jane", "jane@example.com", "Hello there, this is a message.");

    [Fact]
    public async Task NotConfigured_ReturnsUnavailable_WithoutCallingTurnstileOrMail()
    {
        var turnstile = Substitute.For<ITurnstileVerifier>();
        var mailSender = Substitute.For<IContactMailSender>();
        var service = CreateService(new ContactOptions(), turnstile, mailSender, out _);

        var outcome = await service.SubmitAsync(Submission, "token", "203.0.113.1", CancellationToken.None);

        Assert.Equal(ContactSubmissionOutcome.Unavailable, outcome);
        await turnstile.DidNotReceive().VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await mailSender.DidNotReceive().SendAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Revised 2026-09-24 (Codex P1): the budget is now charged only after Turnstile succeeds, so an
    /// exhausted budget can only ever be reached once a real Turnstile verification already passed —
    /// unlike the earlier ordering, this outcome no longer implies the token was never checked.
    /// </summary>
    [Fact]
    public async Task GlobalBudgetExhausted_ReturnsGlobalLimitReached_AfterTurnstileSucceeds_WithoutSendingMail()
    {
        var turnstile = Substitute.For<ITurnstileVerifier>();
        turnstile.VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(TurnstileVerificationResult.Success);
        var mailSender = Substitute.For<IContactMailSender>();
        var service = CreateService(FullyConfigured(), turnstile, mailSender, out var budget, maxSends: 1);
        Assert.True(budget.TryCharge(out _)); // exhaust the one-permit budget before the service ever runs

        var outcome = await service.SubmitAsync(Submission, "token", "203.0.113.1", CancellationToken.None);

        Assert.Equal(ContactSubmissionOutcome.GlobalLimitReached, outcome);
        await turnstile.Received(1).VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await mailSender.DidNotReceive().SendAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The regression this revision closes (Codex P1): charging the budget before Turnstile ran let a
    /// burst of shape-valid requests carrying an invalid token exhaust the one shared, provider-wide
    /// budget for the whole window — locking out every legitimate visitor for up to an hour — without
    /// ever costing a real Turnstile verification. Thirty failed-captcha attempts against a one-permit
    /// budget must leave that permit untouched.
    /// </summary>
    [Fact]
    public async Task TurnstileFailsRepeatedly_NeverChargesTheGlobalBudget()
    {
        var turnstile = Substitute.For<ITurnstileVerifier>();
        turnstile.VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(TurnstileVerificationResult.Failed);
        var mailSender = Substitute.For<IContactMailSender>();
        var service = CreateService(FullyConfigured(), turnstile, mailSender, out var budget, maxSends: 1);

        for (var attempt = 0; attempt < 30; attempt++)
        {
            var outcome = await service.SubmitAsync(
                Submission, "bad-token", $"203.0.113.{attempt}", CancellationToken.None);
            Assert.Equal(ContactSubmissionOutcome.CaptchaFailed, outcome);
        }

        // The one-permit budget must still be fully intact — none of the thirty failed-captcha
        // attempts spent it.
        Assert.True(budget.TryCharge(out _));
        await mailSender.DidNotReceive().SendAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TurnstileRejectsTheToken_ReturnsCaptchaFailed_WithoutSendingMail()
    {
        var turnstile = Substitute.For<ITurnstileVerifier>();
        turnstile.VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(TurnstileVerificationResult.Failed);
        var mailSender = Substitute.For<IContactMailSender>();
        var service = CreateService(FullyConfigured(), turnstile, mailSender, out _);

        var outcome = await service.SubmitAsync(Submission, "bad-token", "203.0.113.1", CancellationToken.None);

        Assert.Equal(ContactSubmissionOutcome.CaptchaFailed, outcome);
        await mailSender.DidNotReceive().SendAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TurnstileUnreachable_ReturnsUnavailable_WithoutSendingMail()
    {
        var turnstile = Substitute.For<ITurnstileVerifier>();
        turnstile.VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(TurnstileVerificationResult.Unavailable);
        var mailSender = Substitute.For<IContactMailSender>();
        var service = CreateService(FullyConfigured(), turnstile, mailSender, out _);

        var outcome = await service.SubmitAsync(Submission, "token", "203.0.113.1", CancellationToken.None);

        Assert.Equal(ContactSubmissionOutcome.Unavailable, outcome);
        await mailSender.DidNotReceive().SendAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EverythingPasses_SendsMail_ReturnsSent()
    {
        var turnstile = Substitute.For<ITurnstileVerifier>();
        turnstile.VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(TurnstileVerificationResult.Success);
        var mailSender = Substitute.For<IContactMailSender>();
        mailSender.SendAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(FullyConfigured(), turnstile, mailSender, out _);

        var outcome = await service.SubmitAsync(Submission, "token", "203.0.113.1", CancellationToken.None);

        Assert.Equal(ContactSubmissionOutcome.Sent, outcome);
        await mailSender.Received(1).SendAsync("Jane", "jane@example.com", "Hello there, this is a message.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MailSendFails_ReturnsUnavailable()
    {
        var turnstile = Substitute.For<ITurnstileVerifier>();
        turnstile.VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(TurnstileVerificationResult.Success);
        var mailSender = Substitute.For<IContactMailSender>();
        mailSender.SendAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var service = CreateService(FullyConfigured(), turnstile, mailSender, out _);

        var outcome = await service.SubmitAsync(Submission, "token", "203.0.113.1", CancellationToken.None);

        Assert.Equal(ContactSubmissionOutcome.Unavailable, outcome);
    }

    /// <summary>
    /// The refund half of the same revision (Codex P1): only a message that actually left for the
    /// operator's mailbox should count against the budget. With a one-permit budget, a failed send
    /// must give its reservation back — otherwise a single transient SMTP hiccup would cost the next
    /// hour's worth of legitimate visitors their only permit for nothing.
    /// </summary>
    [Fact]
    public async Task MailSendFails_RefundsTheReservedPermit()
    {
        var turnstile = Substitute.For<ITurnstileVerifier>();
        turnstile.VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(TurnstileVerificationResult.Success);
        var mailSender = Substitute.For<IContactMailSender>();
        mailSender.SendAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var service = CreateService(FullyConfigured(), turnstile, mailSender, out var budget, maxSends: 1);

        var outcome = await service.SubmitAsync(Submission, "token", "203.0.113.1", CancellationToken.None);

        Assert.Equal(ContactSubmissionOutcome.Unavailable, outcome);
        Assert.True(budget.TryCharge(out _));
    }

    [Fact]
    public void GetAvailability_NotConfigured_ReportsUnavailable_WithNullSiteKey()
    {
        var service = CreateService(new ContactOptions(), Substitute.For<ITurnstileVerifier>(), Substitute.For<IContactMailSender>(), out _);

        var availability = service.GetAvailability();

        Assert.False(availability.Available);
        Assert.Null(availability.TurnstileSiteKey);
    }

    [Fact]
    public void GetAvailability_Configured_ReportsAvailable_WithThePublicSiteKey()
    {
        var options = FullyConfigured();
        var service = CreateService(options, Substitute.For<ITurnstileVerifier>(), Substitute.For<IContactMailSender>(), out _);

        var availability = service.GetAvailability();

        Assert.True(availability.Available);
        Assert.Equal(options.Turnstile.SiteKey, availability.TurnstileSiteKey);
    }

    private static ContactOptions FullyConfigured() => new()
    {
        Smtp = new ContactOptions.SmtpOptions { Host = "smtp.example.com" },
        FromAddress = "operator@example.com",
        ToAddress = "inbox@example.com",
        Turnstile = new ContactOptions.TurnstileOptions { SiteKey = "site", SecretKey = "secret" },
    };

    private static ContactSubmissionService CreateService(
        ContactOptions options,
        ITurnstileVerifier turnstile,
        IContactMailSender mailSender,
        out ContactSendBudget budget,
        int maxSends = 30)
    {
        budget = new ContactSendBudget(maxSends);
        return new ContactSubmissionService(
            turnstile, mailSender, budget, Options.Create(options), NullLogger<ContactSubmissionService>.Instance);
    }
}
