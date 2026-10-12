using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class AdminDashboardReaderTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly IAdminDashboardQueryService _query = Substitute.For<IAdminDashboardQueryService>();
    private readonly IRateLimitTelemetryReader _telemetry = Substitute.For<IRateLimitTelemetryReader>();

    private readonly CapturingLogger _log = new();

    private AdminDashboardReader NewReader()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _query);
        return new AdminDashboardReader(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _telemetry,
            _time,
            new SystemLoadOptions { IntervalSeconds = 15 },
            _log);
    }

    private static AdminDashboardLoad Load(AdminDashboardLoadRange range) =>
        new(false, AdminDashboardLoadRanges.ToWire(range), "raw", 60, default, default, default, null, 15, [],
            null!, null!, null!, null!, null);

    private void StubLoad(Func<AdminDashboardLoadRange, Task<AdminDashboardLoad>> answer) =>
        _query.GetLoadAsync(Arg.Any<AdminDashboardLoadRange>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => answer(call.Arg<AdminDashboardLoadRange>()));

    private static AdminDashboardActivity Activity(AdminDashboardActivityRange range) =>
        new(AdminDashboardActivityRanges.ToWire(range), default, default, true, default, []);

    [Fact]
    public async Task Activity_IsServedFromCacheWithin60SecondsAndRefreshedAfterwards()
    {
        _query.GetActivityAsync(Arg.Any<AdminDashboardActivityRange>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call => Activity(call.Arg<AdminDashboardActivityRange>()));
        var reader = NewReader();

        await reader.GetActivityAsync(AdminDashboardActivityRange.Days30);
        _time.Advance(TimeSpan.FromSeconds(59));
        await reader.GetActivityAsync(AdminDashboardActivityRange.Days30);
        await _query.Received(1).GetActivityAsync(AdminDashboardActivityRange.Days30, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromSeconds(1));
        await reader.GetActivityAsync(AdminDashboardActivityRange.Days30);
        await _query.Received(2).GetActivityAsync(AdminDashboardActivityRange.Days30, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Activity_PassesTheProvidersClockAsUtcNow()
    {
        _query.GetActivityAsync(Arg.Any<AdminDashboardActivityRange>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call => Activity(call.Arg<AdminDashboardActivityRange>()));

        await NewReader().GetActivityAsync(AdminDashboardActivityRange.Days90);

        await _query.Received(1).GetActivityAsync(
            AdminDashboardActivityRange.Days90,
            Arg.Is<DateTime>(d => d == new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc) && d.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Activity_KeepsRangesApart()
    {
        _query.GetActivityAsync(Arg.Any<AdminDashboardActivityRange>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call => Activity(call.Arg<AdminDashboardActivityRange>()));
        var reader = NewReader();

        var a = await reader.GetActivityAsync(AdminDashboardActivityRange.Days30);
        var b = await reader.GetActivityAsync(AdminDashboardActivityRange.Months13);

        Assert.Equal("30d", a.Range);
        Assert.Equal("13mo", b.Range);
    }

    [Fact]
    public async Task ConcurrentReaders_ShareOneFill()
    {
        var gate = new TaskCompletionSource<AdminDashboardActivity>();
        _query.GetActivityAsync(Arg.Any<AdminDashboardActivityRange>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);
        var reader = NewReader();

        var first = reader.GetActivityAsync(AdminDashboardActivityRange.Days30);
        var second = reader.GetActivityAsync(AdminDashboardActivityRange.Days30);
        var third = reader.GetActivityAsync(AdminDashboardActivityRange.Days30);
        gate.SetResult(Activity(AdminDashboardActivityRange.Days30));

        Assert.Same(await first, await second);
        Assert.Same(await first, await third);
        await _query.Received(1).GetActivityAsync(AdminDashboardActivityRange.Days30, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedFill_IsNotCached()
    {
        var calls = 0;
        _query.GetActivityAsync(Arg.Any<AdminDashboardActivityRange>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call => ++calls == 1
                ? Task.FromException<AdminDashboardActivity>(new InvalidOperationException("db down"))
                : Task.FromResult(Activity(call.Arg<AdminDashboardActivityRange>())));
        var reader = NewReader();

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetActivityAsync(AdminDashboardActivityRange.Days30));
        var recovered = await reader.GetActivityAsync(AdminDashboardActivityRange.Days30);

        Assert.Equal("30d", recovered.Range);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SnapshotAndTrend_AreCachedFor30SecondsEach()
    {
        _telemetry.ReadAsync(Arg.Any<CancellationToken>()).Returns(RateLimitTelemetrySnapshot.Unavailable);
        _telemetry.ReadTrendAsync(Arg.Any<CancellationToken>()).Returns(RateLimitTelemetryTrend.Unavailable);
        var reader = NewReader();

        await reader.GetRateLimitSnapshotAsync();
        await reader.GetRateLimitTrendAsync();
        _time.Advance(TimeSpan.FromSeconds(29));
        await reader.GetRateLimitSnapshotAsync();
        await reader.GetRateLimitTrendAsync();
        await _telemetry.Received(1).ReadAsync(Arg.Any<CancellationToken>());
        await _telemetry.Received(1).ReadTrendAsync(Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromSeconds(1));
        await reader.GetRateLimitSnapshotAsync();
        await reader.GetRateLimitTrendAsync();
        await _telemetry.Received(2).ReadAsync(Arg.Any<CancellationToken>());
        await _telemetry.Received(2).ReadTrendAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AReaderCancelling_DoesNotCancelTheSharedFill()
    {
        var gate = new TaskCompletionSource<RateLimitTelemetryTrend>();
        _telemetry.ReadTrendAsync(Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        var reader = NewReader();
        using var cts = new CancellationTokenSource();

        var cancelled = reader.GetRateLimitTrendAsync(cts.Token);
        var patient = reader.GetRateLimitTrendAsync();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.SetResult(RateLimitTelemetryTrend.Unavailable);

        Assert.Same(RateLimitTelemetryTrend.Unavailable, await patient);
    }

    [Theory]
    [InlineData(AdminDashboardLoadRange.Hour1, 30)]
    [InlineData(AdminDashboardLoadRange.Hours24, 30)]
    [InlineData(AdminDashboardLoadRange.Days7, 60)]
    [InlineData(AdminDashboardLoadRange.Days30, 60)]
    [InlineData(AdminDashboardLoadRange.Days90, 60)]
    [InlineData(AdminDashboardLoadRange.Months13, 60)]
    public async Task Load_IsCachedForTheRangesTtl(AdminDashboardLoadRange range, int ttlSeconds)
    {
        StubLoad(r => Task.FromResult(Load(r)));
        var reader = NewReader();

        await reader.GetLoadAsync(range);
        _time.Advance(TimeSpan.FromSeconds(ttlSeconds - 1));
        await reader.GetLoadAsync(range);
        await _query.Received(1).GetLoadAsync(range, Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromSeconds(1));
        await reader.GetLoadAsync(range);
        await _query.Received(2).GetLoadAsync(range, Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Load_PassesTheClockAndTheConfiguredSamplingInterval()
    {
        StubLoad(r => Task.FromResult(Load(r)));

        var read = await NewReader().GetLoadAsync(AdminDashboardLoadRange.Days7);

        Assert.False(read.Busy);
        Assert.NotNull(read.Load);
        await _query.Received(1).GetLoadAsync(
            AdminDashboardLoadRange.Days7,
            Arg.Is<DateTime>(d => d == new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc) && d.Kind == DateTimeKind.Utc),
            15,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Load_ConcurrentReadersOfOneRangeShareOneRebuild()
    {
        var gate = new TaskCompletionSource<AdminDashboardLoad>();
        StubLoad(_ => gate.Task);
        var reader = NewReader();

        var first = reader.GetLoadAsync(AdminDashboardLoadRange.Hours24);
        var second = reader.GetLoadAsync(AdminDashboardLoadRange.Hours24);
        var third = reader.GetLoadAsync(AdminDashboardLoadRange.Hours24);
        gate.SetResult(Load(AdminDashboardLoadRange.Hours24));

        Assert.Same((await first).Load, (await second).Load);
        Assert.Same((await first).Load, (await third).Load);
        await _query.Received(1).GetLoadAsync(
            AdminDashboardLoadRange.Hours24, Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Equal(2, _log.Debug.Count);
    }

    [Fact]
    public async Task Load_RebuildsRunOneAtATimeAcrossRanges()
    {
        var running = 0;
        var maxRunning = 0;
        var release = new TaskCompletionSource();
        StubLoad(async r =>
        {
            maxRunning = Math.Max(maxRunning, Interlocked.Increment(ref running));
            await release.Task;
            Interlocked.Decrement(ref running);
            return Load(r);
        });
        var reader = NewReader();

        var a = reader.GetLoadAsync(AdminDashboardLoadRange.Hours24);
        var b = reader.GetLoadAsync(AdminDashboardLoadRange.Days7);
        await WaitUntilAsync(() => running == 1);
        await Task.Delay(50); // the second range must not start while the first one holds the gate
        Assert.Equal(1, running);

        release.SetResult();
        Assert.False((await a).Busy);
        Assert.False((await b).Busy);
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task Load_WaitingLongerThanFiveSecondsForTheGateAnswersBusyWithoutStartingARebuild()
    {
        var release = new TaskCompletionSource();
        StubLoad(async r =>
        {
            await release.Task;
            return Load(r);
        });
        var reader = NewReader();

        var holder = reader.GetLoadAsync(AdminDashboardLoadRange.Hours24);
        await WaitUntilAsync(() => _query.ReceivedCalls().Any());
        var waiting = reader.GetLoadAsync(AdminDashboardLoadRange.Days7);
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromSeconds(5));

        var busy = await waiting;

        Assert.True(busy.Busy);
        Assert.Null(busy.Load);
        Assert.Equal(5, busy.RetryAfterSeconds);
        await _query.DidNotReceive().GetLoadAsync(
            AdminDashboardLoadRange.Days7, Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // The busy answer is not cached: once the gate is free the same range rebuilds normally.
        release.SetResult();
        Assert.False((await holder).Busy);
        var retry = await reader.GetLoadAsync(AdminDashboardLoadRange.Days7);
        Assert.False(retry.Busy);
        Assert.NotNull(retry.Load);
    }

    [Fact]
    public async Task Load_AFailedRebuildIsNotCachedAndFreesTheGate()
    {
        var calls = 0;
        StubLoad(r => ++calls == 1
            ? Task.FromException<AdminDashboardLoad>(new InvalidOperationException("db down"))
            : Task.FromResult(Load(r)));
        var reader = NewReader();

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetLoadAsync(AdminDashboardLoadRange.Days30));
        var recovered = await reader.GetLoadAsync(AdminDashboardLoadRange.Days30);

        Assert.False(recovered.Busy);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Load_LogsStartAndEndOnlyForRealRebuilds()
    {
        StubLoad(r => Task.FromResult(Load(r)));
        var reader = NewReader();

        await reader.GetLoadAsync(AdminDashboardLoadRange.Days7);
        await reader.GetLoadAsync(AdminDashboardLoadRange.Days7);

        Assert.Equal(2, _log.Debug.Count);
        Assert.Equal("Load dashboard cold rebuild started range=7d", _log.Debug[0]);
        Assert.Matches(@"^Load dashboard cold rebuild finished range=7d ms=\d+$", _log.Debug[1]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
    }

    private sealed class CapturingLogger : ILogger<AdminDashboardReader>
    {
        public List<string> Debug { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug)
            {
                lock (Debug)
                {
                    Debug.Add(formatter(state, exception));
                }
            }
        }
    }
}
