using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure;
using EmotePurge.Infrastructure.Logging;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Worker.ChatLogBackfill;
using EmotePurge.Worker.SevenTv;
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
        services.AddSingleton<WorkerStats>();
        // Construct eagerly while the process service graph is assembled: a Redis live snapshot from
        // before this worker's first poll must not be treated as this process's own live count.
        services.AddSingleton(new WorkerSystemLoadCounterBaselines());
        services.Replace(ServiceDescriptor.Scoped<ISystemLoadWorkloadSource, WorkerSystemLoadWorkloadSource>());
        services.AddSingleton<WorkerIdentity>();
        services.AddSingleton<SevenTvSubscriptionRegistry>();
        services.AddSingleton<ISevenTvEventClient, SevenTvEventClient>();
        return services;
    }

    /// <summary>
    /// The fourteen hosted services of the long-running worker (the log capture writer, or its heartbeat when
    /// capture is off, goes first and so stops last). The host starts them in registration
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
        return services;
    }
}
