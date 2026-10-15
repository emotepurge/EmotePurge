using Xunit;
using EmotePurge.Infrastructure.Logging.Query;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Worker;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class LogFilterSeedTests
{
    // The migration seeds these four verbatim (AddLogCapture); keep both in sync.
    private static readonly string[] SeedFilters =
    [
        "instance:latest uptime<15m",
        "source:worker cat:TwitchConnectionWatchdog,TwitchChatManager,TwitchLib.* -\"angestoßen (Quelle\" -\" gejoint.\"",
        "source:worker cat:SevenTv*",
        "level>=error",
    ];

    [Fact]
    public void AllFourSeeds_Parse()
    {
        foreach (var seed in SeedFilters)
        {
            Assert.True(LogFilterParser.Parse(seed).IsSuccess, seed);
        }
    }

    [Fact]
    public void TwitchReconnects_ParsesIntoTheExpectedTerms()
    {
        var terms = LogFilterParser.Parse(SeedFilters[1]).ExpressionOrThrow().Terms;
        Assert.Equal(4, terms.Count);
        Assert.IsType<SourceTerm>(terms[0]);
        Assert.Equal(["TwitchConnectionWatchdog", "TwitchChatManager", "TwitchLib.*"], Assert.IsType<CategoryTerm>(terms[1]).Patterns.Select(p => p.Value));
        var excludes = terms.Skip(2).Cast<FreeTextTerm>().ToList();
        Assert.All(excludes, t => Assert.True(t.Negated));
        Assert.Equal(["angestoßen (Quelle", " gejoint."], excludes.Select(t => t.Pattern.Value));
    }

    [Fact]
    public void SeedCategories_ExistInCode()
    {
        // cat:TwitchConnectionWatchdog and cat:TwitchChatManager match the type name as the last segment of the category.
        Assert.Equal("EmotePurge.Worker.TwitchConnectionWatchdog", typeof(TwitchConnectionWatchdog).FullName);
        Assert.Equal("EmotePurge.Worker.TwitchChatManager", typeof(TwitchChatManager).FullName);

        // cat:SevenTv* has to hit classes in the worker and in infrastructure.
        var worker = typeof(TwitchChatManager).Assembly.GetTypes().Where(t => t.IsClass && t.Name.StartsWith("SevenTv", StringComparison.Ordinal));
        var infrastructure = typeof(SevenTvSyncService).Assembly.GetTypes().Where(t => t.IsClass && t.Name.StartsWith("SevenTv", StringComparison.Ordinal));
        Assert.Contains(worker, t => t.Name == "SevenTvEventClient");
        Assert.Contains(worker, t => t.Name == "SevenTvEventWorker");
        Assert.Contains(worker, t => t.Name == "SevenTvPeriodicResyncWorker");
        Assert.Contains(infrastructure, t => t.Name == "SevenTvSyncService");
        Assert.Contains(infrastructure, t => t.Name == "SevenTvApiClient");

        // cat:TwitchLib.* is the library's own namespace.
        Assert.StartsWith("TwitchLib.", typeof(TwitchLib.Client.TwitchClient).FullName);
    }

    [Fact]
    public void SeedPhrases_StillInSource()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "EmotePurge.Worker", "TwitchChatManager.cs"));
        var phrases = LogFilterParser.Parse(SeedFilters[1]).ExpressionOrThrow().Terms.OfType<FreeTextTerm>().Select(t => t.Pattern.Value).ToList();
        Assert.Equal(2, phrases.Count);
        Assert.All(phrases, phrase => Assert.Contains(phrase, source));
        Assert.Contains("angestoßen (Quelle", source);
        Assert.Contains(" gejoint.", source);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EmotePurge.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("EmotePurge.slnx not found above the test output directory.");
    }
}
