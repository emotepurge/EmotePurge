using System.Globalization;
using System.Net;
using System.Security.Claims;
using EmotePurge.Api.Auth;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// A 401 in front of the self-service deletion has four causes (user row gone, session revoked,
/// cookie absent or expired, legacy cookie without the issue-time claim), and only the first proves
/// the account no longer exists. These tests pin that the status code says so: 410 for the first
/// cause on <c>DELETE /api/auth/me</c> and nowhere else, 401 for everything else.
/// </summary>
public class SessionRejectionTests
{
    [Theory]
    [InlineData(SessionRejectionReason.UserGone, "DELETE", "/api/auth/me", "1", "1", 410)]
    [InlineData(SessionRejectionReason.UserGone, "delete", "/API/auth/me/", "1", "1", 410)]
    [InlineData(SessionRejectionReason.UserGone, "DELETE", "/api/auth/me", "1", "2", 401)]
    [InlineData(SessionRejectionReason.UserGone, "DELETE", "/api/auth/me", "1", null, 401)]
    [InlineData(SessionRejectionReason.UserGone, "DELETE", "/api/auth/me", "1", "", 401)]
    [InlineData(SessionRejectionReason.UserGone, "DELETE", "/api/auth/me", null, null, 401)]
    [InlineData(SessionRejectionReason.Other, "DELETE", "/api/auth/me", "1", "1", 401)]
    [InlineData(null, "DELETE", "/api/auth/me", "1", "1", 401)]
    [InlineData(SessionRejectionReason.UserGone, "GET", "/api/auth/me", "1", "1", 401)]
    [InlineData(SessionRejectionReason.UserGone, "DELETE", "/api/channels/foo", "1", "1", 401)]
    [InlineData(SessionRejectionReason.UserGone, "POST", "/api/auth/logout", "1", "1", 401)]
    [InlineData(SessionRejectionReason.Other, "GET", "/api/auth/me", "1", "1", 401)]
    [InlineData(null, "GET", null, null, null, 401)]
    public void ChallengeStatusCode_AnswersGoneOnlyForUserGoneOnSelfDeletionOfTheSameAccount(
        SessionRejectionReason? reason, string method, string? path, string? rejectedId, string? expectedId, int expected)
    {
        Assert.Equal(expected, SessionRejection.ChallengeStatusCode(reason, method, path, rejectedId, expectedId));
    }
}

/// <summary>
/// Smoke cases against the REAL cookie scheme (the shared <see cref="ApiFactory"/> replaces it with
/// a header handler). A validly protected ticket is minted through the scheme's own ticket data
/// format and sent as a raw <c>Cookie</c> header — <c>HttpClient</c> would not store a Secure cookie
/// over the TestServer's http address, but nothing forces it to: the header is all the server reads.
/// </summary>
public class SessionRejectionCookieSchemeTests : IClassFixture<SessionRejectionCookieSchemeTests.CookieFactory>
{
    private const string UserId = "123456";

    private readonly CookieFactory _factory;

    public SessionRejectionCookieSchemeTests(CookieFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DeleteMe_WhenUserRowIsGoneAndTheExpectedIdMatches_Answers410()
    {
        _factory.Users.CheckSessionAsync(UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((SessionCheckResult?)null);

        var response = await SendAsync(HttpMethod.Delete, $"/api/auth/me?expectedTwitchUserId={UserId}");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/me?expectedTwitchUserId=999")]
    [InlineData("/api/auth/me")]
    public async Task DeleteMe_WhenUserRowIsGoneButTheExpectedIdDiffersOrIsMissing_Answers401(string path)
    {
        _factory.Users.CheckSessionAsync(UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((SessionCheckResult?)null);

        var response = await SendAsync(HttpMethod.Delete, path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMe_WhenSessionIsRevoked_Answers401()
    {
        _factory.Users.CheckSessionAsync(UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new SessionCheckResult(IsValid: false));

        var response = await SendAsync(HttpMethod.Delete, $"/api/auth/me?expectedTwitchUserId={UserId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetMe_WhenUserRowIsGone_Answers401()
    {
        _factory.Users.CheckSessionAsync(UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((SessionCheckResult?)null);

        var response = await SendAsync(HttpMethod.Get, "/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMe_WithoutCookie_Answers401()
    {
        var response = await _factory.CreateClient().DeleteAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path)
    {
        var options = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, UserId),
                new Claim(TwitchClaimTypes.SessionIssuedAtUtc, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1), IsPersistent = true },
            CookieAuthenticationDefaults.AuthenticationScheme);
        var cookie = options.TicketDataFormat.Protect(ticket);

        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{options.Cookie.Name}={cookie}");
        return await _factory.CreateClient().SendAsync(request);
    }

    /// <summary>Like <see cref="ApiFactory"/> but keeps the real cookie scheme; only the user service and Redis are substituted.</summary>
    public sealed class CookieFactory : WebApplicationFactory<Program>
    {
        public IUserService Users { get; } = Substitute.For<IUserService>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Redis:ConnectionString", "localhost:6379");
            builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=none;Username=none;Password=none");
            builder.UseSetting("Auth:AdminTwitchLogins", string.Empty);
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped(_ => Users);
                services.AddScoped(_ => Substitute.For<IPendingMigrationGuard>());
                services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
            });
        }
    }
}
