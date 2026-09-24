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
            return body is { Success: true }
                ? TurnstileVerificationResult.Success
                : TurnstileVerificationResult.Failed;
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
