using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Logging.Query;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The log viewer's statement time limit (spec 8.4) and its registration. Nothing connects: the Redis
// multiplexer is substituted and no query runs (RetentionOptionsTests pattern).
public class LogViewerOptionsTests
{
    [Fact]
    public void Default_IsFiveSeconds()
    {
        var options = new LogViewerOptions();

        options.Validate();

        Assert.Equal(5000, options.QueryTimeoutMilliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_RejectsANonPositiveLimit_NamingTheKey(int milliseconds)
    {
        var ex = Assert.Throws<InvalidOperationException>(new LogViewerOptions { QueryTimeoutMilliseconds = milliseconds }.Validate);

        Assert.Contains("LogViewer:QueryTimeoutMilliseconds", ex.Message);
    }

    [Fact]
    public void Registration_BindsTheSection_AndResolvesAScopedQueryService()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { ["LogViewer:QueryTimeoutMilliseconds"] = "1" });
        using var scope = provider.CreateScope();
        using var otherScope = provider.CreateScope();

        Assert.Equal(1, provider.GetRequiredService<LogViewerOptions>().QueryTimeoutMilliseconds);
        var service = scope.ServiceProvider.GetRequiredService<ILogQueryService>();
        Assert.IsType<LogQueryService>(service);
        Assert.Same(service, scope.ServiceProvider.GetRequiredService<ILogQueryService>());
        Assert.NotSame(service, otherScope.ServiceProvider.GetRequiredService<ILogQueryService>());
    }

    [Fact]
    public void Registration_FailsFastOnAnInvalidLimit()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(new Dictionary<string, string?> { ["LogViewer:QueryTimeoutMilliseconds"] = "0" }));

        Assert.Contains("LogViewer:QueryTimeoutMilliseconds", ex.Message);
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> viewerSettings)
    {
        var settings = new Dictionary<string, string?>(viewerSettings)
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused",
            ["Redis:ConnectionString"] = "localhost:6379"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEmotePurgeInfrastructure(configuration);
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        return services.BuildServiceProvider();
    }
}
