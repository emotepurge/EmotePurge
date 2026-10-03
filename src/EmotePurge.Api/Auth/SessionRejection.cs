namespace EmotePurge.Api.Auth;

/// <summary>Why the cookie scheme's <c>OnValidatePrincipal</c> rejected a principal.</summary>
public enum SessionRejectionReason
{
    /// <summary>The user row no longer exists: the account was deleted.</summary>
    UserGone,

    /// <summary>The session was revoked (<c>User.SessionsValidFromUtc</c>) or the cookie predates session tracking.</summary>
    Other,
}

/// <summary>
/// Decides the status code of an unauthenticated challenge. Always 401, with one exception: the
/// self-service deletion answers 410 Gone when its own session was rejected because the user row is
/// gone and the request names that same account — the only 401 cause from which a client may conclude
/// that the confirmed account no longer exists.
/// </summary>
public static class SessionRejection
{
    /// <summary>Key under which <c>OnValidatePrincipal</c> leaves the reason in <c>HttpContext.Items</c>.</summary>
    public const string ItemKey = "emotepurge:session-rejection";

    /// <summary>Key under which <c>OnValidatePrincipal</c> leaves the id of the account whose row is gone.</summary>
    public const string RejectedUserIdItemKey = "emotepurge:session-rejected-user-id";

    private const string SelfDeletionPath = "/api/auth/me";

    /// <summary>
    /// 410 additionally requires the deletion to name, as <paramref name="expectedTwitchUserId"/>, the
    /// very account whose row is gone (<paramref name="rejectedUserId"/>): the cookie is shared across
    /// tabs, so a different or missing id means the client confirmed some other account, and "your
    /// account is gone" would be a claim about the wrong one.
    /// </summary>
    public static int ChallengeStatusCode(
        SessionRejectionReason? reason, string method, string? path, string? rejectedUserId, string? expectedTwitchUserId)
    {
        var isSelfDeletion = reason == SessionRejectionReason.UserGone
            && rejectedUserId is not null
            && string.Equals(rejectedUserId, expectedTwitchUserId, StringComparison.Ordinal)
            && HttpMethods.IsDelete(method)
            && string.Equals(path?.TrimEnd('/'), SelfDeletionPath, StringComparison.OrdinalIgnoreCase);
        return isSelfDeletion ? StatusCodes.Status410Gone : StatusCodes.Status401Unauthorized;
    }
}
