using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class SystemLoadSamplerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset StartAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ContinuesSamplingAfterSnapshotAndPersistenceFailures()
    {
        var snapshotSource = new FlakySnapshotSource();
        var samplingService = new FlakySamplingService();
        var clock = new FakeTimeProvider(StartAt);
        var timeProvider = new TimerObservingTimeProvider(clock);
        var logger = new RecordingLogger<SystemLoadSampler>();
        var services = new ServiceCollection();
        services.AddSingleton<ISystemLoadWorkloadSource, EmptyWorkloadSource>();
        services.AddSingleton<ISystemLoadSamplingService>(samplingService);
        await using var provider = services.BuildServiceProvider();
        using var sampler = new SystemLoadSampler(
            SystemLoadSource.Api,
            provider.GetRequiredService<IServiceScopeFactory>(),
            snapshotSource,
            new SystemLoadOptions { IntervalSeconds = 1 },
            timeProvider,
            logger);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            await timeProvider.TimerCreated.Task.WaitAsync(TestTimeout);
            clock.Advance(TimeSpan.FromSeconds(1));
            await snapshotSource.FirstRead.Task.WaitAsync(TestTimeout);
            clock.Advance(TimeSpan.FromSeconds(1));
            await snapshotSource.SecondRead.Task.WaitAsync(TestTimeout);
            clock.Advance(TimeSpan.FromSeconds(1));
            await samplingService.Saved.Task.WaitAsync(TestTimeout);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }

        Assert.True(snapshotSource.ReadCount >= 3);
        Assert.Equal(2, samplingService.AttemptCount);
        Assert.Single(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task InvalidIntervalIsRejectedBeforeTimerConstruction()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISystemLoadWorkloadSource, EmptyWorkloadSource>();
        services.AddSingleton<ISystemLoadSamplingService, FlakySamplingService>();
        await using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger<SystemLoadSampler>();
        var timeProvider = new TimerObservingTimeProvider(new FakeTimeProvider(StartAt));
        using var sampler = new SystemLoadSampler(
            SystemLoadSource.Api,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FlakySnapshotSource(),
            new SystemLoadOptions { IntervalSeconds = SystemLoadOptions.MaximumIntervalSeconds + 1 },
            timeProvider,
            logger);

        await sampler.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sampler.ExecuteTask!);

        Assert.Equal(0, timeProvider.CreateCount);
        Assert.False(timeProvider.TimerCreated.Task.IsCompleted);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task DisabledSamplerDoesNotCreateATimerOrReadASnapshot()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISystemLoadWorkloadSource, EmptyWorkloadSource>();
        services.AddSingleton<ISystemLoadSamplingService, FlakySamplingService>();
        await using var provider = services.BuildServiceProvider();
        var snapshotSource = new FlakySnapshotSource();
        var logger = new RecordingLogger<SystemLoadSampler>();
        var timeProvider = new TimerObservingTimeProvider(new FakeTimeProvider(StartAt));
        using var sampler = new SystemLoadSampler(
            SystemLoadSource.Api,
            provider.GetRequiredService<IServiceScopeFactory>(),
            snapshotSource,
            new SystemLoadOptions { Enabled = false },
            timeProvider,
            logger);

        await sampler.StartAsync(CancellationToken.None);
        await sampler.ExecuteTask!.WaitAsync(TestTimeout);
        await sampler.StopAsync(CancellationToken.None);

        Assert.Equal(0, timeProvider.CreateCount);
        Assert.Equal(0, snapshotSource.ReadCount);
        Assert.Single(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Information);
    }

    [Fact]
    public async Task RepeatedFailuresAreLoggedAgainAfterTheFiveMinuteThrottleWindow()
    {
        var snapshotSource = new RepeatedlyFailingSnapshotSource();
        var fakeClock = new FakeTimeProvider(StartAt);
        var timeProvider = new TimerObservingTimeProvider(fakeClock);
        var logger = new WarningSignalingLogger<SystemLoadSampler>();
        var services = new ServiceCollection();
        services.AddSingleton<ISystemLoadWorkloadSource, EmptyWorkloadSource>();
        services.AddSingleton<ISystemLoadSamplingService, FlakySamplingService>();
        await using var provider = services.BuildServiceProvider();
        using var sampler = new SystemLoadSampler(
            SystemLoadSource.Api,
            provider.GetRequiredService<IServiceScopeFactory>(),
            snapshotSource,
            new SystemLoadOptions { IntervalSeconds = 1 },
            timeProvider,
            logger);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            await timeProvider.TimerCreated.Task.WaitAsync(TestTimeout);
            fakeClock.Advance(TimeSpan.FromSeconds(1));
            await snapshotSource.WaitForAttemptAsync(1).WaitAsync(TestTimeout);

            fakeClock.Advance(TimeSpan.FromSeconds(1));
            await snapshotSource.WaitForAttemptAsync(2).WaitAsync(TestTimeout);
            Assert.Equal(1, logger.WarningCount);

            fakeClock.Advance(TimeSpan.FromMinutes(5));
            await snapshotSource.WaitForAttemptAsync(3).WaitAsync(TestTimeout);
            await logger.SecondWarningLogged.Task.WaitAsync(TestTimeout);
            Assert.Equal(2, logger.WarningCount);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FlakySnapshotSource : ISystemLoadSnapshotSource
    {
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCount => Volatile.Read(ref _readCount);

        private int _readCount;

        public Task<SystemLoadSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            var read = Interlocked.Increment(ref _readCount);
            if (read == 1)
            {
                FirstRead.TrySetResult();
                throw new InvalidOperationException("Injected snapshot failure.");
            }

            if (read == 2)
            {
                SecondRead.TrySetResult();
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

    private sealed class RepeatedlyFailingSnapshotSource : ISystemLoadSnapshotSource
    {
        private readonly TaskCompletionSource[] _attempts =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
        ];

        private int _readCount;

        public Task WaitForAttemptAsync(int attempt) => _attempts[attempt - 1].Task;

        public Task<SystemLoadSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _readCount);
            if (attempt <= _attempts.Length)
            {
                _attempts[attempt - 1].TrySetResult();
            }

            return Task.FromException<SystemLoadSnapshot>(new InvalidOperationException("Injected snapshot failure."));
        }
    }

    private sealed class WarningSignalingLogger<T> : ILogger<T>
    {
        private int _warningCount;

        public int WarningCount => Volatile.Read(ref _warningCount);

        public TaskCompletionSource SecondWarningLogged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && Interlocked.Increment(ref _warningCount) == 2)
            {
                SecondWarningLogged.TrySetResult();
            }
        }
    }

    private sealed class TimerObservingTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private int _createCount;

        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreateCount => Volatile.Read(ref _createCount);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _createCount);
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            TimerCreated.TrySetResult();
            return timer;
        }
    }
}
