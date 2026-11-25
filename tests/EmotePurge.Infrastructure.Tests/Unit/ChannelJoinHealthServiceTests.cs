using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class ChannelJoinHealthServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly IWorkerRosterReader _reader = Substitute.For<IWorkerRosterReader>();
    private readonly IChannelService _channels = Substitute.For<IChannelService>();
    private readonly RecordingLogger<ChannelJoinHealthService> _log = new();

    private ChannelJoinHealthService NewService()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _channels);
        return new ChannelJoinHealthService(
            _reader,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ChannelJoinHealthOptions(),
            _time,
            _log,
            _time.GetUtcNow().UtcDateTime.AddHours(-1));
    }

    private WorkerRosterSnapshot Roster(params WorkerRosterChannel[] rows) =>
        new(_time.GetUtcNow().UtcDateTime, "w", _time.GetUtcNow().UtcDateTime.AddHours(-5), true, false, rows, WorkerRosterSnapshot.CurrentSchemaVersion);

    private static WorkerRosterChannel Unconfirmed(string name, int seconds, string reason) =>
        new(name, false, null, null, false, null, false, true, seconds, reason);

    private void StubDesired(params ActiveChannelActivation[] active) =>
        _channels.ListActiveChannelActivationsAsync(Arg.Any<CancellationToken>()).Returns(active);

    private int Warnings => _log.Entries.Count(e => e.Level == LogLevel.Warning);

    [Fact]
    public async Task Evaluate_ReadsTheDesiredListAndJudgesTheRoster()
    {
        StubDesired(new ActiveChannelActivation("late", _time.GetUtcNow().UtcDateTime.AddSeconds(-421)));

        var report = await NewService().EvaluateAsync(Roster());

        Assert.Equal(ChannelJoinHealthStatus.Failing, report.Status);
        Assert.Equal("late", Assert.Single(report.MissingIntent).ChannelName);
    }

    [Fact]
    public async Task Evaluate_WithoutASnapshot_DoesNotTouchTheDatabase()
    {
        var report = await NewService().EvaluateAsync(null);

        Assert.Equal(ChannelJoinHealthStatus.Unavailable, report.Status);
        await _channels.DidNotReceive().ListActiveChannelActivationsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Evaluate_WhenTheDesiredListThrows_IsUnavailableAndWarnsOnce()
    {
        _channels.ListActiveChannelActivationsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<ActiveChannelActivation>>(_ => throw new InvalidOperationException("db down"));

        var report = await NewService().EvaluateAsync(Roster());

        Assert.Equal(ChannelJoinHealthStatus.Unavailable, report.Status);
        Assert.Equal(1, Warnings);
    }

    [Fact]
    public async Task Evaluate_WhenTheCallerCancels_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _channels.ListActiveChannelActivationsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ActiveChannelActivation>>(c => throw new OperationCanceledException(c.Arg<CancellationToken>()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewService().EvaluateAsync(Roster(), cts.Token));
    }

    [Fact]
    public async Task IsAlarming_ForAHealthyRoster_IsFalse()
    {
        _reader.ReadStrictAsync(Arg.Any<CancellationToken>()).Returns(Roster());
        StubDesired();

        Assert.False(await NewService().IsAlarmingAsync());
    }

    [Fact]
    public async Task IsAlarming_ForAnOverdueChannel_IsTrue()
    {
        _reader.ReadStrictAsync(Arg.Any<CancellationToken>()).Returns(Roster(Unconfirmed("a", 999, TwitchJoinFailureReasons.NoConfirmation)));
        StubDesired();

        Assert.True(await NewService().IsAlarmingAsync());
    }

    [Fact]
    public async Task IsAlarming_ForAColdStart_IsFalse()
    {
        _reader.ReadStrictAsync(Arg.Any<CancellationToken>()).Returns((WorkerRosterSnapshot?)null);
        var services = new ServiceCollection().BuildServiceProvider();
        var service = new ChannelJoinHealthService(
            _reader, services.GetRequiredService<IServiceScopeFactory>(), new ChannelJoinHealthOptions(), _time, _log,
            _time.GetUtcNow().UtcDateTime.AddSeconds(-10));

        Assert.False(await service.IsAlarmingAsync());
    }

    [Fact]
    public async Task IsAlarming_ForAMissingRosterAfterTheColdStartWindow_IsTrue()
    {
        _reader.ReadStrictAsync(Arg.Any<CancellationToken>()).Returns((WorkerRosterSnapshot?)null);

        Assert.True(await NewService().IsAlarmingAsync());
    }

    // Codex review round 2: through the real roster reader, a Redis read failure must stay a failed
    // fill (503) even inside the Api's cold-start window; only a genuinely missing key is "starting".
    [Fact]
    public async Task IsAlarming_WhenTheRedisReadFailsDuringTheColdStart_IsTrue()
    {
        var database = Substitute.For<IDatabase>();
        database.StringGetAsync(Arg.Any<RedisKey>()).Returns<RedisValue>(_ =>
            throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "redis down", null, CommandStatus.Unknown));

        Assert.True(await ColdStartServiceOver(database).IsAlarmingAsync());
        Assert.Equal(1, Warnings);
    }

    [Fact]
    public async Task IsAlarming_WhenTheRosterKeyIsMissingDuringTheColdStart_IsFalse()
    {
        var database = Substitute.For<IDatabase>();
        database.StringGetAsync(Arg.Any<RedisKey>()).Returns(RedisValue.Null);

        Assert.False(await ColdStartServiceOver(database).IsAlarmingAsync());
        Assert.Equal(0, Warnings);
    }

    [Fact]
    public async Task IsAlarming_WithinFiveSeconds_FillsOnceAndThenRefills()
    {
        _reader.ReadStrictAsync(Arg.Any<CancellationToken>()).Returns(Roster());
        StubDesired();
        var service = NewService();

        await service.IsAlarmingAsync();
        _time.Advance(TimeSpan.FromSeconds(4));
        await service.IsAlarmingAsync();
        await _reader.Received(1).ReadStrictAsync(Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromSeconds(2));
        await service.IsAlarmingAsync();
        await _reader.Received(2).ReadStrictAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsAlarming_ConcurrentCallers_ShareOneFill()
    {
        var gate = new TaskCompletionSource<WorkerRosterSnapshot?>();
        _reader.ReadStrictAsync(Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        StubDesired();
        var service = NewService();

        var first = service.IsAlarmingAsync();
        var second = service.IsAlarmingAsync();
        gate.SetResult(Roster());
        await Task.WhenAll(first, second);

        await _reader.Received(1).ReadStrictAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsAlarming_WhenTheReaderThrows_IsUnavailable_CachedAndWarnsOncePerWindow()
    {
        _reader.ReadStrictAsync(Arg.Any<CancellationToken>()).Returns<WorkerRosterSnapshot?>(_ => throw new InvalidOperationException("redis down"));
        var service = NewService();

        for (var i = 0; i < 5; i++)
        {
            Assert.True(await service.IsAlarmingAsync());
        }

        await _reader.Received(1).ReadStrictAsync(Arg.Any<CancellationToken>());
        Assert.Equal(1, Warnings);

        _time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(await service.IsAlarmingAsync());
        await _reader.Received(2).ReadStrictAsync(Arg.Any<CancellationToken>());
        Assert.Equal(2, Warnings);
    }

    [Fact]
    public async Task IsAlarming_WhenTheDatabaseFails_IsUnavailableAndCachedForFiveSeconds()
    {
        _reader.ReadStrictAsync(Arg.Any<CancellationToken>()).Returns(Roster());
        _channels.ListActiveChannelActivationsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<ActiveChannelActivation>>(_ => throw new InvalidOperationException("db down"));
        var service = NewService();

        Assert.True(await service.IsAlarmingAsync());
        Assert.True(await service.IsAlarmingAsync());

        await _channels.Received(1).ListActiveChannelActivationsAsync(Arg.Any<CancellationToken>());
        Assert.Equal(1, Warnings);
    }

    [Fact]
    public async Task IsAlarming_WhenTheReaderHangsAndIgnoresItsToken_IsUnavailableAfterTwoSeconds()
    {
        _reader.ReadStrictAsync(Arg.Any<CancellationToken>()).Returns(_ => new TaskCompletionSource<WorkerRosterSnapshot?>().Task);
        var call = NewService().IsAlarmingAsync();

        await AdvanceUntilCompletedAsync(call, TimeSpan.FromMilliseconds(500));

        Assert.True(await call);
        Assert.Equal(1, Warnings);
    }

    [Fact]
    public async Task IsAlarming_WhenTheAbandonedReadsCancellationCallbackBlocks_StillAnswersAtTheDeadline()
    {
        // Npgsql cancels a running query over a connection of its own, inside the token's callback;
        // during a database outage that callback blocks. The verdict must not wait for it.
        using var release = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reader.ReadStrictAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().Register(() =>
            {
                callbackEntered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(30));
            });
            return new TaskCompletionSource<WorkerRosterSnapshot?>().Task;
        });
        var service = NewService();

        try
        {
            var started = DateTime.UtcNow;
            var call = service.IsAlarmingAsync();
            await AdvanceUntilCompletedAsync(call, TimeSpan.FromMilliseconds(500));

            Assert.True(await call);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the verdict waited for the cancellation callback");
            // The abandoned read is still being cancelled; the cached answer does not wait for it either.
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(release.IsSet);
            Assert.True(await service.IsAlarmingAsync().WaitAsync(TimeSpan.FromSeconds(1)));
            await _reader.Received(1).ReadStrictAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task IsAlarming_ACancelledWaiterThrowsButTheFillContinuesForTheNextCaller()
    {
        var gate = new TaskCompletionSource<WorkerRosterSnapshot?>();
        _reader.ReadStrictAsync(Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        StubDesired();
        var service = NewService();
        using var cts = new CancellationTokenSource();

        var cancelled = service.IsAlarmingAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var next = service.IsAlarmingAsync();
        gate.SetResult(Roster(Unconfirmed("a", 999, TwitchJoinFailureReasons.NoConfirmation)));

        Assert.True(await next);
        await _reader.Received(1).ReadStrictAsync(Arg.Any<CancellationToken>());
    }

    // An Api process ten seconds old, reading the roster through the real reader over the given database.
    private ChannelJoinHealthService ColdStartServiceOver(IDatabase database)
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase().Returns(database);
        var services = new ServiceCollection().BuildServiceProvider();
        return new ChannelJoinHealthService(
            new WorkerRosterReader(multiplexer, NullLogger<WorkerRosterReader>.Instance),
            services.GetRequiredService<IServiceScopeFactory>(),
            new ChannelJoinHealthOptions(),
            _time,
            _log,
            _time.GetUtcNow().UtcDateTime.AddSeconds(-10));
    }

    private async Task AdvanceUntilCompletedAsync(Task task, TimeSpan step)
    {
        // The deadline timer is registered on the pool thread that runs the fill, so keep advancing
        // the fake clock until the call settles instead of racing a single Advance against it.
        var real = DateTime.UtcNow;
        while (!task.IsCompleted && DateTime.UtcNow - real < TimeSpan.FromSeconds(10))
        {
            _time.Advance(step);
            await Task.Delay(5);
        }

        Assert.True(task.IsCompleted, "the fill deadline never fired");
    }
}
