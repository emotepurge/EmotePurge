using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Core.Services;
using EmotePurge.Worker.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The other half of the fail-open guard (Codex "Fail-open CLI"): even with the right arguments, the
// harness branch must not start a single hosted service, or a one-shot container would join IRC
// next to the production worker and count every message twice through the additive UPSERT.
//
// Container-free: nothing here connects. The Redis multiplexer is substituted because its
// registration is a factory that dials on first resolve, and resolving the runner reaches it
// through IChannelService -> IRedisPublisher.
public class WorkerServiceRegistrationTests
{
    [Fact]
    public void AddHarness_RegistersNoHostedService()
    {
        var services = new ServiceCollection();
        services.AddHarness(Configuration());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddWorkerHostedServices_RegistersExactlyTheTenOfTheWorker()
    {
        var services = new ServiceCollection();
        services.AddWorkerCore(Configuration());
        services.AddWorkerHostedServices();

        Assert.Equal(10, services.Count(d => d.ServiceType == typeof(IHostedService)));
    }

    [Fact]
    public void AddWorkerCore_AloneRegistersNoHostedService()
    {
        var services = new ServiceCollection();
        services.AddWorkerCore(Configuration());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddHarness_ResolvesTheRunnerWithItsWholeDependencyGraph()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var runner = scope.ServiceProvider.GetRequiredService<HarnessRunner>();

        Assert.NotNull(runner);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IChatLogArchiveClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IUsageStatQueryService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IChannelService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IBotChatterDetector>());
    }

    [Fact]
    public void HarnessOptions_CarryThePlannedDefaults()
    {
        using var provider = BuildProvider();

        var options = provider.GetRequiredService<HarnessOptions>();

        Assert.Equal("harness-reports", options.OutputDirectory);
        Assert.Equal(200, options.MaxMegabytesPerRun);
        Assert.Equal(30, options.WindowDays);
        Assert.Null(options.SharedChatCutover);
    }

    [Fact]
    public void HarnessOptions_ComeFromTheHarnessSection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=x;Username=u;Password=p",
            ["Redis:ConnectionString"] = "localhost:6379",
            ["Harness:OutputDirectory"] = "/tmp/anderswo",
            ["Harness:MaxMegabytesPerRun"] = "7",
            ["Harness:WindowDays"] = "14",
            ["Harness:SharedChatCutover"] = "2026-09-01"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddHarness(configuration);
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<HarnessOptions>();

        Assert.Equal("/tmp/anderswo", options.OutputDirectory);
        Assert.Equal(7, options.MaxMegabytesPerRun);
        Assert.Equal(14, options.WindowDays);
        // Bound as a raw string, on purpose (Plan-Entscheidung 6): Bind() runs before host.Build(),
        // outside any exit-code handling, so a DateOnly? property would throw here on a typo.
        Assert.Equal("2026-09-01", options.SharedChatCutover);
    }

    [Fact]
    public void HarnessOptions_AnUnparsableSharedChatCutover_DoesNotThrowWhileBinding()
    {
        // The other half of Plan-Entscheidung 6: a value that will later fail HarnessRunner's
        // strict yyyy-MM-dd parse must still bind cleanly here — the refusal belongs in
        // ExecuteAsync (Exit 3, German line), never as an exception out of AddHarness/Bind.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=x;Username=u;Password=p",
            ["Redis:ConnectionString"] = "localhost:6379",
            ["Harness:SharedChatCutover"] = "not-a-date"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddHarness(configuration);
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<HarnessOptions>();

        Assert.Equal("not-a-date", options.SharedChatCutover);
    }

    private static ServiceProvider BuildProvider()
    {
        var configuration = Configuration();
        var services = new ServiceCollection();
        services.AddLogging();
        // The host supplies this; a bare ServiceCollection does not, and BotChatterDetector takes it.
        services.AddSingleton(configuration);
        services.AddHarness(configuration);
        // Overrides the dialing factory registration of AddEmotePurgeInfrastructure; the last
        // registration wins for a single-service resolve.
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // AddEmotePurgeInfrastructure throws without these two; nothing is dialed here.
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=x;Username=u;Password=p",
            ["Redis:ConnectionString"] = "localhost:6379"
        }).Build();
}
