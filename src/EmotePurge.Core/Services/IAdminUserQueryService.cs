namespace EmotePurge.Core.Services;

/// <summary>
/// One row of GET /api/admin/users. Token state is exposed as <b>derived status only</b> —
/// <paramref name="HasRefreshToken"/> is a boolean over the encrypted column, expiry and scopes are
/// plain metadata columns. The token ciphertexts themselves never leave the query, let alone the
/// service: there is no admin scenario that needs them, so no DTO shape that could leak them.
/// <paramref name="SessionsValidFromUtc"/> is the revocation cutoff (null = never revoked), shown so
/// an admin can see that a forced logout actually took. <paramref name="LastSeenAtUtc"/> is the
/// retention timestamp (see User.LastSeenAtUtc); the list keeps sorting by LastLogin, this is
/// exposed for completeness and is not yet rendered anywhere.
/// </summary>
public record AdminUserDto(
    string TwitchUserId,
    string TwitchUsername,
    string DisplayName,
    DateTime LastLogin,
    DateTime? SessionsValidFromUtc,
    DateTime? LastSeenAtUtc,
    bool HasRefreshToken,
    DateTime? TwitchAccessTokenExpiresAtUtc,
    string? TwitchTokenScopes);

/// <summary>
/// Read side of the admin user overview. Paged unlike the channel list: the channel count is capped
/// by Twitch's JOIN limit, but every viewer who ever logged in has a user row.
/// </summary>
public interface IAdminUserQueryService
{
    /// <summary>
    /// Most recently seen first. <paramref name="page"/> is 1-based; both arguments are expected to
    /// be pre-validated by the endpoint, same as the other paged query services.
    /// </summary>
    Task<PagedResult<AdminUserDto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken = default);
}
