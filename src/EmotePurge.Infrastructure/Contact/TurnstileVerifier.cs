using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmotePurge.Infrastructure.Contact;

/// <summary>
/// Calls Cloudflare's <c>POST /turnstile/v0/siteverify</c> (typed <c>HttpClient</c>, registered with
/// a 5 s timeout and <c>BaseAddress = https://challenges.cloudflare.com/turnstile/v0/</c> in
/// <c>ServiceCollectionExtensions</c>). Deliberately no <c>ProviderRequestTelemetryHandler</c> here,
/// unlike the 7TV/Twitch typed clients: that dashboard tracks providers this app calls on a per-user
/// navigation cadence and budgets accordingly, while a Turnstile verification happens at most a
/// handful of times an hour, already bounded tighter by <see cref="ContactSendBudget"/> and the
/// endpoint's own rate-limit policy — adding a fourth provider to that dashboard for traffic this
/// small would not earn its keep.
/// </summary>
public sealed class TurnstileVerifier(
    HttpClient httpClient,
    IOptions<ContactOptions> options,
    ILogger<TurnstileVerifier> logger) : ITurnstileVerifier
{
    private const string SecretFormKey = "secret";
    private const string ResponseFormKey = "response";
    private const string RemoteIpFormKey = "remoteip";

    // Cloudflare's documented siteverify error-codes (developers.cloudflare.com/turnstile/get-started/
    // server-side-validation/, "Error codes" table, checked 2026-09-24) split into two kinds. These
    // three are about the *token the visitor's browser produced* — an ordinary, expected outcome of a
    // real visitor, nothing an operator did wrong:
    //   - invalid-input-response: "The response parameter is invalid or has expired."
    //   - timeout-or-duplicate: "The response parameter has already been validated before."
    //   - missing-input-response: "The response parameter was not passed." — reachable when the
    //     widget never produced a token at all (blocked script, ad blocker, a visitor who submits
    //     before the challenge finishes), not something the operator's own request shape controls.
    // Every other documented code — missing-input-secret, invalid-input-secret (both about the
    // operator's own secret key), bad-request (a malformed request this backend sent), and
    // internal-error (Cloudflare's own outage) — is the operator's or Cloudflare's problem, not the
    // visitor's, and must not be reported to them as "captcha failed" (Codex P2, docs/DECISIONS.md
    // 2026-09-24 revision).
    private static readonly HashSet<string> VisitorTokenErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "invalid-input-response",
        "timeout-or-duplicate",
        "missing-input-response",
    };

    public async Task<TurnstileVerificationResult> VerifyAsync(
        string token, string? remoteIp, CancellationToken cancellationToken)
    {
        var secretKey = options.Value.Turnstile.SecretKey;
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            // Reachable only if the caller skipped ContactOptions.IsAvailable — defensive, not the
            // primary guard (ContactSubmissionService checks availability first).
            return TurnstileVerificationResult.Unavailable;
        }

        var form = new Dictionary<string, string>
        {
            [SecretFormKey] = secretKey,
            [ResponseFormKey] = token,
        };
        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            form[RemoteIpFormKey] = remoteIp;
        }

        try
        {
            using var response = await httpClient.PostAsync(
                "siteverify", new FormUrlEncodedContent(form), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Turnstile siteverify answered with a non-success status: {StatusCode}",
                    (int)response.StatusCode);
                return TurnstileVerificationResult.Unavailable;
            }

            var body = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(cancellationToken);
            if (body is { Success: true })
            {
                return TurnstileVerificationResult.Success;
            }

            var errorCodes = body?.ErrorCodes ?? [];
            if (errorCodes.Count == 0 || errorCodes.All(code => VisitorTokenErrorCodes.Contains(code)))
            {
                // Either no code at all (rejected token, no further detail) or every reported code is
                // about the token itself — an ordinary "this visitor's answer did not check out".
                return TurnstileVerificationResult.Failed;
            }

            // At least one reported code is about the operator's own secret key, a malformed request
            // this backend sent, or Cloudflare's own internal error — never the visitor's fault, so
            // this must not read to them as "wrong answer". Logged at Warning, without the token or the
            // secret key itself, so a misconfigured secret is caught from the logs instead of looking
            // like ordinary visitor churn.
            logger.LogWarning(
                "Turnstile siteverify reported a configuration-side problem: {ErrorCodes}",
                string.Join(", ", errorCodes));
            return TurnstileVerificationResult.Unavailable;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException
            or System.Text.Json.JsonException)
        {
            // Never the token or the visitor's IP in this log line — only that the call itself failed.
            logger.LogWarning(ex, "Turnstile siteverify could not be completed.");
            return TurnstileVerificationResult.Unavailable;
        }
    }

    private sealed class SiteVerifyResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("error-codes")]
        public List<string>? ErrorCodes { get; set; }
    }
}
