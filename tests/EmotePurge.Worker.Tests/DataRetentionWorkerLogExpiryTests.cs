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
    private static readonly TimeSpan LogExpiryFirstRunDelay = TimeSpan.FromMinutes(1);
    private static readonly DateTimeOffset StartAt = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LogLoop_RunsWhileBootGateNeverCompletes()
    {
        var clock = new FakeTimeProvider(StartAt);
        var retention = RetentionWithSignals(out var logCalls, out _);
        var (worker, provider, observed) = CreateWorker(clock, retention, new BootRecoveryGate());

        await StartAsync(worker, observed);
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
        var (worker, provider, observed) = CreateWorker(clock, retention, gate, logger);

        await StartAsync(worker, observed);
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
        var (worker, provider, observed) = CreateWorker(clock, retention, gate);

        await StartAsync(worker, observed);
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
        var (worker, provider, observed) = CreateWorker(clock, retention, new BootRecoveryGate(), logger, enforce: false);

        await StartAsync(worker, observed);
        try
        {
            clock.Advance(TimeSpan.FromMinutes(1)); // pass 1 at 22:31
            await WaitForCallsAsync(logCalls, 1);
            clock.Advance(TimeSpan.FromHours(1)); // pass 2 at 23:31, same UTC day
            await WaitForCallsAsync(logCalls, 2);
            clock.Advance(TimeSpan.FromHours(1)); // pass 3 at 00:31, next UTC day
            await WaitForCallsAsync(logCalls, 3);

            // The call counter moves inside ExpireLogsAsync, before the worker logs the pass's summary.
            // Stopping waits for ExecuteAsync, so every pass has logged once StopAsync returns.
            await worker.StopAsync(CancellationToken.None);

            // Each pass reports its own number as the would-delete count, so a warning names its pass.
            var warnings = logger.LogExpiryMessages(LogLevel.Warning);
            Assert.Equal(2, warnings.Count);
            Assert.Contains(warnings, m => m.Contains("Would delete 1 log entries", StringComparison.Ordinal));
            Assert.Contains(warnings, m => m.Contains("Would delete 3 log entries", StringComparison.Ordinal));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    // BackgroundService.StartAsync returns before ExecuteAsync has run (since .NET 10 it starts on the
    // thread pool), so the log loop's first-run delay may not exist yet. Advancing the fake clock first
    // moves "now" past a timer that is then armed relative to the new "now" and never fires; a fixed
    // real-time pause was not enough on a loaded CI runner. Wait for that very timer instead.
    private static async Task StartAsync(DataRetentionWorker worker, TimerObservingTimeProvider observed)
    {
        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => observed.HasTimer(LogExpiryFirstRunDelay),
            () => $"The log loop never armed its {LogExpiryFirstRunDelay} first-run delay.");
    }

    private static Task WaitForCallsAsync(CallCounter counter, int expected) =>
        WaitUntilAsync(() => counter.Value >= expected, () => $"Expected {expected} calls, saw {counter.Value}.");

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> failure)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure());
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
                // Read the clock before signalling: once the counter moves, the test may advance it.
                var pass = log.Value + 1;
                var summary = new LogRetentionSummary(call.ArgAt<bool>(0), (clock?.GetUtcNow() ?? StartAt).UtcDateTime, pass, 0);
                log.Increment();
                return Task.FromResult(summary);
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

    private static (DataRetentionWorker Worker, ServiceProvider Provider, TimerObservingTimeProvider Observed) CreateWorker(
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
        var observed = new TimerObservingTimeProvider(clock);
        return (
            new DataRetentionWorker(
                logger,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new RetentionOptions { Enforce = enforce, StartupDelayMinutes = 0 },
                gate,
                observed),
            provider,
            observed);
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

        public IReadOnlyList<string> LogExpiryMessages(LogLevel level) =>
            [.. Entries
                .Where(e => e.Level == level && e.Message.Contains("log expiry", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Message)];

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

    // Delegates to the FakeTimeProvider the test advances and records the due time of every timer the
    // worker arms, so a test can wait for a timer to exist before it moves the clock.
    private sealed class TimerObservingTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private readonly List<TimeSpan> _dueTimes = [];

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            lock (_dueTimes)
            {
                _dueTimes.Add(dueTime);
            }

            return timer;
        }

        public bool HasTimer(TimeSpan dueTime)
        {
            lock (_dueTimes)
            {
                return _dueTimes.Contains(dueTime);
            }
        }
    }
}
