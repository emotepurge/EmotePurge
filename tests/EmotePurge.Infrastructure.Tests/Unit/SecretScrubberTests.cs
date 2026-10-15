using Xunit;
using EmotePurge.Infrastructure.Logging;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class SecretScrubberTests
{
    [Fact]
    public void Scrub_ConnectionStringPassword_RedactsValueUpToSemicolon()
    {
        Assert.Equal("Password=[redacted];Host=x", SecretScrubber.Scrub("Password=abc;Host=x"));
    }

    [Fact]
    public void Scrub_PasswordAtLineEnd_RedactsToLineBreak()
    {
        Assert.Equal("Host=x;password=[redacted]\nnext", SecretScrubber.Scrub("Host=x;password=s3cr3t pw\nnext"));
    }

    [Fact]
    public void Scrub_OAuthToken_Redacted()
    {
        var result = SecretScrubber.Scrub("PASS oauth:abc123 sent");
        Assert.DoesNotContain("abc123", result);
        Assert.Contains("[redacted]", result);
    }

    [Fact]
    public void Scrub_BearerJwt_Redacted()
    {
        var result = SecretScrubber.Scrub("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig");
        Assert.DoesNotContain("eyJ", result);
        Assert.StartsWith("Authorization: Bearer ", result);
    }

    [Theory]
    [InlineData("access_token=abc123xyz&x=1", "access_token=[redacted]&x=1")]
    [InlineData("refresh_token=abc123xyz", "refresh_token=[redacted]")]
    [InlineData("client_secret=abc123xyz next", "client_secret=[redacted] next")]
    public void Scrub_NamedSecrets_RedactValue(string input, string expected)
    {
        Assert.Equal(expected, SecretScrubber.Scrub(input));
    }

    [Theory]
    [InlineData("password reset failed")]
    [InlineData("Bearer of bad news")]
    [InlineData("Joined channel handofblood (category EmotePurge.Worker.TwitchChatManager)")]
    [InlineData("")]
    public void Scrub_NonSecrets_Unchanged(string input)
    {
        Assert.Equal(input, SecretScrubber.Scrub(input));
    }
}
