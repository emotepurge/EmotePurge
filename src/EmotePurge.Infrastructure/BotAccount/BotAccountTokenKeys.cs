using System.Security.Cryptography;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// Reads the two halves of the token key pair from their configuration values (plan #487 2.2): one line of base64 DER
/// each, the public key as SubjectPublicKeyInfo, the private key as PKCS#8, RSA with exactly 3072 bits. Every failure
/// is an <see cref="InvalidOperationException"/> that names the key and the reason, never the value, and carries no
/// inner exception (whose text is out of our hands).
/// </summary>
public static class BotAccountTokenKeys
{
    /// <summary>Configuration key of the public half, read by the Api only.</summary>
    public const string PublicKeyName = "BotAccount:TokenPublicKey";

    /// <summary>Configuration key of the private half, read by the worker only.</summary>
    public const string PrivateKeyName = "BotAccount:TokenPrivateKey";

    /// <summary>The only accepted key size, in bits (spec 7.1).</summary>
    public const int KeySizeBits = 3072;

    /// <summary>Imports the public key; the caller owns the returned instance.</summary>
    public static RSA ImportPublicKey(string? value) =>
        Import(value, PublicKeyName, "a one-line base64 DER SubjectPublicKeyInfo (openssl rsa -pubout -outform DER)",
            static (rsa, der) =>
            {
                rsa.ImportSubjectPublicKeyInfo(der, out var read);
                return read == der.Length ? read : -1;
            });

    /// <summary>Imports the private key; the caller owns the returned instance.</summary>
    public static RSA ImportPrivateKey(string? value) =>
        Import(value, PrivateKeyName, "a one-line base64 DER PKCS#8 private key (openssl pkcs8 -topk8 -nocrypt -outform DER)",
            static (rsa, der) =>
            {
                rsa.ImportPkcs8PrivateKey(der, out var read);
                return read == der.Length ? read : -1;
            });

    private static RSA Import(string? value, string keyName, string expectedForm, Func<RSA, byte[], int> import)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Configuration value '{keyName}' is missing but BotAccount:Enabled is true; expected {expectedForm}.");
        }

        byte[] der;
        try
        {
            der = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"Configuration value '{keyName}' is not valid base64; expected {expectedForm}.");
        }

        var rsa = RSA.Create();
        try
        {
            int read;
            try
            {
                read = import(rsa, der);
            }
            catch (CryptographicException)
            {
                read = -1;
            }

            if (read < 0)
            {
                throw new InvalidOperationException($"Configuration value '{keyName}' is not a usable RSA key; expected {expectedForm}.");
            }

            if (rsa.KeySize != KeySizeBits)
            {
                throw new InvalidOperationException(
                    $"Configuration value '{keyName}' is an RSA key of {rsa.KeySize} bits; exactly {KeySizeBits} bits are required.");
            }

            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }
}
