using System.Security.Cryptography;
using EmotePurge.Infrastructure.BotAccount;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class BotAccountTokenKeyFingerprintTests
{
    [Fact]
    public void SameKey_SameValue()
    {
        using var first = BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey);
        using var second = BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey);

        Assert.Equal(BotAccountTokenKeyFingerprint.Of(first), BotAccountTokenKeyFingerprint.Of(second));
    }

    [Fact]
    public void OtherKey_OtherValue()
    {
        using var first = BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey);
        using var other = BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.OtherPublicKey);

        Assert.NotEqual(BotAccountTokenKeyFingerprint.Of(first), BotAccountTokenKeyFingerprint.Of(other));
    }

    [Fact]
    public void PrivateKey_YieldsTheValueOfItsPublicKey()
    {
        using var publicKey = BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey);
        using var privateKey = BotAccountTokenKeys.ImportPrivateKey(BotAccountTestKeys.PrivateKey);

        Assert.Equal(BotAccountTokenKeyFingerprint.Of(publicKey), BotAccountTokenKeyFingerprint.Of(privateKey));
    }

    [Fact]
    public void IsLowercaseHexOfTheSha256OfTheSubjectPublicKeyInfo()
    {
        using var publicKey = BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(BotAccountTestKeys.PublicKey)));

        var fingerprint = BotAccountTokenKeyFingerprint.Of(publicKey);

        Assert.Equal(expected, fingerprint);
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }
}
