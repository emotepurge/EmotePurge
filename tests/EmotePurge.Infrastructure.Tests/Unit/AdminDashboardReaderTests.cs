using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class AdminDashboardReaderTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly IAdminDashboardQueryService _query = Substitute.For<IAdminDashboardQueryService>();
    private readonly IRateLimitTelemetryReader _telemetry = Substitute.For<IRateLimitTelemetryReader>();

    private AdminDashboardReader NewReader()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _query);
        return new AdminDashboardReader(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _telemetry, _time);
    }

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
}
