using System.Text;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// The seal format of the bot account token (spec 7.1, plan #487 2.2): <c>v1.</c> followed by base64 of
/// wrapped key ‖ nonce ‖ ciphertext ‖ tag. A fresh AES-256 key encrypts the UTF-8 token with AES-GCM (12-byte nonce,
/// 16-byte tag, fixed associated data), and RSA-3072 with OAEP-SHA256 wraps that AES key. The associated data binds the
/// seal to this one purpose, so a seal cannot pass for another secret of the same key pair.
/// </summary>
internal static class BotAccountTokenSeal
{
    public const string Prefix = "v1.";
    public const int WrappedKeySize = BotAccountTokenKeys.KeySizeBits / 8;
    public const int AesKeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>Smallest payload: a token of at least one byte.</summary>
    public const int MinPayloadSize = WrappedKeySize + NonceSize + 1 + TagSize;

    /// <summary>Largest payload: <see cref="Core.Services.BotAccountJwt.MaxLength"/> characters, up to four UTF-8 bytes each.</summary>
    public const int MaxPayloadSize = WrappedKeySize + NonceSize + (Core.Services.BotAccountJwt.MaxLength * 4) + TagSize;

    public static ReadOnlySpan<byte> AssociatedData => "emotepurge:bot-account-token:v1"u8;

    public static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
