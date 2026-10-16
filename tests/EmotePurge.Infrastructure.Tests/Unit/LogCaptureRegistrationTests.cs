using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Container-free: building the provider constructs nothing that connects (the data source is lazy).
public class LogCaptureRegistrationTests
{
    [Fact]
    public void AddLogCapture_Disabled_RegistersNoProviderAndNoBuffer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLogCapture(Configuration("false"), LogSource.Api);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ILoggerProvider) && d.ImplementationFactory is not null);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(LogCaptureBuffer));
        Assert.Contains(services, d => d.ServiceType == typeof(LogCaptureIdentity));
        Assert.Contains(services, d => d.ServiceType == typeof(ILogCaptureStore));
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));

        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<ILoggerProvider>().OfType<LogCaptureProvider>());
        Assert.IsType<LogCaptureHeartbeat>(Assert.Single(provider.GetServices<IHostedService>()));
    }

    [Fact]
    public void AddLogCapture_Enabled_ProviderReachesLoggerFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLogCapture(Configuration("true"), LogSource.Worker);

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<ILoggerProvider>().OfType<LogCaptureProvider>());
        var buffer = provider.GetRequiredService<LogCaptureBuffer>();

        provider.GetRequiredService<ILoggerFactory>().CreateLogger("EmotePurge.Test").LogInformation("hello capture");

        Assert.True(buffer.Reader.TryRead(out var entry));
        Assert.Equal("hello capture", entry.Message);
        Assert.IsType<LogCaptureWriter>(provider.GetServices<IHostedService>().First());
    }

    [Fact]
    public void AddLogCapture_Enabled_DisposingTheContainerClosesTheCapture()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLogCapture(Configuration("true"), LogSource.Api);

        var provider = services.BuildServiceProvider();
        var buffer = provider.GetRequiredService<LogCaptureBuffer>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("EmotePurge.Test");
        provider.Dispose();

        logger.LogInformation("after close");

        Assert.False(buffer.Reader.TryRead(out _));
    }

    [Fact]
    public void AddLogCapture_WithoutTheSetting_DefaultsToEnabled()
    {
        var services = new ServiceCollection();
        services.AddLogCapture(new ConfigurationBuilder().Build(), LogSource.Api);

        Assert.Contains(services, d => d.ServiceType == typeof(LogCaptureBuffer));
    }

    private static IConfiguration Configuration(string enabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LogCapture:Enabled"] = enabled,
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=none;Username=none;Password=none"
        }).Build();
}
