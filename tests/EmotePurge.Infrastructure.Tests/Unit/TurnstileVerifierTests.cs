using System.Net;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Contact;
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

    private static TurnstileVerifier CreateVerifier(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://challenges.cloudflare.com/turnstile/v0/"),
        };
        var options = new ContactOptions { Turnstile = { SecretKey = SecretKey, SiteKey = "site-key" } };
        return new TurnstileVerifier(httpClient, Options.Create(options), NullLogger<TurnstileVerifier>.Instance);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string payload) =>
        new(status) { Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
