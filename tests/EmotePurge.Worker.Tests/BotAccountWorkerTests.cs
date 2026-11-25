using EmotePurge.Core.Services;
using EmotePurge.Worker.BotAccount;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Container-free: the worker only owns the switches, the cadence and the failure handling (plan #487 2.5, AK96). The
// runner is a substitute; what it does is tested against Postgres in Infrastructure.Tests.
public sealed class BotAccountWorkerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset StartAt = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    // AK96: one line, no scope — so no runner, and the token is never opened.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Disabled_LogsOneLineEndsAndNeverOpensAScope(bool botAccountEnabled, bool wishesEnabled)
    {
        var clock = new FakeTimeProvider(StartAt);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var logger = new RecordingLogger();
        var worker = new BotAccountWorker(
            logger,
            scopeFactory,
            new BotAccountOptions { Enabled = botAccountEnabled },
            new WishOptions { Enabled = wishesEnabled },
            new BotAccountSignal(),
            clock);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TestTimeout);
        clock.Advance(TimeSpan.FromMinutes(5));
        await worker.StopAsync(CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        scopeFactory.DidNotReceiveWithAnyArgs().CreateScope();
    }

    [Fact]
    public async Task Enabled_RunsAPassAtOnceThenEveryThirtySeconds_WithOneRunner()
    {
        var clock = new FakeTimeProvider(StartAt);
        var (worker, provider, runner, calls, observed, logger) = Create(clock, _ => BotAccountPassResult.Completed);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForCallsAsync(calls, 1);
            await WaitUntilAsync(() => observed.TimerCount >= 1, () => "The cadence wait was never armed.");

            clock.Advance(BotAccountWorker.PassInterval - TimeSpan.FromSeconds(1));
            await Task.Delay(50);
            Assert.Equal(1, calls.Value);

            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitForCallsAsync(calls, 2);
            Assert.Empty(logger.Entries);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // The loop's scope went with the stop, and with it the runner and its lock connection.
        await runner.Received(1).DisposeAsync();
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task ANudge_StartsTheNextPassBeforeTheCadence()
    {
        var clock = new FakeTimeProvider(StartAt);
        var (worker, provider, _, calls, observed, _) = Create(clock, _ => BotAccountPassResult.Completed);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForCallsAsync(calls, 1);
            await WaitUntilAsync(() => observed.TimerCount >= 1, () => "The cadence wait was never armed.");

            provider.GetRequiredService<BotAccountSignal>().Set();

            await WaitForCallsAsync(calls, 2);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task AThrowingPass_LogsTheTypeOnly_AndTheNextPassRuns()
    {
        var clock = new FakeTimeProvider(StartAt);
        var (worker, provider, _, calls, observed, logger) = Create(
            clock, n => n == 1 ? throw new InvalidOperationException("secret-row-data") : BotAccountPassResult.Completed);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForCallsAsync(calls, 1);
            await WaitUntilAsync(() => observed.TimerCount >= 1, () => "The cadence wait was never armed.");
            clock.Advance(BotAccountWorker.PassInterval);
            await WaitForCallsAsync(calls, 2);

            var warning = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Contains("InvalidOperationException", warning.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-row-data", warning.Message, StringComparison.Ordinal);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    // AK81 (lock part): a second worker without the lock says so once, not every 30 seconds, and again only after it held
    // the lock in between.
    [Fact]
    public async Task WithoutTheLock_WarnsOnce_UntilAPassRanUnderTheLock()
    {
        var clock = new FakeTimeProvider(StartAt);
        var (worker, provider, _, calls, observed, logger) = Create(
            clock, n => n == 3 ? BotAccountPassResult.Completed : BotAccountPassResult.NoLock);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            for (var pass = 1; pass <= 4; pass++)
            {
                await WaitForCallsAsync(calls, pass);
                await WaitUntilAsync(() => observed.TimerCount >= pass, () => $"The cadence wait {pass} was never armed.");
                clock.Advance(BotAccountWorker.PassInterval);
            }

            await WaitForCallsAsync(calls, 5);
            Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("lock", StringComparison.Ordinal)));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    private static Task WaitForCallsAsync(CallCounter counter, int expected) =>
        WaitUntilAsync(() => counter.Value >= expected, () => $"Expected {expected} passes, saw {counter.Value}.");

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> failure)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure());
            await Task.Delay(5);
        }
    }

    private static (BotAccountWorker Worker, ServiceProvider Provider, IBotAccountRunner Runner, CallCounter Calls, TimerObservingTimeProvider Observed, RecordingLogger Logger) Create(
        FakeTimeProvider clock, Func<int, BotAccountPassResult> pass)
    {
        var calls = new CallCounter();
        var runner = Substitute.For<IBotAccountRunner>();
        runner.RunPassAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var n = calls.Increment();
            try
            {
                return Task.FromResult(pass(n));
            }
            catch (Exception ex)
            {
                return Task.FromException<BotAccountPassResult>(ex);
            }
        });

        var services = new ServiceCollection();
        services.AddScoped(_ => runner);
        services.AddSingleton<BotAccountSignal>();
        var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger();
        var observed = new TimerObservingTimeProvider(clock);
        var worker = new BotAccountWorker(
            logger,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BotAccountOptions { Enabled = true },
            new WishOptions { Enabled = true },
            provider.GetRequiredService<BotAccountSignal>(),
            observed);
        return (worker, provider, runner, calls, observed, logger);
    }

    private sealed class CallCounter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public int Increment() => Interlocked.Increment(ref _value);
    }

    private sealed class RecordingLogger : ILogger<BotAccountWorker>
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

    // Counts the timers the worker arms (one per cadence wait), so a test moves the clock only once the wait exists.
    private sealed class TimerObservingTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private int _timerCount;

        public int TimerCount => Volatile.Read(ref _timerCount);

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timerCount);
            return timer;
        }
    }
}
