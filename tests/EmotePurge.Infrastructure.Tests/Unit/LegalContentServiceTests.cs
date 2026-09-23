using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Pure filesystem I/O against real temp files — no Postgres/Redis, so this lives in Unit/ rather
// than Integration/ (root CLAUDE.md rule 11: the split follows whether the class under test touches
// real Postgres/Redis infrastructure, not whether it touches a filesystem at all).
public sealed class LegalContentServiceTests : IDisposable
{
    private readonly string _contentDirectory;

    public LegalContentServiceTests()
    {
        _contentDirectory = Directory.CreateTempSubdirectory("legal-content-tests-").FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_contentDirectory, recursive: true);
    }

    [Fact]
    public async Task GetAvailabilityAsync_ReportsFalse_WhenContentPathIsUnset()
    {
        var service = new LegalContentService(new LegalContentOptions { ContentPath = null });

        var availability = await service.GetAvailabilityAsync(CancellationToken.None);

        Assert.False(availability.ImprintAvailable);
        Assert.False(availability.PrivacyAvailable);
    }

    [Fact]
    public async Task GetAvailabilityAsync_ReportsFalse_WhenContentPathPointsAtAnEmptyDirectory()
    {
        var service = NewService();

        var availability = await service.GetAvailabilityAsync(CancellationToken.None);

        Assert.False(availability.ImprintAvailable);
        Assert.False(availability.PrivacyAvailable);
    }

    [Fact]
    public async Task GetAvailabilityAsync_ReportsTrue_OnlyForDocumentsWithAGermanFile()
    {
        WriteFile("imprint.de.md", "# Impressum");
        var service = NewService();

        var availability = await service.GetAvailabilityAsync(CancellationToken.None);

        Assert.True(availability.ImprintAvailable);
        Assert.False(availability.PrivacyAvailable);
    }

    [Fact]
    public async Task GetDocumentAsync_ReturnsNull_WhenContentPathIsUnset()
    {
        var service = new LegalContentService(new LegalContentOptions { ContentPath = null });

        var document = await service.GetDocumentAsync(
            LegalDocumentKind.Imprint, LegalLanguage.German, CancellationToken.None);

        Assert.Null(document);
    }

    [Fact]
    public async Task GetDocumentAsync_ReturnsNull_WhenTheGermanFileIsMissing()
    {
        // English alone is not a valid configuration — German is authoritative (interface remarks).
        WriteFile("imprint.en.md", "# Imprint");
        var service = NewService();

        var document = await service.GetDocumentAsync(
            LegalDocumentKind.Imprint, LegalLanguage.English, CancellationToken.None);

        Assert.Null(document);
    }

    [Fact]
    public async Task GetDocumentAsync_German_RendersTheGermanFile()
    {
        WriteFile("privacy.de.md", "# Datenschutz\n\nWir erheben keine Daten.");
        var service = NewService();

        var document = await service.GetDocumentAsync(
            LegalDocumentKind.Privacy, LegalLanguage.German, CancellationToken.None);

        Assert.NotNull(document);
        Assert.False(document.IsGermanFallback);
        Assert.Contains("<h1>Datenschutz</h1>", document.Html);
        Assert.Contains("Wir erheben keine Daten.", document.Html);
    }

    [Fact]
    public async Task GetDocumentAsync_English_PrefersTheEnglishFile_WhenBothExist()
    {
        WriteFile("imprint.de.md", "# Impressum");
        WriteFile("imprint.en.md", "# Imprint");
        var service = NewService();

        var document = await service.GetDocumentAsync(
            LegalDocumentKind.Imprint, LegalLanguage.English, CancellationToken.None);

        Assert.NotNull(document);
        Assert.False(document.IsGermanFallback);
        Assert.Contains("<h1>Imprint</h1>", document.Html);
    }

    [Fact]
    public async Task GetDocumentAsync_English_FallsBackToGerman_WhenNoEnglishFileExists()
    {
        WriteFile("imprint.de.md", "# Impressum");
        var service = NewService();

        var document = await service.GetDocumentAsync(
            LegalDocumentKind.Imprint, LegalLanguage.English, CancellationToken.None);

        Assert.NotNull(document);
        Assert.True(document.IsGermanFallback);
        Assert.Contains("<h1>Impressum</h1>", document.Html);
    }

    [Fact]
    public async Task GetDocumentAsync_NeverEmitsRawHtml_FromTheMarkdownFile()
    {
        // Markdig's DisableHtml() must turn a literal <script> tag in the operator's file into
        // escaped text, not pass it through — the one behaviour this whole feature depends on to be
        // safe (issue #247: rendering happens server-side specifically so nothing from the file can
        // inject a tag).
        WriteFile("privacy.de.md", "# Datenschutz\n\n<script>alert('xss')</script>\n\nEnde.");
        var service = NewService();

        var document = await service.GetDocumentAsync(
            LegalDocumentKind.Privacy, LegalLanguage.German, CancellationToken.None);

        Assert.NotNull(document);
        Assert.DoesNotContain("<script>", document.Html);
        Assert.DoesNotContain("</script>", document.Html);
        Assert.Contains("&lt;script&gt;", document.Html);
    }

    [Fact]
    public async Task GetDocumentAsync_PicksUpAFileEditWithoutRestart()
    {
        var path = WriteFile("imprint.de.md", "# Erste Fassung");
        var service = NewService();
        var first = await service.GetDocumentAsync(
            LegalDocumentKind.Imprint, LegalLanguage.German, CancellationToken.None);
        Assert.Contains("Erste Fassung", first!.Html);

        // Give the filesystem's mtime resolution room to actually move before overwriting — the
        // cache keys off File.GetLastWriteTimeUtc, and a same-tick rewrite could otherwise report an
        // unchanged mtime on a coarse filesystem clock.
        await Task.Delay(50);
        File.WriteAllText(path, "# Zweite Fassung");

        var second = await service.GetDocumentAsync(
            LegalDocumentKind.Imprint, LegalLanguage.German, CancellationToken.None);

        Assert.Contains("Zweite Fassung", second!.Html);
        Assert.DoesNotContain("Erste Fassung", second.Html);
    }

    private LegalContentService NewService() => new(new LegalContentOptions { ContentPath = _contentDirectory });

    private string WriteFile(string fileName, string content)
    {
        var path = Path.Combine(_contentDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
