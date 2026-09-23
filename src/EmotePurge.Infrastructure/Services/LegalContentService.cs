using System.Collections.Concurrent;
using EmotePurge.Core.Services;
using Markdig;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Reads <c>imprint.{de,en}.md</c> / <c>privacy.{de,en}.md</c> from <see cref="LegalContentOptions.ContentPath"/>
/// and renders them to HTML. Registered as a singleton (see <c>ServiceCollectionExtensions</c>) — the
/// per-file cache below is the only state it holds, and it must survive across requests to be worth
/// having at all.
/// </summary>
/// <remarks>
/// Rendering runs through a single shared <see cref="MarkdownPipeline"/> with
/// <c>DisableHtml()</c>: Markdig then treats any literal HTML in the operator's file (a stray
/// <c>&lt;script&gt;</c>, an inline <c>&lt;img onerror&gt;</c>) as plain text and escapes it on
/// output instead of passing it through, so nothing in the file can inject markup. The frontend
/// still binds the result through Angular's built-in sanitizer on top of that — defence in depth,
/// not a sign this layer is assumed insufficient.
///
/// Caching is per file, keyed by its own last-write time (checked with a cheap
/// <see cref="File.GetLastWriteTimeUtc(string)"/> call on every request): an operator edit is picked
/// up on the next request, no restart required. This was chosen over an mtime-blind cache (which
/// would need a restart to ever show an edit) and over no cache at all (which would re-parse
/// Markdown on every page view) — the file is small and local disk, so the existence check itself is
/// the only cost paid on a cache hit.
/// </remarks>
public sealed class LegalContentService : ILegalContentService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    private readonly LegalContentOptions _options;
    private readonly ConcurrentDictionary<string, CachedRender> _cache = new();

    public LegalContentService(LegalContentOptions options)
    {
        _options = options;
    }

    public Task<LegalDocumentAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new LegalDocumentAvailability(
            ImprintAvailable: GermanFileExists(LegalDocumentKind.Imprint),
            PrivacyAvailable: GermanFileExists(LegalDocumentKind.Privacy)));
    }

    public async Task<LegalDocument?> GetDocumentAsync(
        LegalDocumentKind kind, LegalLanguage language, CancellationToken cancellationToken)
    {
        var germanPath = GermanPath(kind);
        if (germanPath is null || !File.Exists(germanPath))
        {
            // German is authoritative (interface remarks): no German file means "not configured",
            // regardless of what an English file next to it might hold.
            return null;
        }

        if (language == LegalLanguage.German)
        {
            var html = await RenderCachedAsync(germanPath, cancellationToken);
            return new LegalDocument(html, IsGermanFallback: false);
        }

        var englishPath = EnglishPath(kind);
        if (englishPath is not null && File.Exists(englishPath))
        {
            var html = await RenderCachedAsync(englishPath, cancellationToken);
            return new LegalDocument(html, IsGermanFallback: false);
        }

        var germanHtml = await RenderCachedAsync(germanPath, cancellationToken);
        return new LegalDocument(germanHtml, IsGermanFallback: true);
    }

    private bool GermanFileExists(LegalDocumentKind kind)
    {
        var path = GermanPath(kind);
        return path is not null && File.Exists(path);
    }

    private string? GermanPath(LegalDocumentKind kind) => BuildPath(kind, "de");

    private string? EnglishPath(LegalDocumentKind kind) => BuildPath(kind, "en");

    private string? BuildPath(LegalDocumentKind kind, string languageTag)
    {
        if (string.IsNullOrWhiteSpace(_options.ContentPath))
        {
            return null;
        }

        var fileName = kind switch
        {
            LegalDocumentKind.Imprint => "imprint",
            LegalDocumentKind.Privacy => "privacy",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        return Path.Combine(_options.ContentPath, $"{fileName}.{languageTag}.md");
    }

    private async Task<string> RenderCachedAsync(string path, CancellationToken cancellationToken)
    {
        var lastWriteUtc = File.GetLastWriteTimeUtc(path);
        if (_cache.TryGetValue(path, out var cached) && cached.LastWriteUtc == lastWriteUtc)
        {
            return cached.Html;
        }

        var markdown = await File.ReadAllTextAsync(path, cancellationToken);
        var html = Markdown.ToHtml(markdown, Pipeline);
        _cache[path] = new CachedRender(lastWriteUtc, html);
        return html;
    }

    private sealed record CachedRender(DateTime LastWriteUtc, string Html);
}
