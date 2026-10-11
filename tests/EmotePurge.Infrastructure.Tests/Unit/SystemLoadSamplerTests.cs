using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class SystemLoadSamplerTests
{
    [Fact]
    public async Task ContinuesSamplingAfterSnapshotAndPersistenceFailures()
    {
        var snapshotSource = new FlakySnapshotSource();
        var samplingService = new FlakySamplingService();
        var services = new ServiceCollection();
        services.AddSingleton<ISystemLoadWorkloadSource, EmptyWorkloadSource>();
        services.AddSingleton<ISystemLoadSamplingService>(samplingService);
        await using var provider = services.BuildServiceProvider();
        using var sampler = new SystemLoadSampler(
            SystemLoadSource.Api,
            provider.GetRequiredService<IServiceScopeFactory>(),
            snapshotSource,
            new SystemLoadOptions { IntervalSeconds = 1 },
            TimeProvider.System,
            NullLogger<SystemLoadSampler>.Instance);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            await samplingService.Saved.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }

        Assert.True(snapshotSource.ReadCount >= 3);
        Assert.Equal(2, samplingService.AttemptCount);
    }

    private sealed class FlakySnapshotSource : ISystemLoadSnapshotSource
    {
        public int ReadCount => Volatile.Read(ref _readCount);

        private int _readCount;

        public Task<SystemLoadSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                throw new InvalidOperationException("Injected snapshot failure.");
            }

            return Task.FromResult(new SystemLoadSnapshot(
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
    }

    private sealed class EmptyWorkloadSource : ISystemLoadWorkloadSource
    {
        public Task<SystemLoadWorkloadSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemLoadWorkloadSnapshot(null, null, null, null));

        public void ConfirmRecorded(SystemLoadWorkloadSnapshot workload)
        {
        }
    }

    private sealed class FlakySamplingService : ISystemLoadSamplingService
    {
        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AttemptCount => Volatile.Read(ref _attemptCount);

        private int _attemptCount;

        public Task RecordAsync(
            SystemLoadSource source,
            Guid processInstanceId,
            SystemLoadSnapshot snapshot,
            SystemLoadWorkloadSnapshot workload,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attemptCount) == 1)
            {
                throw new InvalidOperationException("Injected database failure.");
            }

            Saved.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
