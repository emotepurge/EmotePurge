using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EmotePurge.Infrastructure.Logging.Query;

/// <summary>
/// <see cref="ILogStatusReader"/>: the capture status behind a 30 s <see cref="TtlCache{T}"/> with
/// single-flight (like <c>AdminDashboardReader.SnapshotTtl</c>). The fill outlives the request that started
/// it, so it opens a scope of its own for the scoped query service and runs inside
/// <see cref="LogCaptureSuppression"/> itself — the viewer's own EF and Npgsql diagnostics are never
/// captured, whichever request happens to start the fill. A failed fill is not cached.
/// </summary>
internal sealed class LogStatusReader : ILogStatusReader
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private const string SingleKey = "";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TtlCache<LogCaptureStatusView> _cache;

    public LogStatusReader(IServiceScopeFactory scopeFactory, TimeProvider time)
    {
        _scopeFactory = scopeFactory;
        _cache = new TtlCache<LogCaptureStatusView>(time, Ttl);
    }

    public Task<LogCaptureStatusView> ReadAsync(CancellationToken ct) => _cache.GetAsync(SingleKey, FillAsync, ct);

    private async Task<LogCaptureStatusView> FillAsync()
    {
        using var suppression = LogCaptureSuppression.Enter();
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILogQueryService>().ComputeStatusAsync(CancellationToken.None);
    }
}
