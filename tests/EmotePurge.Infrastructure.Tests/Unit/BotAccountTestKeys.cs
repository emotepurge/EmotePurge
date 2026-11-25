using System.Security.Cryptography;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Key pairs generated once per test run, never stored anywhere: two 3072-bit pairs (the configured one and a stranger)
/// and one 2048-bit pair for the size check. Exposed in the configuration form (one line of base64 DER).
/// </summary>
internal static class BotAccountTestKeys
{
    private static readonly Lazy<RSA> PrimaryKey = new(() => RSA.Create(3072));
    private static readonly Lazy<RSA> OtherKey = new(() => RSA.Create(3072));
    private static readonly Lazy<RSA> SmallKey = new(() => RSA.Create(2048));

    public static string PublicKey => Public(PrimaryKey.Value);
    public static string PrivateKey => Private(PrimaryKey.Value);
    public static string OtherPublicKey => Public(OtherKey.Value);
    public static string OtherPrivateKey => Private(OtherKey.Value);
    public static string SmallPublicKey => Public(SmallKey.Value);
    public static string SmallPrivateKey => Private(SmallKey.Value);

    private static string Public(RSA key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static string Private(RSA key) => Convert.ToBase64String(key.ExportPkcs8PrivateKey());
}
