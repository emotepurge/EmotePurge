using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// GET /api/contact/config and POST /api/contact (docs/DECISIONS.md 2026-09-24, "contact form").
/// Both routes are deliberately anonymous — no <see cref="TestAuthHandler"/> header anywhere in this
/// class — same reasoning as <see cref="LegalEndpointsTests"/>.
/// </summary>
/// <remarks>
/// Every POST test builds its own isolated host (<see cref="NewIsolatedClient"/>) rather than
/// reusing <see cref="ApiFactory"/>'s shared <c>Contact</c> substitute and shared
/// <c>HttpClient</c>, for two reasons the class fixture alone cannot solve: the real
/// <c>RateLimitPolicyNames.Contact</c> policy only grants three permits before a 20-minute refill —
/// a handful of tests sharing one in-memory limiter would start failing each other with a real 429
/// — and several tests assert "the service was never called", which is only meaningful against a
/// substitute this one test's requests are the sole caller of (the same caveat
/// <see cref="LegalEndpointsTests"/> documents for its own shared substitute).
/// </remarks>
public class ContactEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ContactEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetConfig_Anonymous_Available_ReturnsTrueAndSiteKey()
    {
        _factory.Contact.GetAvailability().Returns(new ContactAvailability(Available: true, TurnstileSiteKey: "public-site-key"));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/contact/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConfigBody>();
        Assert.NotNull(body);
        Assert.True(body.Available);
        Assert.Equal("public-site-key", body.TurnstileSiteKey);
    }

    [Fact]
    public async Task GetConfig_Anonymous_NotAvailable_ReturnsFalseAndNullSiteKey()
    {
        _factory.Contact.GetAvailability().Returns(new ContactAvailability(Available: false, TurnstileSiteKey: null));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/contact/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConfigBody>();
        Assert.NotNull(body);
        Assert.False(body.Available);
        Assert.Null(body.TurnstileSiteKey);
    }

    [Fact]
    public async Task Post_Honeypot_NonEmptyWebsite_Answers204_WithoutCallingTheService()
    {
        var (factory, client, contact) = NewIsolatedClient(_factory);
        using var disposeFactory = factory;
        using var disposeClient = client;

        var response = await client.PostAsJsonAsync("/api/contact", new
        {
            Name = "Bot",
            Email = "bot@example.com",
            Message = "This is a spam message from a bot filling every field.",
            TurnstileToken = "token",
            Website = "https://spam.example.com",
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await contact.DidNotReceive().SubmitAsync(
            Arg.Any<ContactSubmission>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Jane", "jane@example.com", "", "token")] // empty message
    [InlineData("Jane", "jane@example.com", "short", "token")] // message under 10 chars
    [InlineData("Jane", "not-an-email", "A message that is definitely long enough.", "token")] // invalid email
    [InlineData("Jane", "", "A message that is definitely long enough.", "token")] // empty email
    [InlineData("Jane", "jane@example.com", "A message that is definitely long enough.", "")] // missing turnstile token
    public async Task Post_InvalidShape_Answers400WithContactInvalid_WithoutCallingTheService(
        string name, string email, string message, string turnstileToken)
    {
        var (factory, client, contact) = NewIsolatedClient(_factory);
        using var disposeFactory = factory;
        using var disposeClient = client;

        var response = await client.PostAsJsonAsync("/api/contact", new
        {
            Name = name,
            Email = email,
            Message = message,
            TurnstileToken = turnstileToken,
            Website = "",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("contact_invalid", body?.ErrorCode);
        await contact.DidNotReceive().SubmitAsync(
            Arg.Any<ContactSubmission>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Post_MessageOverMaxLength_Answers400WithContactInvalid()
    {
        var (factory, client, contact) = NewIsolatedClient(_factory);
        using var disposeFactory = factory;
        using var disposeClient = client;

        var response = await client.PostAsJsonAsync("/api/contact", new
        {
            Name = "Jane",
            Email = "jane@example.com",
            Message = new string('a', 5001),
            TurnstileToken = "token",
            Website = "",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("contact_invalid", body?.ErrorCode);
        await contact.DidNotReceive().SubmitAsync(
            Arg.Any<ContactSubmission>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Post_NameOverMaxLength_Answers400WithContactInvalid()
    {
        var (factory, client, contact) = NewIsolatedClient(_factory);
        using var disposeFactory = factory;
        using var disposeClient = client;

        var response = await client.PostAsJsonAsync("/api/contact", new
        {
            Name = new string('a', 101),
            Email = "jane@example.com",
            Message = "A message that is definitely long enough.",
            TurnstileToken = "token",
            Website = "",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("contact_invalid", body?.ErrorCode);
        await contact.DidNotReceive().SubmitAsync(
            Arg.Any<ContactSubmission>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Post_NameWithControlCharacter_Answers400WithContactInvalid()
    {
        var (factory, client, _) = NewIsolatedClient(_factory);
        using var disposeFactory = factory;
        using var disposeClient = client;

        var response = await client.PostAsJsonAsync("/api/contact", new
        {
            Name = "Jane\r\nBcc: evil@example.com",
            Email = "jane@example.com",
            Message = "A message that is definitely long enough.",
            TurnstileToken = "token",
            Website = "",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("contact_invalid", body?.ErrorCode);
    }

    [Fact]
    public async Task Post_NameOptional_MissingName_StillValid_CallsTheServiceWithNullName()
    {
        var (factory, client, contact) = NewIsolatedClient(_factory);
        using var disposeFactory = factory;
        using var disposeClient = client;

        var response = await client.PostAsJsonAsync("/api/contact", new
        {
            Email = "jane@example.com",
            Message = "A message that is definitely long enough.",
            TurnstileToken = "token",
            Website = "",
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await contact.Received(1).SubmitAsync(
            Arg.Is<ContactSubmission>(s => s.Name == null && s.Email == "jane@example.com"),
            "token",
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Post_ServiceReturnsCaptchaFailed_Answers400WithContactCaptchaFailed()
    {
        var (factory, client, contact) = NewIsolatedClient(_factory);
        using var disposeFactory = factory;
        using var disposeClient = client;
        contact.SubmitAsync(Arg.Any<ContactSubmission>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ContactSubmissionOutcome.CaptchaFailed);

        var response = await client.PostAsJsonAsync("/api/contact", ValidBody());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("contact_captcha_failed", body?.ErrorCode);
    }

    [Fact]
    public async Task Post_ServiceReturnsUnavailable_Answers503WithContactUnavailable()
    {
        var (factory, client, contact) = NewIsolatedClient(_factory);
        using var disposeFactory = factory;
        using var disposeClient = client;
        contact.SubmitAsync(Arg.Any<ContactSubmission>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ContactSubmissionOutcome.Unavailable);

        var response = await client.PostAsJsonAsync("/api/contact", ValidBody());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("contact_unavailable", body?.ErrorCode);
    }

    [Fact]
    public async Task Post_ServiceReturnsGlobalLimitReached_Answers429WithRateLimitExceeded_AndRetryAfter()
    {
        var (factory, client, contact) = NewIsolatedClient(_factory);
        using var disposeFactory = factory;
        using var disposeClient = client;
        contact.SubmitAsync(Arg.Any<ContactSubmission>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ContactSubmissionOutcome.GlobalLimitReached);

        var response = await client.PostAsJsonAsync("/api/contact", ValidBody());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        var body = await response.Content.ReadFromJsonAsync<RateLimitBody>();
        Assert.Equal("rate_limit_exceeded", body?.ErrorCode);
        Assert.True(body?.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task Post_Success_Answers204()
    {
        var (factory, client, contact) = NewIsolatedClient(_factory);
        using var disposeFactory = factory;
        using var disposeClient = client;
        contact.SubmitAsync(Arg.Any<ContactSubmission>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ContactSubmissionOutcome.Sent);

        var response = await client.PostAsJsonAsync("/api/contact", ValidBody());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// Spot-checks the real, tightly-budgeted <c>Contact</c> policy actually guards the route: three
    /// permits, then a bare-but-real 429 from the ASP.NET Core rate limiter itself (not from the
    /// substituted service, which is never reached once the limiter rejects). A fresh, isolated host
    /// so this test's own three-permit burn cannot bleed into any other test in this class.
    /// </summary>
    [Fact]
    public async Task Post_RealContactPolicy_FourthRequestInOneWindow_Answers429()
    {
        var (factory, client, contact) = NewIsolatedClient(_factory);
        using var disposeFactory = factory;
        using var disposeClient = client;
        contact.SubmitAsync(Arg.Any<ContactSubmission>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ContactSubmissionOutcome.Sent);

        var statusCodes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await client.PostAsJsonAsync("/api/contact", ValidBody());
            statusCodes.Add(response.StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.TooManyRequests],
            statusCodes);
    }

    /// <summary>
    /// Codex Sol review (P2, docs/DECISIONS.md 2026-09-24 revision): the <c>Contact</c> policy used to
    /// partition through <c>RateLimitRejection.PartitionPerUser</c>, whose <c>ResolveUserKey</c>
    /// prefers the authenticated Twitch user id over the remote IP — right for a route that requires
    /// login, wrong here, since this route accepts a submission from an already-signed-in visitor just
    /// as readily as an anonymous one. Regression: three different authenticated users, all through
    /// the same <c>TestServer</c> connection (and therefore the same remote IP), against a two-permit
    /// bucket — a partitioner keyed on the user id would hand each of them their own fresh bucket and
    /// never reject the third; only a partitioner keyed on the shared IP does.
    /// </summary>
    [Fact]
    public async Task Post_RealContactPolicy_PartitionsByRemoteIp_NotByAuthenticatedUser()
    {
        var contact = Substitute.For<IContactSubmissionService>();
        contact.GetAvailability().Returns(new ContactAvailability(Available: true, TurnstileSiteKey: "test-site-key"));
        contact.SubmitAsync(Arg.Any<ContactSubmission>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ContactSubmissionOutcome.Sent);

        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            // A budget small enough to spend in two requests, so a third distinct user id is enough
            // to prove the partition either way — no need to burn through the real 3-permit/20-minute
            // production budget to make the point.
            builder.UseSetting("RateLimiting:Contact:TokenLimit", "2");
            builder.UseSetting("RateLimiting:Contact:TokensPerPeriod", "1");
            builder.UseSetting("RateLimiting:Contact:ReplenishmentPeriodSeconds", "1200");
            builder.ConfigureTestServices(services => services.AddScoped(_ => contact));
        });
        using var client = factory.CreateClient();

        using var first = await PostAsUserAsync(client, "contact-ip-partition-user-a");
        using var second = await PostAsUserAsync(client, "contact-ip-partition-user-b");
        using var third = await PostAsUserAsync(client, "contact-ip-partition-user-c");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        // A per-user partitioner would still have a fresh two-permit bucket for this third, distinct
        // user id — only a partitioner keyed on the shared remote IP rejects it.
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    private static Task<HttpResponseMessage> PostAsUserAsync(HttpClient client, string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/contact")
        {
            Content = JsonContent.Create(ValidBody()),
        };
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return client.SendAsync(request);
    }

    private static object ValidBody() => new
    {
        Name = "Jane",
        Email = "jane@example.com",
        Message = "A message that is definitely long enough to pass validation.",
        TurnstileToken = "token",
        Website = "",
    };

    /// <summary>
    /// A fresh host with its own <see cref="IContactSubmissionService"/> substitute and its own
    /// in-memory rate limiter — see the class remarks for why every POST test needs this rather than
    /// <see cref="ApiFactory"/>'s shared client.
    /// </summary>
    private static (WebApplicationFactory<Program> Factory, HttpClient Client, IContactSubmissionService Contact) NewIsolatedClient(
        ApiFactory baseFactory)
    {
        var contact = Substitute.For<IContactSubmissionService>();
        contact.GetAvailability().Returns(new ContactAvailability(Available: true, TurnstileSiteKey: "test-site-key"));
        contact.SubmitAsync(Arg.Any<ContactSubmission>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ContactSubmissionOutcome.Sent);

        var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddScoped(_ => contact)));

        return (factory, factory.CreateClient(), contact);
    }

    private sealed record ConfigBody(bool Available, string? TurnstileSiteKey);

    private sealed record ErrorBody(string ErrorCode);

    private sealed record RateLimitBody(string ErrorCode, int RetryAfterSeconds);
}
