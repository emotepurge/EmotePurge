using System.Security.Cryptography;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// The worker's opener (spec 7.1, E7): holds the private token key. Every deviation from an intact seal for this key
/// yields <c>null</c>, and no exception leaves <see cref="TryOpen"/> (AK72). Format in <see cref="BotAccountTokenSeal"/>.
/// </summary>
public sealed class RsaBotAccountTokenOpener : IBotAccountTokenOpener, IDisposable
{
    private readonly Lock _gate = new();
    private readonly RSA _privateKey;

    /// <summary>Takes ownership of <paramref name="privateKey"/>.</summary>
    public RsaBotAccountTokenOpener(RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        _privateKey = privateKey;
    }

    public string? TryOpen(string? sealedToken)
    {
        if (sealedToken is null || !sealedToken.StartsWith(BotAccountTokenSeal.Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var packed = DecodePayload(sealedToken.AsSpan(BotAccountTokenSeal.Prefix.Length));
        if (packed is null)
        {
            return null;
        }

        byte[]? aesKey = null;
        byte[]? plaintext = null;
        try
        {
            var wrappedKey = packed.AsSpan(0, BotAccountTokenSeal.WrappedKeySize);
            var nonce = packed.AsSpan(BotAccountTokenSeal.WrappedKeySize, BotAccountTokenSeal.NonceSize);
            var ciphertextStart = BotAccountTokenSeal.WrappedKeySize + BotAccountTokenSeal.NonceSize;
            var ciphertext = packed.AsSpan(ciphertextStart, packed.Length - ciphertextStart - BotAccountTokenSeal.TagSize);
            var tag = packed.AsSpan(packed.Length - BotAccountTokenSeal.TagSize);

            lock (_gate)
            {
                aesKey = _privateKey.Decrypt(wrappedKey.ToArray(), RSAEncryptionPadding.OaepSHA256);
            }

            if (aesKey.Length != BotAccountTokenSeal.AesKeySize)
            {
                return null;
            }

            plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(aesKey, BotAccountTokenSeal.TagSize))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, BotAccountTokenSeal.AssociatedData);
            }

            return BotAccountTokenSeal.StrictUtf8.GetString(plaintext);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            // AuthenticationTagMismatchException is a CryptographicException; DecoderFallbackException an ArgumentException.
            return null;
        }
        finally
        {
            if (aesKey is not null)
            {
                CryptographicOperations.ZeroMemory(aesKey);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public void Dispose() => _privateKey.Dispose();

    private static byte[]? DecodePayload(ReadOnlySpan<char> base64)
    {
        // Upper bound before decoding, so an oversized value is never materialised.
        if (base64.Length == 0 || base64.Length > ((BotAccountTokenSeal.MaxPayloadSize + 2) / 3 * 4))
        {
            return null;
        }

        var buffer = new byte[base64.Length * 3 / 4];
        if (!Convert.TryFromBase64Chars(base64, buffer, out var written)
            || written < BotAccountTokenSeal.MinPayloadSize
            || written > BotAccountTokenSeal.MaxPayloadSize)
        {
            return null;
        }

        return written == buffer.Length ? buffer : buffer[..written];
    }
}
