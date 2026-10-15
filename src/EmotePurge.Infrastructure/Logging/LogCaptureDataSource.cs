using Microsoft.Extensions.Configuration;
using Npgsql;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// The capture writer's own <see cref="NpgsqlDataSource"/>, built from <c>ConnectionStrings:DefaultConnection</c>
/// <b>without</b> a logger factory, so Npgsql logs nothing for the writer's path (recursion layer 1), with a
/// pool of its own (at most <see cref="MaxPoolSize"/> connections, application name
/// <see cref="ApplicationName"/>) so the writer never competes with the application's pool. The connect
/// timeout is the store's command timeout: an unreachable server fails an attempt after 5 s instead of
/// Npgsql's default 15. Built on first use, so a missing or broken connection string surfaces as a failed
/// write (which the writer and the heartbeat swallow), never as a failed host start.
/// </summary>
internal sealed class LogCaptureDataSource : IDisposable, IAsyncDisposable
{
    internal const string ApplicationName = "emotepurge-logcapture";
    internal const int MaxPoolSize = 2;
    internal const int DefaultConnectTimeoutSeconds = 5;

    private readonly Lazy<NpgsqlDataSource> _dataSource;

    public LogCaptureDataSource(IConfiguration configuration)
        : this(configuration.GetConnectionString("DefaultConnection") ?? string.Empty)
    {
    }

    internal LogCaptureDataSource(string connectionString, int connectTimeoutSeconds = DefaultConnectTimeoutSeconds)
    {
        // Built on first use, which happens inside the writer's suppression scope; built without flowing that
        // context, so the pool's long-lived timers never carry the flag.
        _dataSource = new Lazy<NpgsqlDataSource>(
            () => LogCaptureSuppression.WithoutFlow(() => Build(connectionString, connectTimeoutSeconds)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public NpgsqlDataSource DataSource => _dataSource.Value;

    public void Dispose()
    {
        if (_dataSource.IsValueCreated)
        {
            _dataSource.Value.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource.IsValueCreated)
        {
            await _dataSource.Value.DisposeAsync();
        }
    }

    private static NpgsqlDataSource Build(string connectionString, int connectTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured; the log capture cannot write.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = MaxPoolSize,
            ApplicationName = ApplicationName,
            Timeout = connectTimeoutSeconds,
        };

        // Deliberately no UseLoggerFactory: this data source must not log (spec 5.4, layer 1).
        return new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
    }
}
