using EmotePurge.Core.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// Registers the log capture of one process. Called by the Api and the long-running worker only: never
/// from <c>AddEmotePurgeInfrastructure</c> and never from the harness, so a one-shot container cannot
/// write log rows. It must run before every other hosted-service registration, because the host stops
/// hosted services in reverse order and the writer has to be the last to stop (it drains the buffer).
/// </summary>
public static class LogCaptureRegistration
{
    public static IServiceCollection AddLogCapture(this IServiceCollection services, IConfiguration configuration, LogSource source)
    {
        var options = new LogCaptureOptions();
        configuration.GetSection(LogCaptureOptions.SectionName).Bind(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new LogCaptureIdentity(source));
        services.TryAddSingleton<LogCaptureDataSource>(_ => new LogCaptureDataSource(configuration));
        services.TryAddSingleton<ILogCaptureStore>(sp => new PostgresLogCaptureStore(
            sp.GetRequiredService<LogCaptureDataSource>(),
            sp.GetRequiredService<LogCaptureIdentity>()));

        if (!options.Enabled)
        {
            services.AddHostedService(sp => new LogCaptureHeartbeat(
                sp.GetRequiredService<ILogCaptureStore>(),
                sp.GetRequiredService<LogCaptureIdentity>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<LogCaptureHeartbeat>>()));
            return services;
        }

        services.TryAddSingleton(options);
        services.TryAddSingleton(new LogCaptureBuffer());
        services.TryAddSingleton(new LogCaptureDropCounters());
        services.TryAddSingleton(new LogCaptureCoverage());
        services.TryAddSingleton(sp => new LogCaptureBudget(options.DailyEntryBudget, sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(sp => new LogsAppendedNotifier(sp, sp.GetRequiredService<TimeProvider>()));

        // The provider's constructor is internal, hence the factory. The container disposes it at
        // shutdown, which closes the capture (Dispose reaches Close).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, LogCaptureProvider>(sp => new LogCaptureProvider(
            sp.GetRequiredService<LogCaptureBuffer>(),
            sp.GetRequiredService<LogCaptureBudget>(),
            sp.GetRequiredService<LogCaptureDropCounters>(),
            sp.GetRequiredService<LogCaptureCoverage>(),
            sp.GetRequiredService<TimeProvider>())));

        services.AddHostedService(sp => new LogCaptureWriter(
            sp.GetRequiredService<LogCaptureBuffer>(),
            sp.GetRequiredService<LogCaptureDropCounters>(),
            sp.GetRequiredService<LogCaptureCoverage>(),
            sp.GetRequiredService<LogCaptureIdentity>(),
            sp.GetRequiredService<ILogCaptureStore>(),
            sp.GetRequiredService<LogsAppendedNotifier>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<LogCaptureWriter>>()));
        return services;
    }
}
