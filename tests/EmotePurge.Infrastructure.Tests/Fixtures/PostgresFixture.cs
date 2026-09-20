using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.PostgreSql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Fixtures;

// One real postgres:16-alpine container (same image as docker-compose.yml) shared across all
// tests in the "Postgres" collection, migrated once via the project's real EF Core migrations.
// Deliberately not EF Core InMemory: UsageStatQueryService/VoteSessionQueryService rely on
// GroupBy+Sum translations that InMemory evaluates client-side and would never catch a real
// Npgsql translation failure (see the comment in UsageStatQueryService.GetUsageTotalsAsync).
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("emotepurge")
        .WithUsername("emotepurge")
        .WithPassword("emotepurge-test")
        .Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    // Fresh AppDbContext per call — tests get their own change tracker against the same
    // migrated database rather than sharing one long-lived context instance.
    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        return new AppDbContext(options);
    }

    // Same container and database, with EF Core interceptors wired in — for tests that need to
    // inject a failure at a specific point in a service's own SaveChangesAsync sequence (e.g.
    // ChannelEmoteSetObservationServiceTests' transaction-rollback case) without touching the
    // production code under test.
    public AppDbContext CreateDbContext(IEnumerable<IInterceptor> interceptors)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .AddInterceptors(interceptors)
            .Options;

        return new AppDbContext(options);
    }

    // Same container, different database — for tests that need a schema state other than
    // "fully migrated" (e.g. PendingMigrationGuardTests). The database must already exist.
    public AppDbContext CreateDbContext(string databaseName)
    {
        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = databaseName
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options);
    }
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
