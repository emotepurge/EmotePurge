using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public sealed class SystemLoadSampler(
    SystemLoadSource source,
    IServiceScopeFactory scopeFactory,
    ISystemLoadSnapshotSource snapshotSource,
    SystemLoadOptions options,
    TimeProvider timeProvider,
    ILogger<SystemLoadSampler> logger) : BackgroundService
{
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(5);
    private readonly Guid _processInstanceId = Guid.NewGuid();
    private DateTimeOffset? _lastWarningUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("System-load sampling is disabled by SystemLoad:Enabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.IntervalSeconds), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SampleOnceAsync(stoppingToken);
        }
    }

    private async Task SampleOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await snapshotSource.ReadAsync(cancellationToken);
            await using var scope = scopeFactory.CreateAsyncScope();
            var workload = await scope.ServiceProvider
                .GetRequiredService<ISystemLoadWorkloadSource>()
                .ReadAsync(cancellationToken);
            await scope.ServiceProvider
                .GetRequiredService<ISystemLoadSamplingService>()
                .RecordAsync(source, _processInstanceId, snapshot, workload, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Regular shutdown.
        }
        catch (Exception exception)
        {
            var nowUtc = timeProvider.GetUtcNow();
            if (_lastWarningUtc is null || nowUtc - _lastWarningUtc >= WarningInterval)
            {
                _lastWarningUtc = nowUtc;
                logger.LogWarning(
                    "System-load sample failed ({Failure}); the next interval will retry.",
                    exception.GetType().Name);
            }
        }
    }
}

public static class SystemLoadSamplerRegistration
{
    public static IServiceCollection AddSystemLoadSampler(this IServiceCollection services, SystemLoadSource source)
    {
        services.AddHostedService(serviceProvider => ActivatorUtilities.CreateInstance<SystemLoadSampler>(serviceProvider, source));
        return services;
    }
}
