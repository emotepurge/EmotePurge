using EmotePurge.Core.Services;

namespace EmotePurge.Worker.BotAccount;

/// <summary>
/// The bot account's loop (plan #487 2.5): the only writer for the bot account, hence the neutral name. A concrete class,
/// registered only through <c>AddHostedService</c>, right before <see cref="WishCapture.WishCaptureWorker"/>. All logic
/// lives in <see cref="IBotAccountRunner"/>; this class owns the switches, the cadence and the failure handling.
/// <para>
/// With <c>BotAccount:Enabled</c> off, or without a use of the account switched on (today only <c>Wishes:Enabled</c>),
/// it logs one line and ends: no scope, no runner, so the token is never opened (AK96). It does not wait for
/// <see cref="BootRecoveryGate"/>: the runner reads only the database and 7TV.
/// </para>
/// <para>
/// <b>Cadence.</b> A pass every 30 seconds, counted from the start of the previous one; a <c>BOTACCOUNT:</c> nudge
/// (<see cref="BotAccountSignal"/>) starts the next pass at once. A pass that outlasts the interval is followed by the
/// next one without a wait, never by two. The runner is resolved once from a scope that lives as long as the loop: it
/// holds the advisory lock's connection, and disposing the scope at shutdown is what releases the lock.
/// </para>
/// </summary>
public sealed class BotAccountWorker(
    ILogger<BotAccountWorker> logger,
    IServiceScopeFactory scopeFactory,
    BotAccountOptions botAccountOptions,
    WishOptions wishOptions,
    BotAccountSignal signal,
    TimeProvider timeProvider) : BackgroundService
{
    /// <summary>The cadence of the passes (plan 2.5).</summary>
    public static readonly TimeSpan PassInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!botAccountOptions.Enabled || !wishOptions.Enabled)
        {
            logger.LogInformation(
                "Bot account worker is disabled (BotAccount:Enabled is {BotAccountEnabled}, Wishes:Enabled is {WishesEnabled}).",
                botAccountOptions.Enabled, wishOptions.Enabled);
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var scope = scopeFactory.CreateAsyncScope();
                await using (scope)
                {
                    IBotAccountRunner runner;
                    try
                    {
                        runner = scope.ServiceProvider.GetRequiredService<IBotAccountRunner>();
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Never out of ExecuteAsync: StopHost would take the live counting down with it.
                        logger.LogWarning("Bot account worker could not start its loop ({ExceptionType}); retrying.", ex.GetType().Name);
                        await Task.Delay(PassInterval, timeProvider, stoppingToken);
                        continue;
                    }

                    await RunLoopAsync(runner, stoppingToken);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Regular shutdown. Disposing the scope closed the lock connection.
        }
    }

    private async Task RunLoopAsync(IBotAccountRunner runner, CancellationToken stoppingToken)
    {
        var warnedAboutLock = false;
        while (true)
        {
            var started = timeProvider.GetUtcNow();
            try
            {
                var result = await runner.RunPassAsync(stoppingToken);
                if (result == BotAccountPassResult.NoLock)
                {
                    if (!warnedAboutLock)
                    {
                        logger.LogWarning(
                            "Bot account: another worker holds the bot account lock; this one writes nothing and retries every {Seconds} s.",
                            (int)PassInterval.TotalSeconds);
                        warnedAboutLock = true;
                    }
                }
                else
                {
                    warnedAboutLock = false;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Bot account pass failed: {Failure}.", WishExpiryWorker.DescribeFailure(ex));
            }

            var wait = started + PassInterval - timeProvider.GetUtcNow();
            await signal.WaitAsync(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, timeProvider, stoppingToken);
        }
    }
}
