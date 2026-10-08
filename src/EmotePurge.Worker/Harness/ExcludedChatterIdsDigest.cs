using System.Security.Cryptography;
using System.Text;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// A fingerprint of <see cref="IExcludedChatterFilter.ExcludedChatterIds"/>, part of
/// <see cref="HarnessRunIdentity"/> so that a changed exclusion policy (GDPR Art. 21, issue
/// #252/#260) starts a fresh run instead of resuming or recomputing a file whose day lines were
/// counted under a different one — the same objection gate the counting callback in
/// <see cref="HarnessRunner"/> already applies, made visible to the identity that decides resume
/// vs. fresh file.
/// <para>
/// A digest, never the raw ids: unlike <c>BotAccountIds</c> (which <see cref="HarnessRunIdentity"/>
/// already carries verbatim and which is not personal data), an excluded-chatter id names a person
/// who has objected to being processed at all — writing the list itself into a <c>.jsonl</c> header
/// that outlives the run would be the exact kind of processing the objection asked to stop. This is
/// not a secrecy measure: the same ids already sit in plaintext in the operator's <c>.env</c> on the
/// same host that would read this file, so a digest leaks nothing beyond what that file already
/// does — it exists only so the identity comparison (byte-for-byte on the canonical JSON, see
/// <see cref="HarnessReportFile.SerializeIdentity"/>) is sensitive to the policy without a second,
/// independent place naming who is on it.
/// </para>
/// <para>
/// Normalizes (trims, drops blanks, de-duplicates, sorts ordinally) before hashing, so the digest is
/// a function of the *set* the filter enforces — insensitive to configuration order or incidental
/// whitespace, the same insensitivity <c>ExcludedChatterFilter</c>'s own parsing already gives the
/// filter itself. An empty set hashes to a fixed, stable value (the digest of the empty string) —
/// not a special-cased sentinel — so every no-exclusion run under a given <see cref="HarnessRunner.AlgorithmVersion"/>
/// shares one identity for this field, exactly as before this field existed.
/// </para>
/// </summary>
public static class ExcludedChatterIdsDigest
{
    public static string Compute(IEnumerable<string> excludedChatterIds)
    {
        ArgumentNullException.ThrowIfNull(excludedChatterIds);

        var normalized = excludedChatterIds
            .Select(id => id.Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);

        var canonical = string.Join('\n', normalized);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
