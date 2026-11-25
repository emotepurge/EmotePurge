using System.Text;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Telemetry;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using EmotePurge.Infrastructure.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Unit.SevenTvWriteClientTestData;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// The bot token's trust boundary below the HTTP layer (plan #487 T11a, spec AK74/AK75, Sol-P2-9): the real admin
/// service with the real sealer and the real worker runner with the real write client run against a real Postgres, with
/// every logger at trace level and the real persisted log capture (<c>LogCaptureProvider</c> into the Postgres store)
/// next to a console stand-in. After the paths set (valid, expired, without exp, 9 KB, garbage, missing), delete, a
/// forced exception behind the seal, a failing nudge, and the worker's verify with an ok and with a 401, neither the
/// token (whole or in parts), <c>Bearer</c> nor the seal appear in any formatted line, exception, structured property,
/// stored log row, audit entry, non-token column of the credential row or exception thrown to the caller. The HTTP
/// half (responses, the bind-error and exception middleware) is <c>BotAccountTokenResponseBoundaryTests</c> in Api.Tests.
/// </summary>
[Collection("Postgres")]
public class BotAccountTokenBoundaryTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "bot_account_boundary_tests";

    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;
    private static readonly AuditActor Actor = new("900001", "someadmin");

    public async Task InitializeAsync()
    {
        await DatabaseGate.WaitAsync();
        try
        {
            if (!_databaseReady)
            {
                await using (var admin = fixture.CreateDbContext())
                {
                    // A constant name, not user input; CREATE DATABASE cannot be parameterised.
#pragma warning disable EF1002
                    await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {DatabaseName}");
#pragma warning restore EF1002
                }

                await using var migrate = fixture.CreateDbContext(DatabaseName);
                await migrate.Database.MigrateAsync();
                _databaseReady = true;
            }
        }
        finally
        {
            DatabaseGate.Release();
        }

        await using var reset = fixture.CreateDbContext(DatabaseName);
        await reset.Database.ExecuteSqlRawAsync(
            """TRUNCATE "BotAccountCredentials", "AuditLogEntries", "LogEntries", "LogCaptureStatus";""");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // PUT /token valid, the worker's verify with ok, a second token and the worker's verify with 401, DELETE /token.
    [Fact]
    public async Task SetVerifyOk_SetVerify401_AndDelete_LeakNothingIntoAnySink()
    {
        await using var rig = await Rig.StartAsync(fixture, DatabaseName, [
            Fill("""{"data":{"users":{"me":{"id":"@BOT@"}}}}"""),
            UnauthorizedBody,
        ]);
        var first = Jwt(DateTime.UtcNow.AddDays(30), "bot-one");
        var second = Jwt(DateTime.UtcNow.AddDays(31), "bot-two");

        Assert.Equal(BotAccountTokenSetOutcome.Set, await rig.Admin().SetTokenAsync(first, Actor));
        rig.Watch(first);
        await rig.WatchSealAsync();
        await rig.RunWorkerPassAsync();
        Assert.Equal(BotAccountStatus.Valid, (await rig.ReadCredentialAsync()).Status);

        Assert.Equal(BotAccountTokenSetOutcome.Set, await rig.Admin().SetTokenAsync(second, Actor));
        rig.Watch(second);
        await rig.WatchSealAsync();
        await rig.RunWorkerPassAsync();
        Assert.Equal(BotAccountStatus.Invalid, (await rig.ReadCredentialAsync()).Status);

        Assert.True(await rig.Admin().DeleteTokenAsync(Actor));

        Assert.Equal(2, rig.Handler.Requests.Count);
        Assert.Equal($"Bearer {first}", rig.Handler.Requests[0].Authorization);
        await rig.AssertNothingLeakedAsync(expectLogRows: true);
    }

    // Everything the route can be given and the service refuses before it seals anything.
    [Fact]
    public async Task EveryRefusedInput_LeaksNothingIntoAnySink()
    {
        await using var rig = await Rig.StartAsync(fixture, DatabaseName, []);
        var expired = Jwt(DateTime.UtcNow.AddDays(-1), "expired");
        var noExp = JwtFromPayload("""{"sub":"no-exp"}""");
        var nineKb = JwtFromPayload($$"""{"sub":"{{new string('x', 9 * 1024)}}","exp":{{DateTimeOffset.UtcNow.AddDays(9).ToUnixTimeSeconds()}}}""");
        var garbage = "secret-not-a-jwt-" + Guid.NewGuid().ToString("N");
        var wrongType = """{"token":12345}""";
        var brokenJson = """{"token":"eyJhbGciOiJIUzI1NiJ9.eyJleHAiOjF9.c2ln""";

        foreach (var value in new[] { expired, noExp, nineKb, garbage, wrongType, brokenJson })
        {
            rig.Watch(value);
        }

        Assert.Equal(BotAccountTokenSetOutcome.Expired, await rig.Admin().SetTokenAsync(expired, Actor));
        Assert.Equal(BotAccountTokenSetOutcome.Malformed, await rig.Admin().SetTokenAsync(noExp, Actor));
        Assert.Equal(BotAccountTokenSetOutcome.Malformed, await rig.Admin().SetTokenAsync(nineKb, Actor));
        Assert.Equal(BotAccountTokenSetOutcome.Malformed, await rig.Admin().SetTokenAsync(garbage, Actor));
        Assert.Equal(BotAccountTokenSetOutcome.Malformed, await rig.Admin().SetTokenAsync(wrongType, Actor));
        Assert.Equal(BotAccountTokenSetOutcome.Malformed, await rig.Admin().SetTokenAsync(brokenJson, Actor));
        Assert.Equal(BotAccountTokenSetOutcome.Malformed, await rig.Admin().SetTokenAsync(null, Actor));
        Assert.Equal(BotAccountTokenSetOutcome.Malformed, await rig.Admin().SetTokenAsync(string.Empty, Actor));

        // Refused before the transaction: no row, no audit entry.
        Assert.Null(await rig.TryReadCredentialAsync());
        Assert.Empty(await rig.ReadAuditAsync());
        await rig.AssertNothingLeakedAsync(expectLogRows: false);
    }

    // The forced exception hides behind the seal: the sealed value is built, then the save fails. What reaches the
    // caller, EF Core's trace-level failure lines and the capture must not hold the token or the seal.
    [Fact]
    public async Task AFailureBehindTheSeal_LeaksNothingIntoTheExceptionOrAnySink()
    {
        await using var rig = await Rig.StartAsync(fixture, DatabaseName, []);
        var token = Jwt(DateTime.UtcNow.AddDays(30), "doomed");
        rig.Watch(token);
        var hook = new SaveHook((_, _) => throw new InvalidOperationException("forced failure behind the seal"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Admin(rig.OpenDb([hook])).SetTokenAsync(token, Actor));
        rig.Collect(thrown);

        Assert.Equal(1, hook.Calls);
        Assert.Null((await rig.TryReadCredentialAsync())?.SealedToken);
        await rig.AssertNothingLeakedAsync(expectLogRows: false);
    }

    // A Redis outage after the commit is logged as a warning with the exception type only (the one line of the service
    // that is persisted by the capture), and neither it nor the committed row carries the token.
    [Fact]
    public async Task AFailingNudge_IsCapturedWithoutTheTokenOrTheSeal()
    {
        await using var rig = await Rig.StartAsync(fixture, DatabaseName, []);
        rig.Publisher.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("redis down, was handed nothing secret")));
        var token = Jwt(DateTime.UtcNow.AddDays(30), "nudge");
        rig.Watch(token);

        Assert.Equal(BotAccountTokenSetOutcome.Set, await rig.Admin().SetTokenAsync(token, Actor));
        await rig.WatchSealAsync();

        await rig.AssertNothingLeakedAsync(expectLogRows: true);
        Assert.Contains(await rig.ReadLogRowsAsync(), row => row.Category.EndsWith(nameof(BotAccountService), StringComparison.Ordinal));
    }

    private static string Jwt(DateTime expiresAt, string subject) =>
        JwtFromPayload($$"""{"sub":"{{subject}}","exp":{{new DateTimeOffset(expiresAt).ToUnixTimeSeconds()}}}""");

    private static string JwtFromPayload(string payloadJson) =>
        $"{B64("""{"alg":"HS256","typ":"JWT"}""")}.{B64(payloadJson)}.{B64("signature-" + Guid.NewGuid().ToString("N"))}";

    private static string B64(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class Rig : IAsyncDisposable
    {
        private readonly PostgresFixture _fixture;
        private readonly string _databaseName;
        private readonly Harness _harness;
        private readonly CapturingLoggerProvider _console;
        private readonly RsaBotAccountTokenSealer _sealer = new(BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey));
        private readonly ServiceProvider _worker;
        private readonly List<string> _forbidden = ["Bearer", "Authorization"];
        private readonly List<string> _thrown = [];
        private readonly List<IAsyncDisposable> _disposables = [];

        private Rig(PostgresFixture fixture, string databaseName, Harness harness, CapturingLoggerProvider console, ScriptedHttpHandler handler)
        {
            _fixture = fixture;
            _databaseName = databaseName;
            _harness = harness;
            _console = console;
            Handler = handler;

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotAccount:Enabled"] = "true",
                [BotAccountTokenKeys.PrivateKeyName] = BotAccountTestKeys.PrivateKey,
            }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(harness.Factory);
            services.AddLogging();
            services.AddSingleton<IRateLimitTelemetry>(new RecordingRateLimitTelemetry());
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(new BotAccountOptions { Enabled = true, WriteDelaySeconds = 0 });
            services.AddSingleton(Substitute.For<IWishBotPass>());
            services.AddScoped(_ => CreateDb([]));
            services.AddBotAccountWriter(configuration);
            services.AddHttpClient<ISevenTvWriteClient, SevenTvWriteClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
            _worker = services.BuildServiceProvider();
        }

        public ScriptedHttpHandler Handler { get; }

        public IRedisPublisher Publisher { get; } = Substitute.For<IRedisPublisher>();

        public static async Task<Rig> StartAsync(PostgresFixture fixture, string databaseName, IEnumerable<string> verifyAnswers)
        {
            var console = new CapturingLoggerProvider();
            var harness = new Harness(
                fixture.ConnectionStringFor(databaseName),
                LogCaptureWriterTests.Fast,
                configure: builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(console));
            var handler = new ScriptedHttpHandler();
            foreach (var answer in verifyAnswers)
            {
                if (answer == UnauthorizedBody)
                {
                    handler.Then(System.Net.HttpStatusCode.Unauthorized, answer);
                }
                else
                {
                    handler.ThenOk(answer);
                }
            }

            await harness.StartAsync();
            return new Rig(fixture, databaseName, harness, console, handler);
        }

        public BotAccountService Admin(AppDbContext? db = null) => new(
            db ?? OpenDb([]),
            _sealer,
            new BotAccountOptions { Enabled = true },
            Publisher,
            TimeProvider.System,
            _harness.Factory.CreateLogger<BotAccountService>());

        public AppDbContext OpenDb(IEnumerable<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor> interceptors)
        {
            var db = CreateDb(interceptors);
            _disposables.Add(db);
            return db;
        }

        // Every value that must stay out of every sink: the whole token, its payload and signature segments, and the
        // payload's decoded content would be caught by the segments too.
        public void Watch(string token)
        {
            _forbidden.Add(token);
            var parts = token.Split('.');
            if (parts.Length == 3)
            {
                _forbidden.Add(parts[1]);
                _forbidden.Add(parts[2]);
            }
        }

        // The seal on the row now: the whole value and a stretch from each end.
        public async Task WatchSealAsync()
        {
            var seal = (await ReadCredentialAsync()).SealedToken!;
            _forbidden.Add(seal);
            _forbidden.Add(seal[..24]);
            _forbidden.Add(seal[^24..]);
        }

        public void Collect(Exception exception) => _thrown.Add(exception.ToString());

        public async Task RunWorkerPassAsync()
        {
            await using var scope = _worker.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IBotAccountRunner>().RunPassAsync();
        }

        public async Task<BotAccountCredential> ReadCredentialAsync() =>
            await TryReadCredentialAsync() ?? throw new InvalidOperationException("No credential row.");

        public async Task<BotAccountCredential?> TryReadCredentialAsync()
        {
            await using var db = _fixture.CreateDbContext(_databaseName);
            return await db.BotAccountCredentials.AsNoTracking().SingleOrDefaultAsync();
        }

        public async Task<List<AuditLogEntry>> ReadAuditAsync()
        {
            await using var db = _fixture.CreateDbContext(_databaseName);
            return await db.AuditLogEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        }

        public async Task<List<LogEntry>> ReadLogRowsAsync()
        {
            await using var db = _fixture.CreateDbContext(_databaseName);
            return await db.LogEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        }

        public async Task AssertNothingLeakedAsync(bool expectLogRows)
        {
            // Everything buffered is in the store before the rows are read.
            await _harness.StopAsync();

            var sinks = new List<(string Name, string Text)>();
            foreach (var line in _console.Lines)
            {
                sinks.Add(($"console {line.Category} {line.Level}", line.AllText));
            }

            var rows = await ReadLogRowsAsync();
            foreach (var row in rows)
            {
                sinks.Add(($"stored log row {row.Id} {row.Category}", JsonSerializer.Serialize(row)));
            }

            foreach (var entry in await ReadAuditAsync())
            {
                sinks.Add(($"audit entry {entry.Id} {entry.Action}", JsonSerializer.Serialize(entry)));
            }

            // The credential row without the seal column itself, which is the one place the seal belongs.
            if (await TryReadCredentialAsync() is { } credential)
            {
                credential.SealedToken = null;
                sinks.Add(("credential row", JsonSerializer.Serialize(credential)));
            }

            foreach (var text in _thrown)
            {
                sinks.Add(("thrown exception", text));
            }

            // A refused input logs nothing at all, which is the strongest form of the claim; the paths that do log must
            // have reached the console stand-in at trace level, not just the captured information-and-above lines.
            if (expectLogRows)
            {
                Assert.NotEmpty(rows);
                Assert.Contains(_console.Lines, line => line.Level < LogLevel.Information);
            }

            foreach (var (name, text) in sinks)
            {
                foreach (var value in _forbidden)
                {
                    Assert.False(
                        text.Contains(value, StringComparison.OrdinalIgnoreCase),
                        $"{name} contains a forbidden value ({value.Length} characters, starting {value[..Math.Min(8, value.Length)]}).");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _harness.DisposeAsync();
            await _worker.DisposeAsync();
            foreach (var disposable in _disposables)
            {
                await disposable.DisposeAsync();
            }

            _sealer.Dispose();
        }

        private AppDbContext CreateDb(IEnumerable<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor> interceptors)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(_fixture.ConnectionStringFor(_databaseName))
                .UseLoggerFactory(_harness.Factory)
                .AddInterceptors(interceptors)
                .Options;
            return new AppDbContext(options);
        }
    }
}
