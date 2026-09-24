using EmotePurge.Core.Services;
using EmotePurge.Infrastructure;
using EmotePurge.Worker.Harness;
using EmotePurge.Worker.SevenTv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EmotePurge.Worker;

/// <summary>
/// The service graph of the worker image, split into the part both entry points share and the part
/// only the long-running worker gets.
/// <para>
/// It exists so <c>Program</c> cannot accidentally hand the harness branch a hosted service. That
/// is not a style preference: <c>docker compose run</c> replaces a service's <c>command</c>, not
/// its <c>entrypoint</c>, and a harness container that started the ten hosted services would sit
/// in IRC next to the production worker and double every usage row through the additive UPSERT
/// (Codex-adversarial "Fail-open CLI"). With the registration in one place, a test can assert the
/// absence directly instead of trusting a reading of <c>Program</c>.
/// </para>
/// </summary>
public static class WorkerServiceRegistration
{
    /// <summary>
    /// Infrastructure plus the worker's own singletons. Shared by both entry points — the harness
    /// needs the bot detector and, through infrastructure, the query services and the log client.
    /// </summary>
    public static IServiceCollection AddWorkerCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddEmotePurgeInfrastructure(configuration);
        services.AddSingleton<ITwitchChatManager, TwitchChatManager>();
        services.AddSingleton<IEmoteUsageCounter, EmoteUsageCounter>();
        services.AddSingleton<IBotChatterDetector, BotChatterDetector>();
        services.AddSingleton<IExcludedChatterFilter, ExcludedChatterFilter>();
        services.AddSingleton<BootRecoveryGate>();
        services.AddSingleton<WorkerStats>();
        services.AddSingleton<WorkerIdentity>();
        services.AddSingleton<SevenTvSubscriptionRegistry>();
        services.AddSingleton<ISevenTvEventClient, SevenTvEventClient>();
        return services;
    }

    /// <summary>
    /// The ten hosted services of the long-running worker. The host starts them in registration
    /// order, and Worker deliberately goes first: it runs the boot recovery (rejoin every tracked
    /// channel, initial 7TV sync) that the others assume has happened. The ordering is not
    /// load-bearing on its own, though — BootRecoveryGate is what actually enforces it, because
    /// SevenTvPeriodicResyncWorker running concurrently with the boot sync for the same channel
    /// makes two AppDbContext instances insert the same (ChannelId, SevenTvEmoteId) rows and one
    /// loses on the unique index. Read that class before reordering anything here.
    /// </summary>
    public static IServiceCollection AddWorkerHostedServices(this IServiceCollection services)
    {
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
        return services;
    }

    /// <summary>
    /// The harness branch: the shared core plus its own options and runner, and deliberately
    /// <b>no</b> <c>AddHostedService</c> — the process does one run and ends.
    /// </summary>
    public static IServiceCollection AddHarness(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddWorkerCore(configuration);

        var harnessOptions = new HarnessOptions();
        configuration.GetSection("Harness").Bind(harnessOptions);
        services.AddSingleton(harnessOptions);

        // TimeProvider is not part of the base container; the runner needs it to pin the window to
        // the last complete UTC day, which is exactly the thing a test has to be able to fix.
        services.TryAddSingleton(TimeProvider.System);

        // Scoped, because it takes the two scoped query services (AppDbContext). One scope, one run.
        services.AddScoped<HarnessRunner>();
        return services;
    }
}
