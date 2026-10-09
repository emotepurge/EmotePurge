using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.ChatLogArchive;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class ChatLogBackfillOptionsTests
{
    [Fact]
    public void Defaults_AreTheSpecifiedOnesAndTheFeatureIsOff()
    {
        var options = new ChatLogBackfillOptions();

        options.Validate();

        Assert.False(options.Enabled);
        Assert.Equal(10, options.RequestDelaySeconds);
        Assert.Equal(256, options.MaxBlockMegabytes);
        Assert.Equal(900, options.MaxRetryAfterSeconds);
        Assert.Equal(3, options.TransportRetries);
        Assert.Equal(10, options.MaxConsecutivePauses);
        Assert.Equal(60, options.IdlePollSeconds);
        Assert.Equal(2, options.CancelPollSeconds);
    }

    [Theory]
    [InlineData("IdlePollSeconds", "ChatLogBackfill:IdlePollSeconds")]
    [InlineData("CancelPollSeconds", "ChatLogBackfill:CancelPollSeconds")]
    public void Validate_RejectsAZeroPollInterval_NamingTheKey(string property, string key)
    {
        var options = new ChatLogBackfillOptions();
        typeof(ChatLogBackfillOptions).GetProperty(property)!.SetValue(options, 0);

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void ArchiveOptions_Validate_RejectsAnUnusableLineLimitOrDeadline_AndAcceptsTheDefaults()
    {
        new ChatLogArchiveOptions().Validate();

        var line = Assert.Throws<InvalidOperationException>(new ChatLogArchiveOptions { MaxLineBytes = 0 }.Validate);
        var timeout = Assert.Throws<InvalidOperationException>(new ChatLogArchiveOptions { RangeBodyTimeout = TimeSpan.Zero }.Validate);

        Assert.Contains("ChatLogArchive:MaxLineBytes", line.Message);
        Assert.Contains("ChatLogArchive:RangeBodyTimeout", timeout.Message);
    }

    [Fact]
    public void Registration_FailsFastOnAnInvalidArchiveValue()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(new Dictionary<string, string?> { ["ChatLogArchive:MaxLineBytes"] = "0" }));
        Assert.Contains("ChatLogArchive:MaxLineBytes", ex.Message);
    }

    [Theory]
    [InlineData(0, 256, 3, "ChatLogBackfill:RequestDelaySeconds")]
    [InlineData(10, 0, 3, "ChatLogBackfill:MaxBlockMegabytes")]
    [InlineData(10, 256, 0, "ChatLogBackfill:TransportRetries")]
    [InlineData(-1, 256, 3, "ChatLogBackfill:RequestDelaySeconds")]
    public void Validate_RejectsUnusableValues_NamingTheKey(int delay, int megabytes, int retries, string key)
    {
        var options = new ChatLogBackfillOptions { RequestDelaySeconds = delay, MaxBlockMegabytes = megabytes, TransportRetries = retries };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void ArchiveOptions_DefaultToCyexAndTheRangeLimits()
    {
        var options = new ChatLogArchiveOptions();

        Assert.Equal("https://logs.cyex.app/", options.BaseUrl);
        Assert.Equal(TimeSpan.FromMinutes(5), options.RangeBodyTimeout);
        Assert.Equal(16384, options.MaxLineBytes);
    }

    [Fact]
    public void TheBackfillRetentionPeriod_Is365Days() =>
        Assert.Equal(TimeSpan.FromDays(365), RetentionPolicy.ChatLogBackfillRun);

    [Fact]
    public void Registration_BindsTheSection_AndFailsFastOnAnInvalidValue()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ChatLogBackfill:Enabled"] = "true",
            ["ChatLogBackfill:MaxBlockMegabytes"] = "64"
        });

        var options = provider.GetRequiredService<ChatLogBackfillOptions>();
        Assert.True(options.Enabled);
        Assert.Equal(64, options.MaxBlockMegabytes);

        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(new Dictionary<string, string?> { ["ChatLogBackfill:TransportRetries"] = "0" }));
        Assert.Contains("ChatLogBackfill:TransportRetries", ex.Message);
    }

    [Fact]
    public void Registration_ConfiguresTheArchiveClientWithTheProjectUserAgentAndTheConfiguredBaseUrl()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { ["ChatLogArchive:BaseUrl"] = "https://archive.example.test/" });

        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IChatLogArchiveClient));

        Assert.Equal(new Uri("https://archive.example.test/"), http.BaseAddress);
        Assert.Equal("EmotePurge (+https://emotepurge.app)", http.DefaultRequestHeaders.UserAgent.ToString());
        Assert.IsType<ChatLogArchiveClient>(provider.GetRequiredService<IChatLogArchiveClient>());
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var all = new Dictionary<string, string?>(settings)
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused",
            ["Redis:ConnectionString"] = "localhost:6379"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(all).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEmotePurgeInfrastructure(configuration);
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        return services.BuildServiceProvider();
    }
}
