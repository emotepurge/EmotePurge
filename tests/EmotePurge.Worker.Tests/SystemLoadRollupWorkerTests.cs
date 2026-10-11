using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Worker.Tests;

public sealed class SystemLoadRollupWorkerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset StartAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecomputesImmediatelyAtStartupForCatchUp()
    {
        var clock = new FakeTimeProvider(StartAt);
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rollup = Substitute.For<ISystemLoadRollupService>();
        rollup.RecomputeClosableHoursAsync(
                Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                called.TrySetResult();
                return Task.FromResult(4);
            });
        var (worker, provider, _) = CreateWorker(clock, rollup);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await called.Task.WaitAsync(TestTimeout);
            await rollup.Received(1).RecomputeClosableHoursAsync(
                StartAt.UtcDateTime, TimeSpan.FromMinutes(5), 30, Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task LogsAFailedRunAndRetriesOnTheNextHourlyTick()
    {
        var clock = new FakeTimeProvider(StartAt);
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warningLogged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rollup = Substitute.For<ISystemLoadRollupService>();
        var callCount = 0;
        rollup.RecomputeClosableHoursAsync(
                Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstAttempt.TrySetResult();
                    return Task.FromException<int>(new InvalidOperationException("database unavailable"));
                }

                secondAttempt.TrySetResult();
                return Task.FromResult(1);
            });
        var logger = new RecordingLogger<SystemLoadRollupWorker>(warningLogged);
        var (worker, provider, _) = CreateWorker(clock, rollup, logger);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await firstAttempt.Task.WaitAsync(TestTimeout);
            await warningLogged.Task.WaitAsync(TestTimeout);
            await Task.Delay(20);
            clock.Advance(TimeSpan.FromHours(1));

            await secondAttempt.Task.WaitAsync(TestTimeout);

            Assert.Equal(2, callCount);
            Assert.Contains(logger.Entries, entry =>
                entry.Level == LogLevel.Warning && entry.Message.Contains("next hourly run will retry", StringComparison.Ordinal));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancellationDuringRunStopsTheLoopWithoutLoggingAFailure()
    {
        var clock = new FakeTimeProvider(StartAt);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rollup = Substitute.For<ISystemLoadRollupService>();
        rollup.RecomputeClosableHoursAsync(
                Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var cancellationToken = call.ArgAt<CancellationToken>(3);
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            });
        var logger = new RecordingLogger<SystemLoadRollupWorker>();
        var (worker, provider, _) = CreateWorker(clock, rollup, logger);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TestTimeout);

            await worker.StopAsync(CancellationToken.None);

            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
            await rollup.Received(1).RecomputeClosableHoursAsync(
                StartAt.UtcDateTime, TimeSpan.FromMinutes(5), 30, Arg.Any<CancellationToken>());
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    private static (SystemLoadRollupWorker Worker, ServiceProvider Provider, RecordingLogger<SystemLoadRollupWorker> Logger)
        CreateWorker(
            FakeTimeProvider clock,
            ISystemLoadRollupService rollup,
            RecordingLogger<SystemLoadRollupWorker>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(rollup);
        var provider = services.BuildServiceProvider();
        logger ??= new RecordingLogger<SystemLoadRollupWorker>();
        return (
            new SystemLoadRollupWorker(
                logger,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new SystemLoadOptions(),
                clock),
            provider,
            logger);
    }

    private sealed class RecordingLogger<T>(TaskCompletionSource? onWarning = null) : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_entries)
            {
                _entries.Add((logLevel, message));
            }

            if (logLevel == LogLevel.Warning)
            {
                onWarning?.TrySetResult();
            }
        }
    }
}
