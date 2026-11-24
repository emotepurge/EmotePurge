using System.Security.Cryptography;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// The Api's sealer (spec 7.1, E7): holds only the public token key, so the Api can seal but never open. Format in
/// <see cref="BotAccountTokenSeal"/>.
/// </summary>
public sealed class RsaBotAccountTokenSealer : IBotAccountTokenSealer, IDisposable
{
    // RSA instances are not documented as thread-safe; sealing is rare (one admin request), so a lock costs nothing.
    private readonly Lock _gate = new();
    private readonly RSA _publicKey;

    /// <summary>Takes ownership of <paramref name="publicKey"/>.</summary>
    public RsaBotAccountTokenSealer(RSA publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        _publicKey = publicKey;
        KeyFingerprint = BotAccountTokenKeyFingerprint.Of(publicKey);
    }

    public string? KeyFingerprint { get; }

    public string Seal(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length == 0 || token.Length > BotAccountJwt.MaxLength)
        {
            throw new ArgumentException($"The token must be between 1 and {BotAccountJwt.MaxLength} characters long.", nameof(token));
        }

        var plaintext = BotAccountTokenSeal.StrictUtf8.GetBytes(token);
        var aesKey = RandomNumberGenerator.GetBytes(BotAccountTokenSeal.AesKeySize);
        try
        {
            byte[] wrappedKey;
            lock (_gate)
            {
                wrappedKey = _publicKey.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);
            }

            var nonce = RandomNumberGenerator.GetBytes(BotAccountTokenSeal.NonceSize);
            var packed = new byte[wrappedKey.Length + nonce.Length + plaintext.Length + BotAccountTokenSeal.TagSize];
            var ciphertext = packed.AsSpan(wrappedKey.Length + nonce.Length, plaintext.Length);
            var tag = packed.AsSpan(packed.Length - BotAccountTokenSeal.TagSize);

            using (var aes = new AesGcm(aesKey, BotAccountTokenSeal.TagSize))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, BotAccountTokenSeal.AssociatedData);
            }

            wrappedKey.CopyTo(packed, 0);
            nonce.CopyTo(packed, wrappedKey.Length);
            return BotAccountTokenSeal.Prefix + Convert.ToBase64String(packed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Dispose() => _publicKey.Dispose();
}
