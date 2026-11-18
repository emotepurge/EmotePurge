using EmotePurge.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Container-free: the worker only owns the switch, the timer and the failure handling.
public sealed class WishExpiryWorkerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly DateTimeOffset StartAt = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Disabled_LogsOneLineEndsAndNeverOpensAScope()
    {
        var clock = new FakeTimeProvider(StartAt);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var logger = new RecordingLogger();
        var worker = new WishExpiryWorker(logger, scopeFactory, new WishOptions { Enabled = false }, clock);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TestTimeout);
        clock.Advance(TimeSpan.FromHours(1));
        await worker.StopAsync(CancellationToken.None);

        Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, logger.Entries[0].Level);
        scopeFactory.DidNotReceiveWithAnyArgs().CreateScope();
    }

    [Fact]
    public async Task Enabled_RunsFirstAfterOneMinuteThenEveryInterval()
    {
        var clock = new FakeTimeProvider(StartAt);
        var service = ServiceReturning(out var calls, _ => WishExpiryRunResult.Empty);
        var (worker, provider, observed, logger) = Create(clock, service);

        await StartAsync(worker, observed);
        try
        {
            clock.Advance(FirstRunDelay - TimeSpan.FromSeconds(1));
            await Task.Delay(50);
            Assert.Equal(0, calls.Value);

            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitForCallsAsync(calls, 1);
            await WaitUntilAsync(() => observed.HasTimer(Interval), () => "The periodic timer was never armed.");

            clock.Advance(Interval);
            await WaitForCallsAsync(calls, 2);
            Assert.Empty(logger.Entries);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task ThrowingPass_LogsTypeOnlyAndNextTickRuns()
    {
        var clock = new FakeTimeProvider(StartAt);
        var service = ServiceReturning(out var calls, n =>
            n == 1 ? throw new InvalidOperationException("secret-row-data") : WishExpiryRunResult.Empty);
        var (worker, provider, observed, logger) = Create(clock, service);

        await StartAsync(worker, observed);
        try
        {
            clock.Advance(FirstRunDelay);
            await WaitForCallsAsync(calls, 1);
            await WaitUntilAsync(() => observed.HasTimer(Interval), () => "The periodic timer was never armed.");
            clock.Advance(Interval);
            await WaitForCallsAsync(calls, 2);

            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("InvalidOperationException", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("secret-row-data", StringComparison.Ordinal));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task Summary_ChangeLogsOneInformationLine_FailuresLogAWarning()
    {
        var clock = new FakeTimeProvider(StartAt);
        var changed = new WishExpiryRunResult(3, 0, new Dictionary<string, int> { ["expired"] = 2 }, 1, 0, 0, 0);
        var failed = new WishExpiryRunResult(1, 2, new Dictionary<string, int>(), 0, 0, 0, 1);
        var service = ServiceReturning(out var calls, n => n == 1 ? changed : failed);
        var (worker, provider, observed, logger) = Create(clock, service);

        await StartAsync(worker, observed);
        try
        {
            clock.Advance(FirstRunDelay);
            await WaitForCallsAsync(calls, 1);
            await WaitUntilAsync(() => observed.HasTimer(Interval), () => "The periodic timer was never armed.");
            clock.Advance(Interval);
            await WaitForCallsAsync(calls, 2);
            await worker.StopAsync(CancellationToken.None);

            Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
            Assert.Contains("presented 2 and queued 1", logger.Entries.Single(e => e.Level == LogLevel.Information).Message, StringComparison.Ordinal);
            var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
            Assert.Contains("2 failed channel reconciles and 1 failed wishes", warning.Message, StringComparison.Ordinal);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task Stop_DuringTheFirstMinute_EndsWithoutAPassOrALine()
    {
        var clock = new FakeTimeProvider(StartAt);
        var service = ServiceReturning(out var calls, _ => WishExpiryRunResult.Empty);
        var (worker, provider, observed, logger) = Create(clock, service);

        await StartAsync(worker, observed);
        await worker.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();

        Assert.Equal(0, calls.Value);
        Assert.Empty(logger.Entries);
    }

    private static async Task StartAsync(WishExpiryWorker worker, TimerObservingTimeProvider observed)
    {
        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => observed.HasTimer(FirstRunDelay), () => "The worker never armed its first-run delay.");
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

    private static IWishExpiryService ServiceReturning(out CallCounter calls, Func<int, WishExpiryRunResult> result)
    {
        var counter = new CallCounter();
        var service = Substitute.For<IWishExpiryService>();
        service.RunAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var n = counter.Increment();
            try
            {
                return Task.FromResult(result(n));
            }
            catch (Exception ex)
            {
                return Task.FromException<WishExpiryRunResult>(ex);
            }
        });
        calls = counter;
        return service;
    }

    private static (WishExpiryWorker Worker, ServiceProvider Provider, TimerObservingTimeProvider Observed, RecordingLogger Logger) Create(
        FakeTimeProvider clock, IWishExpiryService service)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => service);
        var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger();
        var observed = new TimerObservingTimeProvider(clock);
        var worker = new WishExpiryWorker(
            logger,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new WishOptions { Enabled = true, ExpiryIntervalMinutes = (int)Interval.TotalMinutes },
            observed);
        return (worker, provider, observed, logger);
    }

    private sealed class CallCounter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public int Increment() => Interlocked.Increment(ref _value);
    }

    private sealed class RecordingLogger : ILogger<WishExpiryWorker>
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

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    // Records the due time of every timer the worker arms, so a test can wait for a timer to exist
    // before it moves the clock.
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
