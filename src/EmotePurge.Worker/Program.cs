using EmotePurge.Core.Services;
using EmotePurge.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWorkerCore(builder.Configuration);
builder.Services.AddWorkerHostedServices(builder.Configuration);

var host = builder.Build();

// S3-34 fail-fast, same guard as the Api: without it a worker against a stale schema boots,
// reports healthy, and fails only when the first flush or sync touches the missing column.
await using (var migrationScope = host.Services.CreateAsyncScope())
{
    await migrationScope.ServiceProvider.GetRequiredService<IPendingMigrationGuard>()
        .EnsureNoPendingMigrationsAsync();
}

await host.RunAsync();
