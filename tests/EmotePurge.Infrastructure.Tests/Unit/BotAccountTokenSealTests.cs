using System.Security.Cryptography;
using System.Text;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.BotAccount;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// AK72: round trip yields the original; a changed byte, a wrong private key and an unknown prefix yield "no token",
// never an exception.
public class BotAccountTokenSealTests
{
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJleHAiOjE4OTM0NTYwMDB9.c2lnbmF0dXJl";

    // Offsets into the decoded payload: wrapped key (384) | nonce (12) | ciphertext | tag (16).
    private const int WrappedKeySize = 384;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    [Fact]
    public void RoundTrip_YieldsTheOriginal()
    {
        using var sealer = Sealer();
        using var opener = Opener();

        var sealedToken = sealer.Seal(Token);

        Assert.StartsWith("v1.", sealedToken, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, sealedToken, StringComparison.Ordinal);
        Assert.Equal(Token, opener.TryOpen(sealedToken));
    }

    [Fact]
    public void RoundTrip_KeepsNonAsciiAndMaximumLength()
    {
        using var sealer = Sealer();
        using var opener = Opener();
        var longest = new string('a', BotAccountJwt.MaxLength - 2) + "äß";

        Assert.Equal("äß€😀", opener.TryOpen(sealer.Seal("äß€😀")));
        Assert.Equal(longest, opener.TryOpen(sealer.Seal(longest)));
    }

    [Fact]
    public void Seal_IsFreshEveryTime()
    {
        using var sealer = Sealer();

        Assert.NotEqual(sealer.Seal(Token), sealer.Seal(Token));
    }

    [Fact]
    public void Seal_HasTheDocumentedLayout()
    {
        using var sealer = Sealer();

        var payload = Payload(sealer.Seal(Token));

        Assert.Equal(WrappedKeySize + NonceSize + Encoding.UTF8.GetByteCount(Token) + TagSize, payload.Length);
    }

    [Fact]
    public void Seal_RefusesAnEmptyToken()
    {
        using var sealer = Sealer();

        Assert.Throws<ArgumentException>(() => sealer.Seal(string.Empty));
    }

    [Fact]
    public void Seal_RefusesAnOversizedToken_WithoutEchoingIt()
    {
        using var sealer = Sealer();
        var oversized = new string('x', 64 * 1024);

        var exception = Assert.Throws<ArgumentException>(() => sealer.Seal(oversized));

        Assert.DoesNotContain("xxxx", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void KeyFingerprint_IsThatOfThePublicKey()
    {
        using var sealer = Sealer();
        using var publicKey = BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey);

        Assert.Equal(BotAccountTokenKeyFingerprint.Of(publicKey), sealer.KeyFingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1.")]
    [InlineData("v1.!!!not-base64!!!")]
    [InlineData("v1.AAAA")]
    [InlineData("plain text")]
    public void TryOpen_RejectsWhatIsNoSeal(string? value)
    {
        using var opener = Opener();

        Assert.Null(opener.TryOpen(value));
    }

    [Theory]
    [InlineData("v2.")]
    [InlineData("V1.")]
    [InlineData("")]
    [InlineData("v1")]
    public void TryOpen_RejectsAnUnknownPrefix(string prefix)
    {
        using var sealer = Sealer();
        using var opener = Opener();
        var body = sealer.Seal(Token)["v1.".Length..];

        Assert.Null(opener.TryOpen(prefix + body));
    }

    [Fact]
    public void TryOpen_RejectsATruncatedSeal()
    {
        using var sealer = Sealer();
        using var opener = Opener();
        var payload = Payload(sealer.Seal(Token));

        Assert.Null(opener.TryOpen(Seal(payload[..^1])));
        Assert.Null(opener.TryOpen(Seal(payload[..(WrappedKeySize + NonceSize + TagSize)])));
        Assert.Null(opener.TryOpen(Seal(payload[..100])));
    }

    [Fact]
    public void TryOpen_RejectsAnAppendedByte()
    {
        using var sealer = Sealer();
        using var opener = Opener();
        var payload = Payload(sealer.Seal(Token));

        Assert.Null(opener.TryOpen(Seal([.. payload, 0])));
    }

    [Theory]
    [InlineData(0)] // wrapped key, first byte
    [InlineData(WrappedKeySize - 1)] // wrapped key, last byte
    [InlineData(WrappedKeySize)] // nonce, first byte
    [InlineData(WrappedKeySize + NonceSize - 1)] // nonce, last byte
    [InlineData(WrappedKeySize + NonceSize)] // ciphertext, first byte
    [InlineData(-1)] // tag, last byte
    [InlineData(-TagSize)] // tag, first byte
    public void TryOpen_RejectsAFlippedByte(int offset)
    {
        using var sealer = Sealer();
        using var opener = Opener();
        var payload = Payload(sealer.Seal(Token));
        var index = offset < 0 ? payload.Length + offset : offset;

        payload[index] ^= 0x01;

        Assert.Null(opener.TryOpen(Seal(payload)));
    }

    [Fact]
    public void TryOpen_RejectsEveryFlippedByte()
    {
        using var sealer = Sealer();
        using var opener = Opener();
        var original = Payload(sealer.Seal("e30.e30.c2ln"));

        for (var index = 0; index < original.Length; index++)
        {
            var payload = (byte[])original.Clone();
            payload[index] ^= 0x80;
            Assert.Null(opener.TryOpen(Seal(payload)));
        }
    }

    [Fact]
    public void TryOpen_RejectsASealForAnotherKey()
    {
        using var sealer = Sealer();
        using var stranger = new RsaBotAccountTokenOpener(BotAccountTokenKeys.ImportPrivateKey(BotAccountTestKeys.OtherPrivateKey));

        Assert.Null(stranger.TryOpen(sealer.Seal(Token)));
    }

    [Fact]
    public void TryOpen_RejectsASealWithoutTheAssociatedData()
    {
        // Same key pair and layout, but encrypted without the fixed associated data: the seal is bound to its purpose.
        using var publicKey = BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey);
        using var opener = Opener();
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintext = Encoding.UTF8.GetBytes(Token);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(aesKey, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, "emotepurge:other-secret:v1"u8);
        }

        byte[] payload = [.. publicKey.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256), .. nonce, .. ciphertext, .. tag];

        Assert.Null(opener.TryOpen(Seal(payload)));
    }

    [Fact]
    public void TryOpen_RejectsAnOversizedValue()
    {
        using var opener = Opener();

        Assert.Null(opener.TryOpen("v1." + new string('A', 64 * 1024)));
    }

    private static RsaBotAccountTokenSealer Sealer() =>
        new(BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey));

    private static RsaBotAccountTokenOpener Opener() =>
        new(BotAccountTokenKeys.ImportPrivateKey(BotAccountTestKeys.PrivateKey));

    private static byte[] Payload(string sealedToken) => Convert.FromBase64String(sealedToken["v1.".Length..]);

    private static string Seal(byte[] payload) => "v1." + Convert.ToBase64String(payload);
}
