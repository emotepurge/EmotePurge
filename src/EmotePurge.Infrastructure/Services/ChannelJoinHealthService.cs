using System.Diagnostics;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// <see cref="IChannelJoinHealthService"/>: the roster read plus the active-channel list, judged by
/// <see cref="ChannelJoinHealthPolicy"/>. A singleton (the roster reader is one) that opens a scope
/// per database read, like <c>LogStatusReader</c>.
/// <para>
/// The public decision sits behind a <see cref="TtlCache{T}"/>: one shared fill with a deadline of its
/// own (independent of any caller, and enforced even by a reader that ignores its token), the result
/// cached for <see cref="CacheTtl"/>, and a failed fill turned into the cached "unavailable" outcome
/// inside the fill, so hammering a failing dependency costs one fill and one Warning per window.
/// </para>
/// </summary>
internal sealed class ChannelJoinHealthService : IChannelJoinHealthService
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan FillDeadline = TimeSpan.FromSeconds(2);

    private const string SingleKey = "";

    private readonly IWorkerRosterReader _rosterReader;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<ChannelJoinHealthService> _logger;
    private readonly TtlCache<ChannelJoinHealthReport> _cache;
    private readonly TimeSpan _grace;
    private readonly DateTime _processStartedUtc;

    public ChannelJoinHealthService(
        IWorkerRosterReader rosterReader,
        IServiceScopeFactory scopeFactory,
        ChannelJoinHealthOptions options,
        TimeProvider time,
        ILogger<ChannelJoinHealthService> logger,
        DateTime processStartedUtc)
    {
        _rosterReader = rosterReader;
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
        _grace = TimeSpan.FromSeconds(options.GraceSeconds);
        _processStartedUtc = processStartedUtc;
        _cache = new TtlCache<ChannelJoinHealthReport>(time, CacheTtl);
    }

    /// <summary>Start of this OS process, read at construction by the DI factory (not lazily on first request).</summary>
    public static DateTime CurrentProcessStartedUtc() => Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public async Task<ChannelJoinHealthReport> EvaluateAsync(WorkerRosterSnapshot? snapshot, CancellationToken cancellationToken = default)
    {
        // The list is only needed once a usable roster exists; skipping the read otherwise keeps a
        // dead worker from also costing a query on every evaluation.
        var desired = snapshot is null ? null : await LoadDesiredAsync(cancellationToken);
        return ChannelJoinHealthPolicy.Evaluate(
            snapshot, desired, _time.GetUtcNow().UtcDateTime, _processStartedUtc, WorkerHealthKeys.RosterTtl, _grace);
    }

    public async Task<bool> IsAlarmingAsync(CancellationToken cancellationToken = default)
    {
        var report = await _cache.GetAsync(SingleKey, FillAsync, cancellationToken);
        return report.Status is ChannelJoinHealthStatus.Failing or ChannelJoinHealthStatus.Unavailable;
    }

    private async Task<ChannelJoinHealthReport> FillAsync()
    {
        var abandon = new CancellationTokenSource();
        var work = ReadAndEvaluateAsync(abandon.Token);
        try
        {
            var report = await work.WaitAsync(FillDeadline, _time);
            abandon.Dispose();
            return report;
        }
        catch (Exception ex)
        {
            // Whatever the cause (timeout, reader fault), the fill's answer is "not assessable", and it
            // is given now: the abandoned read is cancelled off this path, because a cancellation
            // callback (Npgsql opens a connection of its own to cancel a query) can block for as long
            // as the outage that made the fill time out in the first place.
            _ = work.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _ = CancelAbandonedAsync(abandon);
            _logger.LogWarning(ex, "Join health could not be evaluated; reporting it as unavailable");
            return new ChannelJoinHealthReport(ChannelJoinHealthStatus.Unavailable, (int)_grace.TotalSeconds, [], [], 0, 0);
        }
    }

    private async Task<ChannelJoinHealthReport> ReadAndEvaluateAsync(CancellationToken ct)
    {
        var snapshot = await _rosterReader.ReadAsync(ct);
        return await EvaluateAsync(snapshot, ct);
    }

    private async Task<IReadOnlyList<ActiveChannelActivation>?> LoadDesiredAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IChannelService>().ListActiveChannelActivationsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The active channel list could not be read for the join health check; reporting it as unavailable");
            return null;
        }
    }

    // Runs the cancellation callbacks on the pool, never on the caller's path, and disposes the source
    // only once no callback still runs on it.
    private static async Task CancelAbandonedAsync(CancellationTokenSource abandon)
    {
        try
        {
            await Task.Run(abandon.Cancel);
        }
        catch (AggregateException)
        {
            // A throwing callback belongs to the abandoned read, not to the verdict.
        }
        finally
        {
            abandon.Dispose();
        }
    }
}
