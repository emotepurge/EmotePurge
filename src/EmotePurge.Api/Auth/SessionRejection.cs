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
/// gone — the only 401 cause from which a client may conclude that the account no longer exists.
/// </summary>
public static class SessionRejection
{
    /// <summary>Key under which <c>OnValidatePrincipal</c> leaves the reason in <c>HttpContext.Items</c>.</summary>
    public const string ItemKey = "emotepurge:session-rejection";

    private const string SelfDeletionPath = "/api/auth/me";

    public static int ChallengeStatusCode(SessionRejectionReason? reason, string method, string? path)
    {
        var isSelfDeletion = reason == SessionRejectionReason.UserGone
            && HttpMethods.IsDelete(method)
            && string.Equals(path?.TrimEnd('/'), SelfDeletionPath, StringComparison.OrdinalIgnoreCase);
        return isSelfDeletion ? StatusCodes.Status410Gone : StatusCodes.Status401Unauthorized;
    }
}
