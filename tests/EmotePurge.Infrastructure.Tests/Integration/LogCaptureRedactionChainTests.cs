using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using EmotePurge.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// Acceptance criterion 4 through the whole chain, deterministically: the Worker's redacting wrapper in
/// front of a real logger factory that holds both the capture sink and a console stand-in, the real
/// writer and the real Postgres store. Every fixture of <see cref="RedactionChainFixtures"/> is logged once
/// and must leave no identity, id or chat text in the stored row or (except for the deliberately bypassed
/// wrapper) in the console line.
/// </summary>
[Collection("Postgres")]
public class LogCaptureRedactionChainTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "log_capture_chain_tests";
    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;

    public async Task InitializeAsync()
    {
        await DatabaseGate.WaitAsync();
        try
        {
            if (!_databaseReady)
            {
                await using (var admin = fixture.CreateDbContext())
                {
#pragma warning disable EF1002 // constant name, CREATE DATABASE cannot be parameterized
                    await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {DatabaseName}");
#pragma warning restore EF1002
                }

                await using var db = fixture.CreateDbContext(DatabaseName);
                await db.Database.MigrateAsync();
                _databaseReady = true;
            }
        }
        finally
        {
            DatabaseGate.Release();
        }

        await using var reset = fixture.CreateDbContext(DatabaseName);
        await reset.Database.ExecuteSqlRawAsync("TRUNCATE \"LogEntries\", \"LogCaptureStatus\";");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [MemberData(nameof(RedactionChainFixtures.Names), MemberType = typeof(RedactionChainFixtures))]
    public async Task Ak4Fixture_ThroughRealChain_LeaksNothing(string name)
    {
        var chainFixture = RedactionChainFixtures.All[name];
        var console = new CapturingLoggerProvider();
        await using var harness = new Harness(fixture.ConnectionStringFor(DatabaseName), LogCaptureWriterTests.Fast, configure: builder => builder.AddProvider(console));
        ILoggerFactory factory = chainFixture.ThroughWrapper ? new RedactingTwitchClientLoggerFactory(harness.Factory) : harness.Factory;
        await harness.StartAsync();

        chainFixture.Log(factory.CreateLogger(chainFixture.Category));

        var row = await WaitForSingleRowAsync(chainFixture.Category);
        await harness.StopAsync();

        // Nothing identifying in any stored field.
        foreach (var forbidden in RedactionChainFixtures.Forbidden)
        {
            Assert.DoesNotContain(forbidden, row.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, row.Exception ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, row.ExceptionType ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, row.Properties ?? string.Empty, StringComparison.Ordinal);
        }

        Assert.Equal((LogEntryLevel)(short)chainFixture.Level, row.Level);
        Assert.Equal(chainFixture.EventName, row.EventName);

        // ...and nothing in the console stand-in (rendered message, exception.ToString(), state values).
        var line = Assert.Single(console.Lines, l => l.Category == chainFixture.Category);
        if (chainFixture.ConsoleChecked)
        {
            foreach (var forbidden in RedactionChainFixtures.Forbidden)
            {
                Assert.DoesNotContain(forbidden, line.AllText, StringComparison.Ordinal);
            }
        }

        switch (chainFixture.Shape)
        {
            case RedactionChainFixtures.Shape.RedactedLine:
                Assert.StartsWith($"TwitchLib.{chainFixture.EventName} (redacted, #246): command=", row.Message, StringComparison.Ordinal);
                Assert.Contains(", tagKeys=[", row.Message, StringComparison.Ordinal);
                Assert.Null(row.Properties);
                Assert.Equal(chainFixture.WithheldTagKeys, row.Message.EndsWith(", withheldTagKeys=1", StringComparison.Ordinal));
                break;
            case RedactionChainFixtures.Shape.SpliceWarning:
                Assert.Contains("<withheld>", row.Message, StringComparison.Ordinal);
                using (var properties = JsonDocument.Parse(row.Properties!))
                {
                    Assert.Contains("<withheld>", properties.RootElement.GetProperty("tagblock").GetString(), StringComparison.Ordinal);
                    Assert.Equal("<entfernt>", properties.RootElement.GetProperty("roomid").GetString());
                }

                break;
        }

        if (chainFixture.WithException)
        {
            Assert.Equal(typeof(InvalidOperationException).FullName, row.ExceptionType);
            Assert.Contains(typeof(InvalidOperationException).FullName!, row.Exception, StringComparison.Ordinal);
            Assert.Contains(typeof(FormatException).FullName!, row.Exception, StringComparison.Ordinal);
            Assert.Contains("   at ", row.Exception, StringComparison.Ordinal);
            Assert.DoesNotContain("outer:", row.Exception, StringComparison.Ordinal);
            Assert.DoesNotContain("inner:", row.Exception, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(row.ExceptionType);
            Assert.Null(row.Exception);
        }
    }

    [Fact]
    public void Fixtures_CoverEveryCaseOfTheAcceptanceCriterion()
    {
        Assert.Equal(8, RedactionChainFixtures.All.Count);
        Assert.Single(RedactionChainFixtures.All.Values, f => !f.ThroughWrapper);
        Assert.Single(RedactionChainFixtures.All.Values, f => f.Shape == RedactionChainFixtures.Shape.SpliceWarning);
        Assert.Equal(4, RedactionChainFixtures.All.Values.Count(f => f.WithException));
    }

    private async Task<LogEntry> WaitForSingleRowAsync(string category)
    {
        LogEntry? row = null;
        await LogCaptureWriterTests.Eventually(async () =>
        {
            await using var db = fixture.CreateDbContext(DatabaseName);
            row = await db.LogEntries.AsNoTracking().SingleOrDefaultAsync(e => e.Category == category);
            return row is not null;
        });
        return row!;
    }
}
