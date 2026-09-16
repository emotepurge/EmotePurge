using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// Guards the two halves of the reverse-proxy contract that were broken and invisible from
/// 2026-07-26 to 2026-09-16: <c>Program.cs</c> configured <c>ForwardedHeadersMiddleware</c> with
/// <c>KnownIPNetworks = { }</c> / <c>KnownProxies = { }</c>, which is a collection initializer
/// adding zero elements and not an assignment. The loopback defaults survived, the API in the
/// container was reached from the Docker bridge gateway, and every <c>X-Forwarded-Proto</c> and
/// <c>X-Forwarded-For</c> was dropped without a trace.
/// </summary>
/// <remarks>
/// <para>
/// The forwarded-header cases go through <see cref="TestServer.SendAsync(Action{HttpContext},
/// CancellationToken)"/> rather than <c>HttpClient</c>, because the whole decision hangs on
/// <c>Connection.RemoteIpAddress</c> and <c>HttpClient</c> against a TestServer leaves it
/// <see langword="null"/> — which is never trusted. A test written the obvious way would pass
/// against the broken configuration too and prove nothing; this one fails without the fix.
/// </para>
/// </remarks>
public class ForwardedHeadersTrustTests : IClassFixture<ApiFactory>
{
    /// <summary>Inside Docker's default bridge pool (172.16.0.0/12) — the gateway a host-native proxy arrives from.</summary>
    private const string TrustedProxyAddress = "172.18.0.1";

    /// <summary>TEST-NET-3 (RFC 5737), outside every trusted range.</summary>
    private const string UntrustedAddress = "203.0.113.5";

    private const string ForwardedClientAddress = "198.51.100.42";

    private readonly ApiFactory _factory;

    public ForwardedHeadersTrustTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ForwardedHeaders_FromDockerBridgeGateway_AreApplied()
    {
        var context = await SendAsync(TrustedProxyAddress);

        Assert.True(
            context.Request.IsHttps,
            "X-Forwarded-Proto: https from the Docker bridge gateway was ignored — Request.IsHttps stayed false.");
        Assert.Equal(ForwardedClientAddress, context.Connection.RemoteIpAddress?.ToString());
    }

    [Fact]
    public async Task ForwardedHeaders_FromUntrustedSender_AreIgnored()
    {
        var context = await SendAsync(UntrustedAddress);

        Assert.False(
            context.Request.IsHttps,
            "X-Forwarded-Proto from an untrusted sender was applied — the headers are spoofable.");
        Assert.Equal(UntrustedAddress, context.Connection.RemoteIpAddress?.ToString());
    }

    /// <summary>
    /// The OAuth state cookie is the CSRF defence of the login flow and now carries <c>Secure</c>
    /// unconditionally (<c>AuthEndpoints.cs</c>), like the session cookie since S2-10 — instead of
    /// deriving it from <c>Request.IsHttps</c>, which is exactly what the broken middleware
    /// configuration above was silently keeping false in production.
    /// </summary>
    [Fact]
    public async Task OAuthStateCookie_IsSecure_OverPlainHttp()
    {
        // The handler throws without these two, answering 500 with no Set-Cookie at all. Set here
        // rather than in ApiFactory so no other test's environment changes.
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Twitch:ClientId", "test-client-id");
            builder.UseSetting("Auth:Twitch:RedirectUri", "http://localhost/api/auth/twitch/callback");
        });

        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/api/auth/twitch/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("ep_oauth_state=", setCookie, StringComparison.Ordinal);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpContext> SendAsync(string remoteAddress)
        => await _factory.Server.SendAsync(
            context =>
            {
                context.Request.Method = HttpMethods.Get;
                context.Request.Scheme = "http";
                context.Request.Host = new HostString("localhost");

                // Any route that needs neither auth nor infrastructure; the /api fallback answers 404
                // and the middleware under test runs long before routing either way.
                context.Request.Path = "/api/does-not-exist";

                context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
                context.Request.Headers["X-Forwarded-Proto"] = "https";
                context.Request.Headers["X-Forwarded-For"] = ForwardedClientAddress;
            });
}
