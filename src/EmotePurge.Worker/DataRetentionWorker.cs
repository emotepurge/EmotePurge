using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Npgsql;

namespace EmotePurge.Worker;

// Enforces the periods of RetentionPolicy (issues #243/#244) once a day by default. Waits for
// BootRecoveryGate.Completed because the channel purge inside IDataRetentionService.RunAsync
// touches the very rows boot recovery reads and writes (see TwitchIdentityReconcileWorker for the
// same reasoning about renames/merges), then RetentionOptions.StartupDelayMinutes so a restart loop
// does not begin every start with a pass. After that a PeriodicTimer, first tick right after the
// delay. Dry run (Retention:Enforce = false, the default) is the safe starting state: the job
// counts and writes nothing but the channel deactivation stamps that open the 180-day period, and
// every tick logs a Warning as a reminder that nothing is actually being deleted.
public class DataRetentionWorker(
    ILogger<DataRetentionWorker> logger,
    IServiceScopeFactory scopeFactory,
    RetentionOptions options,
    BootRecoveryGate bootRecoveryGate) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await bootRecoveryGate.Completed.WaitAsync(stoppingToken);
        await Task.Delay(TimeSpan.FromMinutes(options.StartupDelayMinutes), stoppingToken);

        // First run happens right after the delay, not only on the timer: a pass is the whole
        // point of this worker existing, and waiting a further IntervalHours (up to 24h) for the
        // first one would make a freshly deployed job look inactive for a day.
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.IntervalHours));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var retentionService = scope.ServiceProvider.GetRequiredService<IDataRetentionService>();
            var summary = await retentionService.RunAsync(options.Enforce, ct);
            LogSummary(summary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Regular shutdown.
        }
        catch (Exception ex)
        {
            // Runs forever on a timer — a Postgres hiccup must cost one tick, never the worker
            // host (StopHost default would take the usage flush down with it). The exception
            // message can quote a key value (see the remark on IDataRetentionService.RunAsync),
            // so only the exception type and, if a Postgres error is in the chain, its SQLSTATE
            // are logged — never the message.
            logger.LogWarning("Retention pass failed: {Failure}.", DescribeFailure(ex));
        }
    }

    // Exactly one line per tick, even when every count is zero: it is the only proof this worker
    // is alive and past the boot gate. Warning in dry-run mode on purpose (Plan-Entscheidung 4) —
    // it is the daily reminder that Retention:Enforce is still false and nothing is actually being
    // deleted; Information once it is enforcing for real.
    private void LogSummary(RetentionRunSummary summary)
    {
        var details = RetentionRunSummaryFormatter.Format(summary);
        if (summary.Enforced)
        {
            logger.LogInformation("Retention pass enforced. {Details}", details);
        }
        else
        {
            logger.LogWarning(
                "Retention dry run: nothing was deleted, Retention:Enforce is false. {Details}", details);
        }
    }

    private static string DescribeFailure(Exception exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException postgres)
            {
                return $"{exception.GetType().Name}, SQLSTATE {postgres.SqlState}";
            }
        }

        return exception.GetType().Name;
    }
}
