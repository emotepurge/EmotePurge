namespace EmotePurge.Core.Services;

/// <summary>What Cloudflare's <c>siteverify</c> endpoint answered, or the fact that it could not be asked.</summary>
public enum TurnstileVerificationResult
{
    /// <summary><c>{ "success": true }</c> — a real, positive answer from Cloudflare.</summary>
    Success,

    /// <summary><c>{ "success": false }</c> — a real, negative answer (expired/invalid/spent token).</summary>
    Failed,

    /// <summary>
    /// Cloudflare could not be reached at all (timeout, DNS, non-2xx, an unparsable body). Distinct
    /// from <see cref="Failed"/> on purpose: the caller's token may well have been valid, so this
    /// must not be reported to the visitor the same way a genuinely rejected token is.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Verifies a Cloudflare Turnstile response token server-side against
/// <c>https://challenges.cloudflare.com/turnstile/v0/siteverify</c>. Implemented as a typed
/// <c>HttpClient</c> in Infrastructure (<c>TurnstileVerifier</c>) with a short timeout — this
/// interface exists so <c>ContactSubmissionService</c> and its tests never depend on the concrete
/// HTTP shape.
/// </summary>
public interface ITurnstileVerifier
{
    /// <param name="token">The <c>turnstileToken</c> the client submitted.</param>
    /// <param name="remoteIp">
    /// The visitor's address as this app already resolves it behind the reverse proxy
    /// (<c>HttpContext.Connection.RemoteIpAddress</c>, correct only because of the
    /// <c>ForwardedHeadersMiddleware</c> trust configuration in <c>Program.cs</c>) — optional per
    /// Cloudflare's own contract, so a <see langword="null"/> IP still verifies the token, just
    /// without that extra signal.
    /// </param>
    Task<TurnstileVerificationResult> VerifyAsync(
        string token, string? remoteIp, CancellationToken cancellationToken);
}
