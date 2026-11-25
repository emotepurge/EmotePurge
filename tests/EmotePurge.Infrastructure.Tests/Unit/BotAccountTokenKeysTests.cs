using System.Security.Cryptography;
using EmotePurge.Infrastructure.BotAccount;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class BotAccountTokenKeysTests
{
    [Fact]
    public void ImportsBothHalvesOfA3072BitPair()
    {
        using var publicKey = BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey);
        using var privateKey = BotAccountTokenKeys.ImportPrivateKey(BotAccountTestKeys.PrivateKey);

        Assert.Equal(3072, publicKey.KeySize);
        Assert.Equal(3072, privateKey.KeySize);
    }

    [Fact]
    public void ToleratesSurroundingWhitespace()
    {
        using var publicKey = BotAccountTokenKeys.ImportPublicKey("  " + BotAccountTestKeys.PublicKey + "\n");

        Assert.Equal(3072, publicKey.KeySize);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingKey_NamesTheKey(string? value)
    {
        AssertRejected(() => BotAccountTokenKeys.ImportPublicKey(value), BotAccountTokenKeys.PublicKeyName, value);
        AssertRejected(() => BotAccountTokenKeys.ImportPrivateKey(value), BotAccountTokenKeys.PrivateKeyName, value);
    }

    [Fact]
    public void NotBase64_IsRejected()
    {
        const string value = "this-is-not-base64-at-all!";

        AssertRejected(() => BotAccountTokenKeys.ImportPublicKey(value), BotAccountTokenKeys.PublicKeyName, value);
        AssertRejected(() => BotAccountTokenKeys.ImportPrivateKey(value), BotAccountTokenKeys.PrivateKeyName, value);
    }

    [Fact]
    public void Pem_IsRejected()
    {
        using var key = RSA.Create(3072);
        var pem = key.ExportSubjectPublicKeyInfoPem();

        AssertRejected(() => BotAccountTokenKeys.ImportPublicKey(pem), BotAccountTokenKeys.PublicKeyName, pem);
    }

    [Fact]
    public void Base64OfSomethingElse_IsRejected()
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(400));

        AssertRejected(() => BotAccountTokenKeys.ImportPublicKey(value), BotAccountTokenKeys.PublicKeyName, value);
        AssertRejected(() => BotAccountTokenKeys.ImportPrivateKey(value), BotAccountTokenKeys.PrivateKeyName, value);
    }

    [Fact]
    public void A2048BitKey_IsRejected()
    {
        AssertRejected(() => BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.SmallPublicKey),
            BotAccountTokenKeys.PublicKeyName, BotAccountTestKeys.SmallPublicKey, "2048");
        AssertRejected(() => BotAccountTokenKeys.ImportPrivateKey(BotAccountTestKeys.SmallPrivateKey),
            BotAccountTokenKeys.PrivateKeyName, BotAccountTestKeys.SmallPrivateKey, "2048");
    }

    [Fact]
    public void ThePublicKeyAsPrivateKey_IsRejected() =>
        AssertRejected(() => BotAccountTokenKeys.ImportPrivateKey(BotAccountTestKeys.PublicKey),
            BotAccountTokenKeys.PrivateKeyName, BotAccountTestKeys.PublicKey);

    [Fact]
    public void ThePrivateKeyAsPublicKey_IsRejected() =>
        AssertRejected(() => BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PrivateKey),
            BotAccountTokenKeys.PublicKeyName, BotAccountTestKeys.PrivateKey);

    [Fact]
    public void TrailingBytes_AreRejected()
    {
        var value = Convert.ToBase64String([.. Convert.FromBase64String(BotAccountTestKeys.PublicKey), 0]);

        AssertRejected(() => BotAccountTokenKeys.ImportPublicKey(value), BotAccountTokenKeys.PublicKeyName, value);
    }

    private static void AssertRejected(Func<RSA> import, string keyName, string? value, string? expectedFragment = null)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => import().Dispose());

        Assert.Contains(keyName, exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        if (expectedFragment is not null)
        {
            Assert.Contains(expectedFragment, exception.Message, StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            // No part of the configured value, not even a stretch of it, appears in the message.
            var trimmed = value.Trim();
            Assert.DoesNotContain(trimmed[..Math.Min(16, trimmed.Length)], exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(trimmed[^Math.Min(16, trimmed.Length)..], exception.ToString(), StringComparison.Ordinal);
        }
    }
}
