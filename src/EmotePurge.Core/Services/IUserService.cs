using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

// Decrypted view of the per-user Twitch token columns. AccessToken/ExpiresAtUtc may be null
// (never refreshed yet, or the stored ciphertext failed to decrypt) while RefreshToken is not —
// a row without a decryptable refresh token is reported as null instead.
public record TwitchStoredTokens(string RefreshToken, string? AccessToken, DateTime? AccessTokenExpiresAtUtc, string? Scopes);

// Result of CheckSessionAsync for a known user row. IsValid is false when the cookie's issue time
// predates the user's last revocation (see User.SessionsValidFromUtc) — CheckSessionAsync makes
// that call itself now, rather than handing a raw cutoff back for the caller to compare, so that a
// revoked-but-still-valid-looking session cannot slip past the check that decides whether
// LastSeenAtUtc gets stamped (see that method's contract comment). A missing user row is still told
// apart from a present-but-revoked one by the outer nullable — see IUserService.CheckSessionAsync.
public record SessionCheckResult(bool IsValid);

public interface IUserService
{
    Task<User> UpsertLoginAsync(string twitchUserId, string twitchUsername, string displayName, CancellationToken cancellationToken = default);

    // Runs on every authenticated request (OnValidatePrincipal), which passes the cookie's own
    // issue time (the `twitch:session_issued_at` claim, UTC) as issuedAtUtc. Returns null when the
    // user row is gone — the caller must reject the principal rather than treat that as "never
    // revoked" (a deleted account's session must not keep working). Otherwise returns whether the
    // session is still valid: false when issuedAtUtc predates User.SessionsValidFromUtc, i.e. the
    // cookie was issued before the account's sessions were last revoked.
    //
    // Side effect: *only on a valid session*, stamps User.LastSeenAtUtc to now, and only when it is
    // null or older than 24 hours — most requests hit a fresh stamp and cause no write at all, so
    // this adds no extra write to the hot path beyond the one write a day every active user gets.
    // A client that keeps replaying a revoked cookie is rejected on every request and never moves
    // this stamp, which matters because it feeds the retention cutoffs (see User.LastSeenAtUtc) —
    // stamping a session the caller is about to reject would let that client keep postponing its
    // own account's retention clock indefinitely. The write is a single conditional UPDATE, not a
    // load-modify-save, so concurrent requests for the same user cannot double-write or lose the
    // update: whichever request's UPDATE commits first makes the stamp fresh, and every other
    // concurrent request's WHERE clause then no longer matches and affects zero rows.
    Task<SessionCheckResult?> CheckSessionAsync(string twitchUserId, DateTime issuedAtUtc, CancellationToken cancellationToken = default);

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
