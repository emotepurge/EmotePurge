using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

// Decrypted view of the per-user Twitch token columns. AccessToken/ExpiresAtUtc may be null
// (never refreshed yet, or the stored ciphertext failed to decrypt) while RefreshToken is not —
// a row without a decryptable refresh token is reported as null instead.
public record TwitchStoredTokens(string RefreshToken, string? AccessToken, DateTime? AccessTokenExpiresAtUtc, string? Scopes);

// Result of CheckSessionAsync. RevokedBefore is the server-side revocation cutoff (null means
// nothing was ever revoked) — the same information GetSessionsValidFromUtcAsync used to return
// alone. The wrapping record exists so a missing user row can be told apart from a present one
// that was never revoked: both used to read as plain "null", which is exactly wrong once accounts
// can be deleted (retention job, admin deletion) — a deleted account's cookie must stop working on
// its very next request instead of reading as "nothing to check".
public record SessionCheckResult(DateTime? RevokedBefore);

public interface IUserService
{
    Task<User> UpsertLoginAsync(string twitchUserId, string twitchUsername, string displayName, CancellationToken cancellationToken = default);

    // Runs on every authenticated request (OnValidatePrincipal). Returns null when the user row is
    // gone — the caller must reject the principal rather than treat that as "never revoked" (a
    // deleted account's session must not keep working). Otherwise returns the revocation cutoff,
    // same as the method this replaced.
    //
    // Side effect: stamps User.LastSeenAtUtc to now, but only when it is null or older than 24
    // hours — most requests hit a fresh stamp and cause no write at all, so this adds no extra
    // write to the hot path beyond the one write a day every active user gets. The write is a
    // single conditional UPDATE, not a load-modify-save, so concurrent requests for the same user
    // cannot double-write or lose the update: whichever request's UPDATE commits first makes the
    // stamp fresh, and every other concurrent request's WHERE clause then no longer matches and
    // affects zero rows. LastSeenAtUtc is what lets retention measure "still around" for a user
    // whose 14-day sliding cookie means they may never log in again (see User.LastSeenAtUtc).
    Task<SessionCheckResult?> CheckSessionAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // Invalidates every session issued before now for this user; false when the user is unknown.
    // `actor` decides whether the revocation is audited: an admin forcing another user out passes
    // themselves and a `user.revokeSessions` entry is written in the same transaction; self-logout
    // passes null and stays unaudited (user decision: no login/logout events in the audit log).
    // Deliberately not defaulted — every caller must make that choice visibly.
    Task<bool> RevokeSessionsAsync(string twitchUserId, AuditActor? actor, CancellationToken cancellationToken = default);

    // Persists a fresh token pair (login callback or successful refresh). Tokens are encrypted
    // via ITokenCipher before they touch the database; callers always pass plaintext.
    Task StoreTwitchTokensAsync(string twitchUserId, string accessToken, DateTime accessTokenExpiresAtUtc, string refreshToken, string? scopes, CancellationToken cancellationToken = default);

    // Null when the user is unknown, has no stored refresh token, or the stored value cannot be
    // decrypted (key change/tamper) — all three mean "refreshing is impossible, re-login required".
    Task<TwitchStoredTokens?> GetTwitchTokensAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // Drops all stored Twitch tokens (logout, or Twitch reported the refresh token as invalid).
    Task ClearTwitchTokensAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // Admin action: drops every cached role answer (mod/sub/7TV editor) for this user via
    // IModRoleCache, so the next authorization check resolves live. Returns the number of removed
    // cache entries, or null when the user is unknown. Audited as user.invalidateRoleCache; the
    // actor is required — unlike session revocation there is no self-service variant of this.
    Task<int?> InvalidateRoleCacheAsync(string twitchUserId, AuditActor actor, CancellationToken cancellationToken = default);
}
