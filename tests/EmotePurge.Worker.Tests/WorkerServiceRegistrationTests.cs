using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Worker.ChatLogBackfill;
using EmotePurge.Worker.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
    public void AddWorkerHostedServices_RegistersExactlyTheThirteenOfTheWorker()
    {
        var services = new ServiceCollection();
        services.AddWorkerCore(Configuration());
        services.AddWorkerHostedServices();

        Assert.Equal(13, services.Count(d => d.ServiceType == typeof(IHostedService)));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ChatLogBackfillWorker));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SystemLoadRollupWorker));
    }

    // #350: the backfill loop and its wake-up resolve with the worker's own graph, and Worker and the
    // loop share one signal instance — two would leave every BACKFILL: nudge unheard.
    [Fact]
    public void AddWorkerHostedServices_ResolvesTheBackfillWorker_AndOneSharedSignal()
    {
        var configuration = Configuration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddWorkerCore(configuration);
        services.AddWorkerHostedServices();
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());

        using var provider = services.BuildServiceProvider();

        Assert.Contains(provider.GetServices<IHostedService>(), s => s is ChatLogBackfillWorker);
        Assert.Same(provider.GetRequiredService<ChatLogBackfillSignal>(), provider.GetRequiredService<ChatLogBackfillSignal>());
        Assert.False(provider.GetRequiredService<ChatLogBackfillOptions>().Enabled);
    }

    [Fact]
    public void AddWorkerCore_AloneRegistersNoHostedService()
    {
        var services = new ServiceCollection();
        services.AddWorkerCore(Configuration());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public async Task AddWorkerCore_WorkloadSource_PreservesCounterDeltasAcrossScopes()
    {
        var configuration = Configuration();
        var services = new ServiceCollection();
        services.AddWorkerCore(configuration);

        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));
        services.AddSingleton(channelService);

        var liveStatusReader = Substitute.For<ITwitchLiveStatusReader>();
        liveStatusReader.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TwitchLiveStatusSnapshot?>(null));
        services.AddSingleton(liveStatusReader);

        using var provider = services.BuildServiceProvider();
        var stats = provider.GetRequiredService<WorkerStats>();
        var usageCounter = provider.GetRequiredService<IEmoteUsageCounter>();

        stats.RecordChatLine();
        stats.RecordChatLine();
        usageCounter.Increment("one", "set", UsageCategory.Human);
        usageCounter.Increment("two", "set", UsageCategory.Bot);
        var first = await ReadInScopeAsync(provider);

        usageCounter.DrainAndReset();
        stats.RecordChatLine();
        usageCounter.Increment("three", "set", UsageCategory.SharedChat);
        var second = await ReadInScopeAsync(provider);

        usageCounter.DrainAndReset();
        var third = await ReadInScopeAsync(provider);

        Assert.Equal(2, first.ChatLines);
        Assert.Equal(2, first.EmoteMatches);
        Assert.Equal(1, second.ChatLines);
        Assert.Equal(1, second.EmoteMatches);
        Assert.Equal(0, third.ChatLines);
        Assert.Equal(0, third.EmoteMatches);
    }

    [Fact]
    public async Task Sampler_ConfirmsWorkerCounterBaselinesOnlyAfterSuccessfulRecord()
    {
        var configuration = Configuration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkerCore(configuration);

        var channelService = Substitute.For<IChannelService>();
        channelService.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));
        services.AddSingleton(channelService);

        var liveStatusReader = Substitute.For<ITwitchLiveStatusReader>();
        liveStatusReader.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TwitchLiveStatusSnapshot?>(null));
        services.AddSingleton(liveStatusReader);

        var snapshotSource = new EmptySystemLoadSnapshotSource();
        var samplingService = new FailingSecondSystemLoadSamplingService();
        services.AddSingleton<ISystemLoadSnapshotSource>(snapshotSource);
        services.AddSingleton<ISystemLoadSamplingService>(samplingService);

        await using var provider = services.BuildServiceProvider();
        var stats = provider.GetRequiredService<WorkerStats>();
        for (var index = 0; index < 100; index++)
        {
            stats.RecordChatLine();
        }

        samplingService.OnRecord = attempt =>
        {
            if (attempt is 1 or 2)
            {
                for (var index = 0; index < 30; index++)
                {
                    stats.RecordChatLine();
                }
            }
        };

        using var sampler = new SystemLoadSampler(
            SystemLoadSource.Worker,
            provider.GetRequiredService<IServiceScopeFactory>(),
            snapshotSource,
            new SystemLoadOptions { IntervalSeconds = 1 },
            TimeProvider.System,
            NullLogger<SystemLoadSampler>.Instance);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            await samplingService.TwoSamplesSaved.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }

        Assert.Equal(3, samplingService.AttemptCount);
        Assert.Equal([100L, 60L], samplingService.SavedChatLineDeltas);
    }

    private static async Task<SystemLoadWorkloadSnapshot> ReadInScopeAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<ISystemLoadWorkloadSource>();
        var workload = await source.ReadAsync();
        source.ConfirmRecorded(workload);
        return workload;
    }

    private sealed class EmptySystemLoadSnapshotSource : ISystemLoadSnapshotSource
    {
        public Task<SystemLoadSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemLoadSnapshot(
                DateTime.UtcNow,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null));
    }

    private sealed class FailingSecondSystemLoadSamplingService : ISystemLoadSamplingService
    {
        private readonly List<long?> _savedChatLineDeltas = [];
        private int _attemptCount;

        public TaskCompletionSource TwoSamplesSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public IReadOnlyList<long?> SavedChatLineDeltas => _savedChatLineDeltas;

        public Action<int>? OnRecord { get; set; }

        public Task RecordAsync(
            SystemLoadSource source,
            Guid processInstanceId,
            SystemLoadSnapshot snapshot,
            SystemLoadWorkloadSnapshot workload,
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _attemptCount);
            OnRecord?.Invoke(attempt);
            if (attempt == 2)
            {
                throw new InvalidOperationException("Injected persistence failure.");
            }

            _savedChatLineDeltas.Add(workload.ChatLines);
            if (_savedChatLineDeltas.Count == 2)
            {
                TwoSamplesSaved.TrySetResult();
            }

            return Task.CompletedTask;
        }
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
