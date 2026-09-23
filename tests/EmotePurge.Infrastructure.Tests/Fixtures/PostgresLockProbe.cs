using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Fixtures;

/// <summary>
/// What makes a row-lock race deterministic in a test: each contender runs on a context whose
/// connections carry their own <c>application_name</c>, and the test waits on Postgres itself — on
/// <c>pg_stat_activity</c> reporting that backend as waiting on a lock — rather than on a clock. No
/// test sleeps and hopes.
/// </summary>
public static class PostgresLockProbe
{
    /// <summary>
    /// A context whose connections carry <paramref name="applicationName"/>, so
    /// <see cref="WaitUntilBlockedOnLockAsync"/> can find exactly this contender in pg_stat_activity.
    /// </summary>
    public static AppDbContext CreateTaggedDbContext(this PostgresFixture fixture, string applicationName)
    {
        using var probe = fixture.CreateDbContext();
        var connectionString = new NpgsqlConnectionStringBuilder(probe.Database.GetConnectionString())
        {
            ApplicationName = applicationName
        }.ConnectionString;

        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
    }

    /// <summary>
    /// Returns once the backend tagged <paramref name="applicationName"/> is waiting on a lock. Fails
    /// fast if <paramref name="contender"/> finishes instead — then it never blocked, and the ordering
    /// the test depends on did not happen.
    /// </summary>
    public static async Task WaitUntilBlockedOnLockAsync(this PostgresFixture fixture, string applicationName, Task contender)
    {
        await using var probe = fixture.CreateDbContext();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            if (contender.IsCompleted)
            {
                await contender;
                Assert.Fail($"{applicationName} completed without ever waiting on a lock.");
            }

            var waiting = await probe.Database
                .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM pg_stat_activity WHERE application_name = {applicationName} AND wait_event_type = 'Lock'""")
                .SingleAsync();
            if (waiting > 0)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{applicationName} never started waiting on a lock.");
            }

            await Task.Delay(20);
        }
    }
}
