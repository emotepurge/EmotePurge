namespace EmotePurge.Core.Services;

/// <summary>
/// Seals the bot account's 7TV token for storage (spec 7.1, E7). Registered in the Api only, and it holds only the
/// public half of the token key pair: the Api can seal a token, never read one back. The worker opens the seal with
/// <see cref="IBotAccountTokenOpener"/>.
/// </summary>
public interface IBotAccountTokenSealer
{
    /// <summary>
    /// SHA-256 fingerprint of the public token key in use (lowercase hex), or <c>null</c> while the bot account is
    /// disabled. Written next to the seal so a later key change is visible (plan #487 2.13).
    /// </summary>
    string? KeyFingerprint { get; }

    /// <summary>
    /// Returns the seal (<c>v1.</c> followed by base64). Throws <see cref="InvalidOperationException"/> while the bot
    /// account is disabled and <see cref="ArgumentException"/> for an empty or oversized token; no message ever
    /// contains the token.
    /// </summary>
    string Seal(string token);
}
