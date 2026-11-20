using EmotePurge.Core.Services;
using Npgsql;

namespace EmotePurge.Worker;

// The clock of the wish expiry run (#484, K2): one pass of IWishExpiryService every
// Wishes:ExpiryIntervalMinutes (default 15), the first one minute after start. All the logic lives in
// the service; this class only owns the switch, the timer and the failure handling.
//
// It deliberately does not wait for BootRecoveryGate: the run reconciles the channels it needs itself and
// takes row locks per wish, so it neither depends on boot recovery nor races it. With Wishes:Enabled false
// it logs one line and ends without opening a scope or a timer.
public class WishExpiryWorker(
    ILogger<WishExpiryWorker> logger,
    IServiceScopeFactory scopeFactory,
    WishOptions options,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Wish expiry worker is disabled (Wishes:Enabled is false).");
            return;
        }

        try
        {
            await Task.Delay(FirstRunDelay, timeProvider, stoppingToken);

            // Created before the first pass so the cadence starts at the first run; a pass that outlasts
            // the interval does not queue a second one (WaitForNextTickAsync coalesces missed ticks).
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.ExpiryIntervalMinutes), timeProvider);
            await RunOnceAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Regular shutdown.
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IWishExpiryService>();
            var result = await service.RunAsync(ct);
            LogResult(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Regular shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning("Wish expiry pass failed: {Failure}.", DescribeFailure(ex));
        }
    }

    private void LogResult(WishExpiryRunResult result)
    {
        if (result.Presented > 0 || result.Queued > 0)
        {
            logger.LogInformation(
                "Wish expiry presented {Presented} and queued {Queued} wishes ({HeldBack} held back, {Skipped} skipped) after reconciling {Channels} channels.",
                result.Presented, result.Queued, result.HeldBack, result.Skipped, result.ReconciledChannels);
        }

        if (result.FailedReconciles > 0 || result.Failed > 0)
        {
            logger.LogWarning(
                "Wish expiry had {FailedReconciles} failed channel reconciles and {Failed} failed wishes.",
                result.FailedReconciles, result.Failed);
        }
    }

    // Exception type and SQLSTATE only: a message can carry connection or row data.
    internal static string DescribeFailure(Exception exception)
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
