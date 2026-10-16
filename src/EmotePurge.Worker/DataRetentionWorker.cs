using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Npgsql;

namespace EmotePurge.Worker;

// Enforces the periods of RetentionPolicy (issues #243/#244) in two independent loops.
//
// The hourly log-expiry loop deletes captured log entries older than RetentionPolicy.LogEntry
// (7 days) in batches, and capture status rows whose last heartbeat is older than that. Its first
// pass runs one minute after start, and it never waits for the boot gate (see ExecuteAsync). In dry
// run it deletes nothing and warns at most once per UTC day.
//
// The daily loop handles everything else, once a day by default. It waits for
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
    BootRecoveryGate bootRecoveryGate,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan LogExpiryFirstRunDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LogExpiryInterval = TimeSpan.FromHours(1);

    private DateOnly _lastLogDryRunWarningDay;

    // Two independent loops. The log-expiry loop starts first and never waits for the boot gate: log
    // rows are written continuously and a stuck boot recovery must not let them pile up. Each loop
    // catches its own failures, so neither can end the other (and a faulted ExecuteAsync would stop
    // the host).
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(RunLogExpiryLoopAsync(stoppingToken), RunDailyLoopAsync(stoppingToken));

    private async Task RunLogExpiryLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(LogExpiryFirstRunDelay, timeProvider, stoppingToken);

            // Created before the first pass so the hourly cadence starts at the first run, not after it.
            using var timer = new PeriodicTimer(LogExpiryInterval, timeProvider);
            await ExpireLogsOnceAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ExpireLogsOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Regular shutdown.
        }
    }

    private async Task ExpireLogsOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var retentionService = scope.ServiceProvider.GetRequiredService<IDataRetentionService>();
            var summary = await retentionService.ExpireLogsAsync(options.Enforce, ct);
            LogLogExpirySummary(summary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Regular shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning("Log expiry pass failed: {Failure}.", DescribeFailure(ex));
        }
    }

    // Enforced: only a pass that deleted something is worth a line, it runs hourly. Dry run: at most one
    // Warning per UTC day, the same reminder the daily pass gives that Retention:Enforce is still false.
    private void LogLogExpirySummary(LogRetentionSummary summary)
    {
        var deleted = summary.LogEntriesDeleted + summary.LogCaptureStatusRowsDeleted;
        if (summary.Enforced)
        {
            if (deleted > 0)
            {
                logger.LogInformation(
                    "Log expiry deleted {Entries} log entries and {StatusRows} capture status rows.",
                    summary.LogEntriesDeleted, summary.LogCaptureStatusRowsDeleted);
            }

            return;
        }

        var day = DateOnly.FromDateTime(summary.ReferenceTimeUtc);
        if (day == _lastLogDryRunWarningDay)
        {
            return;
        }

        _lastLogDryRunWarningDay = day;
        logger.LogWarning(
            "Log expiry dry run: nothing was deleted, Retention:Enforce is false. Would delete {Entries} log entries and {StatusRows} capture status rows.",
            summary.LogEntriesDeleted, summary.LogCaptureStatusRowsDeleted);
    }

    private async Task RunDailyLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await DailyLoopCoreAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Regular shutdown.
        }
    }

    private async Task DailyLoopCoreAsync(CancellationToken stoppingToken)
    {
        await bootRecoveryGate.Completed.WaitAsync(stoppingToken);
        await Task.Delay(TimeSpan.FromMinutes(options.StartupDelayMinutes), timeProvider, stoppingToken);

        // First run happens right after the delay, not only on the timer: a pass is the whole
        // point of this worker existing, and waiting a further IntervalHours (up to 24h) for the
        // first one would make a freshly deployed job look inactive for a day.
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.IntervalHours), timeProvider);
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
