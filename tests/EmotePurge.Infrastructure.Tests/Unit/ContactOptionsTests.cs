using EmotePurge.Infrastructure.Contact;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="ContactOptions.IsAvailable"/>: the feature is available only once every one of the
/// five required values is set — a partial configuration must read the same as "nothing configured".
/// </summary>
public class ContactOptionsTests
{
    private static ContactOptions FullyConfigured() => new()
    {
        Smtp = new ContactOptions.SmtpOptions { Host = "smtp.example.com" },
        FromAddress = "operator@example.com",
        ToAddress = "inbox@example.com",
        Turnstile = new ContactOptions.TurnstileOptions { SiteKey = "site", SecretKey = "secret" },
    };

    [Fact]
    public void EveryValueSet_IsAvailable()
    {
        Assert.True(FullyConfigured().IsAvailable);
    }

    [Fact]
    public void DefaultOptions_NothingSet_IsNotAvailable()
    {
        Assert.False(new ContactOptions().IsAvailable);
    }

    [Theory]
    [InlineData("Smtp.Host")]
    [InlineData("FromAddress")]
    [InlineData("ToAddress")]
    [InlineData("Turnstile.SiteKey")]
    [InlineData("Turnstile.SecretKey")]
    public void AnySingleMissingValue_MakesItUnavailable(string missing)
    {
        var options = FullyConfigured();
        switch (missing)
        {
            case "Smtp.Host":
                options.Smtp.Host = null;
                break;
            case "FromAddress":
                options.FromAddress = null;
                break;
            case "ToAddress":
                options.ToAddress = null;
                break;
            case "Turnstile.SiteKey":
                options.Turnstile.SiteKey = null;
                break;
            case "Turnstile.SecretKey":
                options.Turnstile.SecretKey = null;
                break;
        }

        Assert.False(options.IsAvailable);
    }

    [Fact]
    public void WhitespaceOnlyValue_CountsAsMissing()
    {
        var options = FullyConfigured();
        options.FromAddress = "   ";

        Assert.False(options.IsAvailable);
    }

    /// <summary>
    /// Added 2026-09-24 (Codex P2): a non-blank but malformed From/To address used to read as
    /// "available" here and then throw once <c>ContactMailSender.BuildMessage</c> parsed the very same
    /// string with MimeKit — a 500 on every submission instead of the intended <c>contact_unavailable</c>
    /// 503. Both examples are realistic operator typos in an environment variable (a truncated
    /// <c>CONTACT_FROM_ADDRESS=user@</c>, a leading <c>CONTACT_TO_ADDRESS=@example.com</c>), not
    /// contrived strings — <c>MailboxAddress.TryParse</c> rejects both.
    /// </summary>
    [Theory]
    [InlineData("user@")]
    [InlineData("@example.com")]
    [InlineData("two spaced words")]
    public void MalformedFromAddress_MakesItUnavailable(string malformed)
    {
        var options = FullyConfigured();
        options.FromAddress = malformed;

        Assert.False(options.IsAvailable);
    }

    [Theory]
    [InlineData("user@")]
    [InlineData("@example.com")]
    public void MalformedToAddress_MakesItUnavailable(string malformed)
    {
        var options = FullyConfigured();
        options.ToAddress = malformed;

        Assert.False(options.IsAvailable);
    }
}
