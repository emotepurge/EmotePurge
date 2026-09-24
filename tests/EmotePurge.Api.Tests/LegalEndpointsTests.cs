using System.Net;
using System.Net.Http.Json;
using EmotePurge.Core.Services;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// GET /api/legal/availability and GET /api/legal/{kind}/{language} (issue #247). Both routes are
/// deliberately anonymous — no <see cref="TestAuthHandler"/> header is set anywhere in this class —
/// and the point of this suite is exactly that: an unauthenticated <see cref="HttpClient"/> reaches
/// the handler at all, unlike almost everything else this factory's other test classes cover.
/// </summary>
public class LegalEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LegalEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Availability_Anonymous_Answers200WithBothFlags()
    {
        _factory.LegalContent.GetAvailabilityAsync(Arg.Any<CancellationToken>())
            .Returns(new LegalDocumentAvailability(ImprintAvailable: true, PrivacyAvailable: false));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/legal/availability");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AvailabilityBody>();
        Assert.NotNull(body);
        Assert.True(body.ImprintAvailable);
        Assert.False(body.PrivacyAvailable);
    }

    [Fact]
    public async Task GetDocument_Anonymous_KnownConfiguredDocument_Answers200()
    {
        _factory.LegalContent.GetDocumentAsync(
                LegalDocumentKind.Imprint, LegalLanguage.German, Arg.Any<CancellationToken>())
            .Returns(new LegalDocument("<h1>Impressum</h1>", IsGermanFallback: false));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/legal/imprint/de");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DocumentBody>();
        Assert.NotNull(body);
        Assert.Equal("<h1>Impressum</h1>", body.Html);
        Assert.False(body.IsGermanFallback);
    }

    [Fact]
    public async Task GetDocument_Anonymous_GermanFallback_IsForwardedUnchanged()
    {
        _factory.LegalContent.GetDocumentAsync(
                LegalDocumentKind.Privacy, LegalLanguage.English, Arg.Any<CancellationToken>())
            .Returns(new LegalDocument("<h1>Datenschutz</h1>", IsGermanFallback: true));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/legal/privacy/en");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DocumentBody>();
        Assert.NotNull(body);
        Assert.True(body.IsGermanFallback);
    }

    [Fact]
    public async Task GetDocument_NotConfigured_Answers404WithErrorCode()
    {
        // ApiFactory's ILegalContentService substitute returns null from GetDocumentAsync by
        // default (NSubstitute's default for an unconfigured Task<T?> member) — nothing to arrange.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/legal/privacy/de");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("legal_document_not_found", body?.ErrorCode);
    }

    // Deliberately not asserting "the service was never called" here: ApiFactory's ILegalContentService
    // substitute is one instance shared (via IClassFixture) across every test in this class, and
    // NSubstitute's Received()/DidNotReceive() looks at the substitute's whole call history, not just
    // this test's — a call-count assertion here would depend on execution order against the other
    // facts above. The 404 plus the stable error code is what the frontend can act on either way.
    [Theory]
    [InlineData("nonsense", "de")]
    [InlineData("imprint", "fr")]
    [InlineData("Imprint", "de")]
    public async Task GetDocument_UnknownKindOrLanguage_Answers404(string kind, string language)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/legal/{kind}/{language}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("legal_document_not_found", body?.ErrorCode);
    }

    private sealed record AvailabilityBody(bool ImprintAvailable, bool PrivacyAvailable);

    private sealed record DocumentBody(string Html, bool IsGermanFallback);

    private sealed record ErrorBody(string ErrorCode);
}
