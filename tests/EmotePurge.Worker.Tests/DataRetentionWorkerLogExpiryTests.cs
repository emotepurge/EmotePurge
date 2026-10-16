using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The hourly log-expiry loop of DataRetentionWorker: it must run without the boot gate and must not
// share a failure with the daily pass.
public sealed class DataRetentionWorkerLogExpiryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset StartAt = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LogLoop_RunsWhileBootGateNeverCompletes()
    {
        var clock = new FakeTimeProvider(StartAt);
        var retention = RetentionWithSignals(out var logCalls, out _);
        var (worker, provider, _) = CreateWorker(clock, retention, new BootRecoveryGate());

        await StartAsync(worker);
        try
        {
            await retention.DidNotReceive().ExpireLogsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());

            clock.Advance(TimeSpan.FromMinutes(1));
            await WaitForCallsAsync(logCalls, 1);
            clock.Advance(TimeSpan.FromHours(1));
            await WaitForCallsAsync(logCalls, 2);

            await retention.DidNotReceive().RunAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task LogLoop_FailureDoesNotStopNextPassOrDailyPass()
    {
        var clock = new FakeTimeProvider(StartAt);
        var retention = RetentionWithSignals(out var logCalls, out var dailyCalls);
        var failFirst = true;
        retention.ExpireLogsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                logCalls.Increment();
                if (failFirst)
                {
                    failFirst = false;
                    return Task.FromException<LogRetentionSummary>(new InvalidOperationException("secret-key-value"));
                }

                return Task.FromResult(new LogRetentionSummary(true, StartAt.UtcDateTime, 0, 0));
            });
        var gate = new BootRecoveryGate();
        var logger = new RecordingLogger();
        var (worker, provider, _) = CreateWorker(clock, retention, gate, logger);

        await StartAsync(worker);
        try
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await WaitForCallsAsync(logCalls, 1);
            gate.MarkCompleted();
            await WaitForCallsAsync(dailyCalls, 1);
            clock.Advance(TimeSpan.FromHours(1));
            await WaitForCallsAsync(logCalls, 2);

            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("InvalidOperationException", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("secret-key-value", StringComparison.Ordinal));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task DailyPass_FailureDoesNotStopLogLoop()
    {
        var clock = new FakeTimeProvider(StartAt);
        var retention = RetentionWithSignals(out var logCalls, out var dailyCalls);
        retention.RunAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                dailyCalls.Increment();
                return Task.FromException<RetentionRunSummary>(new InvalidOperationException("boom"));
            });
        var gate = new BootRecoveryGate();
        gate.MarkCompleted();
        var (worker, provider, _) = CreateWorker(clock, retention, gate);

        await StartAsync(worker);
        try
        {
            await WaitForCallsAsync(dailyCalls, 1);
            clock.Advance(TimeSpan.FromMinutes(1));
            await WaitForCallsAsync(logCalls, 1);
            clock.Advance(TimeSpan.FromHours(1));
            await WaitForCallsAsync(logCalls, 2);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task LogLoop_DryRun_WarnsAtMostOncePerUtcDay()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 22, 30, 0, TimeSpan.Zero));
        var retention = RetentionWithSignals(out var logCalls, out _, clock);
        var logger = new RecordingLogger();
        var (worker, provider, _) = CreateWorker(clock, retention, new BootRecoveryGate(), logger, enforce: false);

        await StartAsync(worker);
        try
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await WaitForCallsAsync(logCalls, 1);
            clock.Advance(TimeSpan.FromHours(1)); // 23:31, same UTC day
            await WaitForCallsAsync(logCalls, 2);
            Assert.Equal(1, logger.Count(LogLevel.Warning));

            clock.Advance(TimeSpan.FromHours(1)); // 00:31 next day
            await WaitForCallsAsync(logCalls, 3);
            Assert.Equal(2, logger.Count(LogLevel.Warning));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    // BackgroundService.StartAsync may return before ExecuteAsync has armed its first timer; advancing
    // the fake clock earlier would miss it (same precedent as SystemLoadRollupWorkerTests).
    private static async Task StartAsync(DataRetentionWorker worker)
    {
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(50);
    }

    private static async Task WaitForCallsAsync(CallCounter counter, int expected)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (counter.Value < expected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Expected {expected} calls, saw {counter.Value}.");
            await Task.Delay(5);
        }
    }

    private static IDataRetentionService RetentionWithSignals(out CallCounter logCalls, out CallCounter dailyCalls, TimeProvider? clock = null)
    {
        var log = new CallCounter();
        var daily = new CallCounter();
        var retention = Substitute.For<IDataRetentionService>();
        retention.ExpireLogsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                log.Increment();
                return Task.FromResult(new LogRetentionSummary(call.ArgAt<bool>(0), (clock?.GetUtcNow() ?? StartAt).UtcDateTime, 0, 0));
            });
        retention.RunAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                daily.Increment();
                return Task.FromResult<RetentionRunSummary>(null!);
            });
        logCalls = log;
        dailyCalls = daily;
        return retention;
    }

    private static (DataRetentionWorker Worker, ServiceProvider Provider, RecordingLogger Logger) CreateWorker(
        FakeTimeProvider clock,
        IDataRetentionService retention,
        BootRecoveryGate gate,
        RecordingLogger? logger = null,
        bool enforce = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(retention);
        var provider = services.BuildServiceProvider();
        logger ??= new RecordingLogger();
        return (
            new DataRetentionWorker(
                logger,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new RetentionOptions { Enforce = enforce, StartupDelayMinutes = 0 },
                gate,
                clock),
            provider,
            logger);
    }

    private sealed class CallCounter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public void Increment() => Interlocked.Increment(ref _value);
    }

    private sealed class RecordingLogger : ILogger<DataRetentionWorker>
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

        public int Count(LogLevel level) => Entries.Count(e => e.Level == level && e.Message.Contains("log", StringComparison.OrdinalIgnoreCase) && e.Message.Contains("expiry", StringComparison.OrdinalIgnoreCase));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
