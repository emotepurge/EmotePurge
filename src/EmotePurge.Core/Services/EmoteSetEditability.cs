namespace EmotePurge.Core.Services;

/// <summary>
/// The one rule behind "may this account write to this 7TV emote set" (spec 2026-09-24 restore-
/// per-set addendum, 5.8/E19/F16): a set is editable exactly when it names an owner, and that
/// owner is the 7TV account id of an account whose own set list was read successfully.
/// </summary>
/// <remarks>
/// Two callers share this rule so it cannot drift apart between them: <c>GET
/// /api/seventv/me/emote-set-targets</c> calls it once per listed set, comparing against every
/// account of that same response whose list came back <c>Ok</c> (5.8); the set-centric import's
/// ownership check (Infrastructure, spec 2026-09-20 section 32) calls it once per owner id a set
/// was listed under, comparing against the same readable-account ids it has gathered from the
/// actor's and every <c>editor_of</c> account's list. AK 30 pins that both answer alike.
/// </remarks>
public static class EmoteSetEditability
{
    /// <param name="ownerSevenTvUserId">
    /// <c>owner.id</c> of the set, or <c>null</c>/empty when 7TV reported no owner for it — always
    /// <c>false</c> in that case (F16). This function never falls back to a direct 7TV lookup to
    /// answer that question, even where a live lookup might allow it: the caller is deliberately
    /// stricter here than a fallback lookup could be, never looser.
    /// </param>
    /// <param name="readableAccountSevenTvUserIds">
    /// The 7TV account ids of every account whose set list was read successfully — never an
    /// account whose list failed, nor one with no 7TV account at all (it has no id to contribute).
    /// </param>
    public static bool IsEditable(string? ownerSevenTvUserId, IReadOnlyCollection<string> readableAccountSevenTvUserIds)
    {
        ArgumentNullException.ThrowIfNull(readableAccountSevenTvUserIds);

        return !string.IsNullOrEmpty(ownerSevenTvUserId)
            && readableAccountSevenTvUserIds.Contains(ownerSevenTvUserId, StringComparer.Ordinal);
    }
}
