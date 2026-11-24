using System.Security.Cryptography;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// Fingerprint of a token key (plan #487 2.13, spec 18.2): SHA-256 over the key's SubjectPublicKeyInfo, lowercase hex.
/// Pure. A private key yields the same value as its public half, so either side of the pair can be compared.
/// </summary>
public static class BotAccountTokenKeyFingerprint
{
    public static string Of(RSA key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }
}
