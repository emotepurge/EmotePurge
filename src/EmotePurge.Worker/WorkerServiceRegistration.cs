using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Logging;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Worker.BotAccount;
using EmotePurge.Worker.ChatLogBackfill;
using EmotePurge.Worker.SevenTv;
using EmotePurge.Worker.WishCapture;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EmotePurge.Worker;

/// <summary>
/// The service graph of the worker image: the shared core and the long-running worker's hosted services.
/// </summary>
public static class WorkerServiceRegistration
{
    /// <summary>
    /// Infrastructure plus the worker's own singletons.
    /// </summary>
    public static IServiceCollection AddWorkerCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddEmotePurgeInfrastructure(configuration);
        services.AddSingleton<ITwitchChatManager, TwitchChatManager>();
        services.AddSingleton<IEmoteUsageCounter, EmoteUsageCounter>();
        services.AddSingleton<IBotChatterDetector, BotChatterDetector>();
        services.AddSingleton<IExcludedChatterFilter, ExcludedChatterFilter>();
        services.AddSingleton<BootRecoveryGate>();
        // Set by Worker on a BACKFILL: command, awaited by ChatLogBackfillWorker (spec 4.8).
        services.AddSingleton<ChatLogBackfillSignal>();
        // Set by Worker on a BOTACCOUNT: command, awaited by BotAccountWorker (plan #487 2.5). Registered whatever
        // BotAccount:Enabled says: the command loop that sets it always runs.
        services.AddSingleton<BotAccountSignal>();
        services.AddSingleton<WorkerStats>();
        // The reward capture's hand-over (K6, plan #486 2.1): one queue, written by TwitchChatManager through the
        // sink and read by the capture worker through the concrete type — the same instance under both names.
        services.AddSingleton<RewardObservationQueue>();
        services.AddSingleton<IRewardObservationSink>(provider => provider.GetRequiredService<RewardObservationQueue>());
        // The capture gaps (plan 2.7): the watchdog reports through the narrow recorder, the capture worker drives the
        // concrete tracker — again one instance under both names.
        services.AddSingleton<CaptureGapTracker>();
        services.AddSingleton<ICaptureGapRecorder>(provider => provider.GetRequiredService<CaptureGapTracker>());
        // Construct eagerly while the process service graph is assembled: a Redis live snapshot from
        // before this worker's first poll must not be treated as this process's own live count.
        services.AddSingleton(new WorkerSystemLoadCounterBaselines());
        services.Replace(ServiceDescriptor.Scoped<ISystemLoadWorkloadSource, WorkerSystemLoadWorkloadSource>());
        services.AddSingleton<WorkerIdentity>();
        services.AddSingleton<SevenTvSubscriptionRegistry>();
        services.AddSingleton<ISevenTvEventClient, SevenTvEventClient>();
        // The bot account's writing side (plan #487 2.2): opens the sealed token with the private key, which only the
        // worker holds. Registers nothing while BotAccount:Enabled is off; never registers the sealer.
        services.AddBotAccountWriter(configuration);
        return services;
    }

    /// <summary>
    /// The seventeen hosted services of the long-running worker (the log capture writer, or its heartbeat when
    /// capture is off, goes first and so stops last; the wish capture worker goes last and so stops first, the bot
    /// account worker right before it). The host starts them in registration
    /// order, and Worker deliberately goes first: it runs the boot recovery (rejoin every tracked
    /// channel, initial 7TV sync) that the others assume has happened. The ordering is not
    /// load-bearing on its own, though — BootRecoveryGate is what actually enforces it, because
    /// SevenTvPeriodicResyncWorker running concurrently with the boot sync for the same channel
    /// makes two AppDbContext instances insert the same (ChannelId, SevenTvEmoteId) rows and one
    /// loses on the unique index. Read that class before reordering anything here.
    /// </summary>
    public static IServiceCollection AddWorkerHostedServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogCapture(configuration, LogSource.Worker);
        services.AddHostedService<Worker>();
        services.AddHostedService<UsageFlushWorker>();
        services.AddHostedService<SevenTvPeriodicResyncWorker>();
        services.AddHostedService<SevenTvEventWorker>();
        services.AddHostedService<TwitchConnectionWatchdog>();
        services.AddHostedService<WorkerHealthPublisher>();
        services.AddHostedService<WorkerRosterPublisher>();
        services.AddHostedService<TwitchLivePollWorker>();
        services.AddHostedService<TwitchIdentityReconcileWorker>();
        services.AddHostedService<DataRetentionWorker>();
        services.AddSystemLoadSampler(SystemLoadSource.Worker);
        services.AddHostedService<SystemLoadRollupWorker>();
        // Exits after one log line unless ChatLogBackfill:Enabled is true; waits for BootRecoveryGate
        // like DataRetentionWorker.
        services.AddHostedService<ChatLogBackfillWorker>();
        // Exits after one log line unless Wishes:Enabled is true; does not wait for BootRecoveryGate.
        services.AddHostedService<WishExpiryWorker>();
        // The bot account's loop (plan #487 2.5): exits after one log line unless BotAccount:Enabled and Wishes:Enabled
        // are both true; does not wait for BootRecoveryGate. Before the capture worker, so that one stays last.
        services.AddHostedService<BotAccountWorker>();
        // K6 (plan #486 2.3): last on purpose. The host stops services one after the other in reverse order, so this one
        // stops first: it closes the observation queue while the chat client (Worker) still runs, drains with a budget
        // of its own of a few seconds, and leaves the rest of the shutdown timeout to the final usage flush and to the
        // log capture writer, both of which stop after it. Housekeeping runs whatever the switches say; the queue is
        // drained only with Wishes:Capture:Enabled and Wishes:Enabled.
        services.AddHostedService<WishCaptureWorker>();
        return services;
    }
}
