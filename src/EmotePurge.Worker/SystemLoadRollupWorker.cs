using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace EmotePurge.Worker;

public sealed class SystemLoadRollupWorker(
    ILogger<SystemLoadRollupWorker> logger,
    IServiceScopeFactory scopeFactory,
    SystemLoadOptions options,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ISystemLoadRollupService>();
            var hours = await service.RecomputeClosableHoursAsync(
                timeProvider.GetUtcNow().UtcDateTime,
                TimeSpan.FromMinutes(options.RollupGraceMinutes),
                options.IntervalSeconds,
                cancellationToken);
            logger.LogInformation("System-load rollup recomputed {Count} hourly rows.", hours);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Regular shutdown.
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "System-load rollup failed ({Failure}); the next hourly run will retry.",
                DescribeFailure(exception));
        }
    }

    private static string DescribeFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return $"{exception.GetType().Name}, SQLSTATE {postgres.SqlState}";
            }
        }

        return exception.GetType().Name;
    }
}
