using System.Net;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Contact;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="TurnstileVerifier"/> against a stubbed <see cref="HttpMessageHandler"/> — no real
/// network call to Cloudflare. Pins the three outcomes <c>ContactSubmissionService</c> branches on:
/// a genuine pass, a genuine fail, and "could not be asked at all" (timeout/non-2xx/unparsable body).
/// </summary>
public class TurnstileVerifierTests
{
    private const string SecretKey = "test-secret";

    [Fact]
    public async Task SuccessResponse_ReturnsSuccess()
    {
        var handler = new StubHandler(_ => JsonResponse(HttpStatusCode.OK, """{"success":true}"""));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("token", "203.0.113.1", CancellationToken.None);

        Assert.Equal(TurnstileVerificationResult.Success, result);
    }

    [Fact]
    public async Task FailureResponse_ReturnsFailed()
    {
        var handler = new StubHandler(_ =>
            JsonResponse(HttpStatusCode.OK, """{"success":false,"error-codes":["invalid-input-response"]}"""));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("bad-token", null, CancellationToken.None);

        Assert.Equal(TurnstileVerificationResult.Failed, result);
    }

    /// <summary>
    /// Added 2026-09-24 (Codex P2): every documented visitor-token-side error code
    /// (developers.cloudflare.com/turnstile/get-started/server-side-validation/, "Error codes" table)
    /// must still read as an ordinary rejected token, not as the form being unavailable.
    /// </summary>
    [Theory]
    [InlineData("invalid-input-response")]
    [InlineData("timeout-or-duplicate")]
    [InlineData("missing-input-response")]
    public async Task VisitorTokenErrorCode_ReturnsFailed(string errorCode)
    {
        var handler = new StubHandler(_ =>
            JsonResponse(HttpStatusCode.OK, $$"""{"success":false,"error-codes":["{{errorCode}}"]}"""));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("bad-token", null, CancellationToken.None);

        Assert.Equal(TurnstileVerificationResult.Failed, result);
    }

    [Fact]
    public async Task FailureResponse_WithNoErrorCodes_ReturnsFailed()
    {
        var handler = new StubHandler(_ => JsonResponse(HttpStatusCode.OK, """{"success":false}"""));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("bad-token", null, CancellationToken.None);

        Assert.Equal(TurnstileVerificationResult.Failed, result);
    }

    /// <summary>
    /// Added 2026-09-24 (Codex P2): a configuration-side error code — the operator's own secret key,
    /// a malformed request, or Cloudflare's own internal error — must not read as "the visitor's
    /// token was wrong". Before this fix, every non-success answer fell through to
    /// <see cref="TurnstileVerificationResult.Failed"/> regardless of which error code Cloudflare
    /// actually reported.
    /// </summary>
    [Theory]
    [InlineData("missing-input-secret")]
    [InlineData("invalid-input-secret")]
    [InlineData("bad-request")]
    [InlineData("internal-error")]
    public async Task ConfigurationOrInternalErrorCode_ReturnsUnavailable(string errorCode)
    {
        var handler = new StubHandler(_ =>
            JsonResponse(HttpStatusCode.OK, $$"""{"success":false,"error-codes":["{{errorCode}}"]}"""));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("token", null, CancellationToken.None);

        Assert.Equal(TurnstileVerificationResult.Unavailable, result);
    }

    /// <summary>
    /// A mixed answer — one visitor-side code alongside one configuration-side code — must still read
    /// as "unavailable": the configuration problem is real regardless of what else Cloudflare reported,
    /// and telling the visitor "your token was wrong" would hide it.
    /// </summary>
    [Fact]
    public async Task MixedErrorCodes_WithAnyConfigurationSideCode_ReturnsUnavailable()
    {
        var handler = new StubHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{"success":false,"error-codes":["invalid-input-response","invalid-input-secret"]}"""));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("token", null, CancellationToken.None);

        Assert.Equal(TurnstileVerificationResult.Unavailable, result);
    }

    /// <summary>
    /// The configuration-side case is logged at Warning — an operator watching logs should notice a
    /// broken secret key quickly — but never with the secret key or the visitor's token in the line.
    /// </summary>
    [Fact]
    public async Task ConfigurationErrorCode_LogsAtWarning_WithoutTheSecretOrTheToken()
    {
        var handler = new StubHandler(_ =>
            JsonResponse(HttpStatusCode.OK, """{"success":false,"error-codes":["invalid-input-secret"]}"""));
        var logger = new RecordingLogger<TurnstileVerifier>();
        var verifier = CreateVerifier(handler, logger);

        await verifier.VerifyAsync("the-actual-token", null, CancellationToken.None);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("invalid-input-secret", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretKey, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("the-actual-token", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonSuccessStatusCode_ReturnsUnavailable()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("token", null, CancellationToken.None);

        Assert.Equal(TurnstileVerificationResult.Unavailable, result);
    }

    [Fact]
    public async Task RequestThrows_ReturnsUnavailable_NotAnException()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("network down"));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("token", null, CancellationToken.None);

        Assert.Equal(TurnstileVerificationResult.Unavailable, result);
    }

    [Fact]
    public async Task Timeout_ReturnsUnavailable_NotAnException()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out"));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("token", null, CancellationToken.None);

        Assert.Equal(TurnstileVerificationResult.Unavailable, result);
    }

    [Fact]
    public async Task UnparsableBody_ReturnsUnavailable_NotAnException()
    {
        var handler = new StubHandler(_ => JsonResponse(HttpStatusCode.OK, "not json"));
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("token", null, CancellationToken.None);

        Assert.Equal(TurnstileVerificationResult.Unavailable, result);
    }

    [Fact]
    public async Task RequestCarriesSecretResponseAndRemoteIp()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return JsonResponse(HttpStatusCode.OK, """{"success":true}""");
        });
        var verifier = CreateVerifier(handler);

        await verifier.VerifyAsync("the-token", "198.51.100.7", CancellationToken.None);

        Assert.NotNull(captured);
        var form = await captured!.Content!.ReadAsStringAsync();
        Assert.Contains($"secret={SecretKey}", form, StringComparison.Ordinal);
        Assert.Contains("response=the-token", form, StringComparison.Ordinal);
        Assert.Contains("remoteip=198.51.100.7", form, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRemoteIp_StillVerifies_WithoutARemoteIpFormField()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return JsonResponse(HttpStatusCode.OK, """{"success":true}""");
        });
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("the-token", null, CancellationToken.None);

        Assert.Equal(TurnstileVerificationResult.Success, result);
        var form = await captured!.Content!.ReadAsStringAsync();
        Assert.DoesNotContain("remoteip", form, StringComparison.Ordinal);
    }

    private static TurnstileVerifier CreateVerifier(HttpMessageHandler handler, ILogger<TurnstileVerifier>? logger = null)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://challenges.cloudflare.com/turnstile/v0/"),
        };
        var options = new ContactOptions { Turnstile = { SecretKey = SecretKey, SiteKey = "site-key" } };
        return new TurnstileVerifier(httpClient, Options.Create(options), logger ?? NullLogger<TurnstileVerifier>.Instance);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string payload) =>
        new(status) { Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
