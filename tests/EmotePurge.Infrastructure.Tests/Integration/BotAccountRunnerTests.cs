using System.Text;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using EmotePurge.Infrastructure.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The bot account's runner (#487, plan 2.5, 2.5a (g), 2.8, T5) against real Postgres in a database of its own, so the
// advisory lock and the audit log hold only what these tests did. The opener is the real one with a generated key, the
// 7TV client a substitute whose verification a test can hold open; the wish part is a substitute that records what it got.
// Covered: the token upkeep with its conditional bookings (rotation, delete, swap and 429 during a held verification,
// S5-9), the lock (a second instance, a terminated lock connection mid-read), the runner epoch fence (R2-3), and the
// session's budget, write spacing and pause.
[Collection("Postgres")]
public class BotAccountRunnerTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "bot_account_runner_tests";

    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;

    private static readonly DateTimeOffset StartAt = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(15);

    private readonly FakeTimeProvider _clock = new(StartAt);
    private readonly RsaBotAccountTokenSealer _sealer = new(BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey));
    private readonly RsaBotAccountTokenOpener _opener = new(BotAccountTokenKeys.ImportPrivateKey(BotAccountTestKeys.PrivateKey));
    private readonly List<IAsyncDisposable> _disposables = [];

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

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

                await using var migrate = CreateDb();
                await migrate.Database.MigrateAsync();
                _databaseReady = true;
            }
        }
        finally
        {
            DatabaseGate.Release();
        }

        await using var reset = CreateDb();
        await TerminateLockHoldersAsync(reset);
        await reset.Database.ExecuteSqlRawAsync("""TRUNCATE "BotAccountCredentials", "AuditLogEntries";""");
    }

    public async Task DisposeAsync()
    {
        for (var i = _disposables.Count - 1; i >= 0; i--)
        {
            await _disposables[i].DisposeAsync();
        }

        _sealer.Dispose();
        _opener.Dispose();
    }

    // ------------------------------------------------------------------------------------ Token upkeep

    [Fact]
    public async Task Unverified_VerifiedOk_BecomesValid_WithBotIdAndTheVerifiedVersion_WithoutAudit()
    {
        var token = Jwt(Now.AddDays(30), "a");
        await SeedAsync(BotAccountStatus.Unverified, token, Now.AddDays(30), credentialVersion: 4);
        var client = ClientAnswering(SevenTvWriteResult<string>.Ok("bot-a"));
        var (runner, pass) = CreateRunner(client);

        Assert.Equal(BotAccountPassResult.Completed, await runner.RunPassAsync());

        var row = await ReadRowAsync();
        Assert.Equal(BotAccountStatus.Valid, row.Status);
        Assert.Equal("bot-a", row.BotSevenTvUserId);
        Assert.Equal(Now, row.LastVerifiedAtUtc);
        Assert.Equal(4, row.LastVerifiedCredentialVersion);
        Assert.Null(row.LastErrorCode);
        Assert.Empty(await TokenStateAuditsAsync());
        await client.Received(1).VerifyAsync(token, Arg.Any<CancellationToken>());

        // The wish part ran with the opened token, the account as booked and this runner's epoch.
        var session = Assert.Single(pass.Sessions);
        Assert.Equal(token, session.Token);
        Assert.Equal(BotAccountStatus.Valid, session.Account.Status);
        Assert.Equal(row.RunnerEpoch, session.RunnerEpoch);
        Assert.True(session.RunnerEpoch > 0);
    }

    [Fact]
    public async Task Unauthorized_MakesTheTokenInvalid_WithOneAuditEntryWithoutTheToken()
    {
        var token = Jwt(Now.AddDays(30), "a");
        await SeedAsync(BotAccountStatus.Unverified, token, Now.AddDays(30));
        var (runner, pass) = CreateRunner(ClientAnswering(SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Unauthorized())));

        await runner.RunPassAsync();

        var row = await ReadRowAsync();
        Assert.Equal(BotAccountStatus.Invalid, row.Status);
        Assert.Equal(BotAccountErrorCodes.TokenUnauthorized, row.LastErrorCode);
        var audit = Assert.Single(await TokenStateAuditsAsync());
        Assert.Equal(AuditActor.System.Login, audit.ActorLogin);
        Assert.Equal(new Dictionary<string, string> { ["status"] = "invalid", ["errorCode"] = "token_unauthorized" }, Details(audit));
        Assert.DoesNotContain(token, audit.DetailsJson, StringComparison.Ordinal);
        Assert.Null(Assert.Single(pass.Sessions).Token);
    }

    [Fact]
    public async Task ASealTheKeyCannotOpen_IsInvalid_WithoutCallingSevenTv()
    {
        await SeedRawAsync(BotAccountStatus.Unverified, "v1.not-a-seal", Now.AddDays(30));
        var client = Substitute.For<ISevenTvWriteClient>();
        var (runner, _) = CreateRunner(client);

        await runner.RunPassAsync();

        var row = await ReadRowAsync();
        Assert.Equal(BotAccountStatus.Invalid, row.Status);
        Assert.Equal(BotAccountErrorCodes.SealUnreadable, row.LastErrorCode);
        Assert.Single(await TokenStateAuditsAsync());
        await client.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);
    }

    [Theory]
    [InlineData(BotAccountStatus.Unverified)]
    [InlineData(BotAccountStatus.Valid)]
    public async Task ExpiryExactlyNow_IsExpired_WithOneAudit_WithoutOpeningOrCallingSevenTv(string status)
    {
        await SeedAsync(status, Jwt(Now.AddDays(1), "a"), expiresAtUtc: Now, lastVerifiedAtUtc: Now.AddMinutes(-1), lastVerifiedCredentialVersion: 1);
        var client = Substitute.For<ISevenTvWriteClient>();
        var opener = new CountingOpener(_opener);
        var (runner, pass) = CreateRunner(client, opener);

        await runner.RunPassAsync();

        Assert.Equal(BotAccountStatus.Expired, (await ReadRowAsync()).Status);
        var audit = Assert.Single(await TokenStateAuditsAsync());
        Assert.Equal(new Dictionary<string, string> { ["status"] = "expired" }, Details(audit));
        Assert.Equal(0, opener.Calls);
        await client.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);
        Assert.Null(Assert.Single(pass.Sessions).Token);
    }

    // Revision 4: invalid stays until an admin acts, also with exp in the past; expired stays until a new token or a swap.
    [Theory]
    [InlineData(BotAccountStatus.Invalid)]
    [InlineData(BotAccountStatus.Expired)]
    public async Task InvalidAndExpired_StayAsTheyAre_EvenWithExpInThePast(string status)
    {
        await SeedAsync(status, Jwt(Now.AddDays(-1), "a"), expiresAtUtc: Now.AddDays(-1));
        var client = Substitute.For<ISevenTvWriteClient>();
        var opener = new CountingOpener(_opener);
        var (runner, _) = CreateRunner(client, opener);

        Assert.Equal(BotAccountPassResult.Completed, await runner.RunPassAsync());

        Assert.Equal(status, (await ReadRowAsync()).Status);
        Assert.Empty(await TokenStateAuditsAsync());
        Assert.Equal(0, opener.Calls);
        await client.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);
    }

    [Fact]
    public async Task Valid_IsVerifiedAgainAfterSixHours_NotBefore()
    {
        var token = Jwt(Now.AddDays(30), "a");
        await SeedAsync(BotAccountStatus.Valid, token, Now.AddDays(30), lastVerifiedAtUtc: Now.AddHours(-5), lastVerifiedCredentialVersion: 1, botSevenTvUserId: "bot-a");
        var client = ClientAnswering(SevenTvWriteResult<string>.Ok("bot-a"));
        var (runner, pass) = CreateRunner(client);

        await runner.RunPassAsync();
        await client.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);
        Assert.Equal(token, pass.Sessions[^1].Token);

        _clock.Advance(TimeSpan.FromHours(1));
        await runner.RunPassAsync();

        await client.Received(1).VerifyAsync(token, Arg.Any<CancellationToken>());
        Assert.Equal(Now, (await ReadRowAsync()).LastVerifiedAtUtc);
    }

    // Plan 2.8: after every Unauthorized of a write the next pass verifies again; that verification decides.
    [Fact]
    public async Task AReportedUnauthorized_ForcesTheNextVerification()
    {
        var token = Jwt(Now.AddDays(30), "a");
        await SeedAsync(BotAccountStatus.Valid, token, Now.AddDays(30), lastVerifiedAtUtc: Now, lastVerifiedCredentialVersion: 1, botSevenTvUserId: "bot-a");
        var client = ClientAnswering(SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Unauthorized()));
        var (runner, pass) = CreateRunner(client);
        pass.OnRun = session => session.ReportUnauthorized();

        await runner.RunPassAsync();
        await client.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);

        pass.OnRun = null;
        await runner.RunPassAsync();

        await client.Received(1).VerifyAsync(token, Arg.Any<CancellationToken>());
        Assert.Equal(BotAccountStatus.Invalid, (await ReadRowAsync()).Status);
    }

    [Fact]
    public async Task KillSwitchOrARunningPause_HoldTheVerification()
    {
        await SeedAsync(BotAccountStatus.Unverified, Jwt(Now.AddDays(30), "a"), Now.AddDays(30), killSwitch: true);
        var client = ClientAnswering(SevenTvWriteResult<string>.Ok("bot-a"));
        var (runner, _) = CreateRunner(client);

        await runner.RunPassAsync();
        await client.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);

        await using (var db = CreateDb())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "BotAccountCredentials" SET "KillSwitch" = false, "PausedUntilUtc" = {Now.AddMinutes(1)}""");
        }

        await runner.RunPassAsync();
        await client.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await runner.RunPassAsync();
        await client.ReceivedWithAnyArgs(1).VerifyAsync(default!, default);
        Assert.Equal(BotAccountStatus.Valid, (await ReadRowAsync()).Status);
    }

    [Fact]
    public async Task AnUnclearVerification_BooksNothing_AndIsTriedAgainNextPass()
    {
        await SeedAsync(BotAccountStatus.Unverified, Jwt(Now.AddDays(30), "a"), Now.AddDays(30));
        var client = Substitute.For<ISevenTvWriteClient>();
        client.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Unavailable()), SevenTvWriteResult<string>.Ok("bot-a"));
        var (runner, _) = CreateRunner(client);

        await runner.RunPassAsync();
        Assert.Equal(BotAccountStatus.Unverified, (await ReadRowAsync()).Status);

        await runner.RunPassAsync();
        Assert.Equal(BotAccountStatus.Valid, (await ReadRowAsync()).Status);
        Assert.Empty(await TokenStateAuditsAsync());
    }

    // Sol-P1-3: a PUT during a held verification of token A. A's late answer — Ok or 401 — never touches token B.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RotationDuringAHeldVerification_LeavesTheNewTokenUnverified_AndTheNextPassChecksIt(bool answerOk)
    {
        var tokenA = Jwt(Now.AddDays(30), "a");
        var tokenB = Jwt(Now.AddDays(30), "b");
        await SeedAsync(BotAccountStatus.Unverified, tokenA, Now.AddDays(30));
        var held = new HeldVerification();
        var client = Substitute.For<ISevenTvWriteClient>();
        client.VerifyAsync(tokenA, Arg.Any<CancellationToken>()).Returns(_ => held.EnterAsync());
        client.VerifyAsync(tokenB, Arg.Any<CancellationToken>()).Returns(SevenTvWriteResult<string>.Ok("bot-b"));
        var (runner, _) = CreateRunner(client);

        var pass = runner.RunPassAsync();
        await held.Entered.WaitAsync(HoldTimeout);
        Assert.Equal(BotAccountTokenSetOutcome.Set, await SetTokenThroughTheAdminRouteAsync(tokenB));
        held.Release(answerOk ? SevenTvWriteResult<string>.Ok("bot-a") : SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Unauthorized()));
        Assert.Equal(BotAccountPassResult.Completed, await pass.WaitAsync(HoldTimeout));

        var row = await ReadRowAsync();
        Assert.Equal(BotAccountStatus.Unverified, row.Status);
        Assert.Null(row.BotSevenTvUserId);
        Assert.Null(row.LastVerifiedCredentialVersion);
        Assert.Empty(await TokenStateAuditsAsync());

        await runner.RunPassAsync();

        row = await ReadRowAsync();
        Assert.Equal(BotAccountStatus.Valid, row.Status);
        Assert.Equal("bot-b", row.BotSevenTvUserId);
        Assert.Equal(row.CredentialVersion, row.LastVerifiedCredentialVersion);
        await client.Received(1).VerifyAsync(tokenB, Arg.Any<CancellationToken>());
    }

    // Sol-P1-3: a DELETE during a held verification. The late Ok leaves the row without a token.
    [Fact]
    public async Task DeleteDuringAHeldVerification_LeavesTheRowWithoutAToken()
    {
        await SeedAsync(BotAccountStatus.Unverified, Jwt(Now.AddDays(30), "a"), Now.AddDays(30));
        var held = new HeldVerification();
        var client = Substitute.For<ISevenTvWriteClient>();
        client.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => held.EnterAsync());
        var (runner, _) = CreateRunner(client);

        var pass = runner.RunPassAsync();
        await held.Entered.WaitAsync(HoldTimeout);
        await using (var db = CreateDb())
        {
            Assert.True(await CreateAdminService(db).DeleteTokenAsync(new AuditActor("900001", "someadmin")));
        }

        held.Release(SevenTvWriteResult<string>.Ok("bot-a"));
        await pass.WaitAsync(HoldTimeout);

        var row = await ReadRowAsync();
        Assert.Equal(BotAccountStatus.None, row.Status);
        Assert.Null(row.SealedToken);
        Assert.Null(row.BotSevenTvUserId);
        Assert.Empty(await TokenStateAuditsAsync());
    }

    // Revision 4: a renewal swap (CredentialVersion + 1, same IdentityVersion, valid, LastVerifiedAtUtc empty). The next
    // pass opens the new seal and verifies it once; the status stays valid, nothing falls back.
    [Fact]
    public async Task ASwap_IsOpenedAndVerifiedOnce_AndStaysValid()
    {
        var tokenA = Jwt(Now.AddDays(30), "a");
        var tokenB = Jwt(Now.AddDays(60), "b");
        await SeedAsync(BotAccountStatus.Valid, tokenA, Now.AddDays(30), lastVerifiedAtUtc: Now.AddHours(-1), lastVerifiedCredentialVersion: 1, botSevenTvUserId: "bot-a");
        var client = ClientAnswering(SevenTvWriteResult<string>.Ok("bot-a"));
        var (runner, pass) = CreateRunner(client);
        await runner.RunPassAsync();
        Assert.Equal(tokenA, pass.Sessions[^1].Token);

        await SwapAsync(tokenB, Now.AddDays(60));
        await runner.RunPassAsync();
        await runner.RunPassAsync();

        await client.Received(1).VerifyAsync(tokenB, Arg.Any<CancellationToken>());
        await client.DidNotReceive().VerifyAsync(tokenA, Arg.Any<CancellationToken>());
        var row = await ReadRowAsync();
        Assert.Equal(BotAccountStatus.Valid, row.Status);
        Assert.Equal(2, row.LastVerifiedCredentialVersion);
        Assert.Equal(tokenB, pass.Sessions[^1].Token);
        Assert.Empty(await TokenStateAuditsAsync());
    }

    [Fact]
    public async Task ASwap_WhoseTokenIsUnauthorized_IsInvalid()
    {
        var tokenB = Jwt(Now.AddDays(60), "b");
        await SeedAsync(BotAccountStatus.Valid, Jwt(Now.AddDays(30), "a"), Now.AddDays(30), lastVerifiedAtUtc: Now, lastVerifiedCredentialVersion: 1, botSevenTvUserId: "bot-a");
        var (runner, _) = CreateRunner(ClientAnswering(SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Unauthorized())));
        await runner.RunPassAsync();

        await SwapAsync(tokenB, Now.AddDays(60));
        await runner.RunPassAsync();

        Assert.Equal(BotAccountStatus.Invalid, (await ReadRowAsync()).Status);
        Assert.Single(await TokenStateAuditsAsync());
    }

    // S5-9: LastVerifiedCredentialVersion stays on the old value after the swap until the new token is verified; a late
    // verification of token A writes nothing at all.
    [Fact]
    public async Task ALateVerificationOfTheOldToken_AfterASwap_WritesNothing()
    {
        var tokenA = Jwt(Now.AddDays(30), "a");
        var tokenB = Jwt(Now.AddDays(60), "b");
        await SeedAsync(BotAccountStatus.Valid, tokenA, Now.AddDays(30), lastVerifiedAtUtc: Now.AddHours(-7), lastVerifiedCredentialVersion: 1, botSevenTvUserId: "bot-a");
        var held = new HeldVerification();
        var client = Substitute.For<ISevenTvWriteClient>();
        client.VerifyAsync(tokenA, Arg.Any<CancellationToken>()).Returns(_ => held.EnterAsync());
        client.VerifyAsync(tokenB, Arg.Any<CancellationToken>()).Returns(SevenTvWriteResult<string>.Ok("bot-a"));
        var (runner, _) = CreateRunner(client);

        var pass = runner.RunPassAsync();
        await held.Entered.WaitAsync(HoldTimeout);
        await SwapAsync(tokenB, Now.AddDays(60));
        held.Release(SevenTvWriteResult<string>.Ok("bot-a"));
        await pass.WaitAsync(HoldTimeout);

        var row = await ReadRowAsync();
        Assert.Equal(1, row.LastVerifiedCredentialVersion);
        Assert.Null(row.LastVerifiedAtUtc);
        Assert.Equal(BotAccountStatus.Valid, row.Status);

        await runner.RunPassAsync();
        Assert.Equal(2, (await ReadRowAsync()).LastVerifiedCredentialVersion);
    }

    // Revision 4: the pause after a 429 belongs to the account (IdentityVersion) and survives a swap; a new token set by an
    // admin (IdentityVersion + 1) does not inherit it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A429OfTheOldToken_PausesTheAccountAcrossASwap_ButNotAcrossANewToken(bool swap)
    {
        var tokenA = Jwt(Now.AddDays(30), "a");
        var tokenB = Jwt(Now.AddDays(60), "b");
        await SeedAsync(BotAccountStatus.Unverified, tokenA, Now.AddDays(30));
        var held = new HeldVerification();
        var client = Substitute.For<ISevenTvWriteClient>();
        client.VerifyAsync(tokenA, Arg.Any<CancellationToken>()).Returns(_ => held.EnterAsync());
        var (runner, _) = CreateRunner(client);

        var pass = runner.RunPassAsync();
        await held.Entered.WaitAsync(HoldTimeout);
        if (swap)
        {
            await SwapAsync(tokenB, Now.AddDays(60));
        }
        else
        {
            await SetTokenThroughTheAdminRouteAsync(tokenB);
        }

        held.Release(SevenTvWriteResult<string>.Failed(SevenTvWriteResult.RateLimited(TimeSpan.FromSeconds(90))));
        await pass.WaitAsync(HoldTimeout);

        Assert.Equal(swap ? Now.AddSeconds(90) : (DateTime?)null, (await ReadRowAsync()).PausedUntilUtc);
    }

    // A pause booked for the old identity is gone after a hand-set token (PUT), and survives a renewal swap.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABookedPause_IsGoneAfterAPut_ButSurvivesARenewalSwap(bool swap)
    {
        var tokenA = Jwt(Now.AddDays(30), "a");
        var tokenB = Jwt(Now.AddDays(60), "b");
        await SeedAsync(BotAccountStatus.Unverified, tokenA, Now.AddDays(30));
        await using (var db = CreateDb())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "BotAccountCredentials" SET "PausedUntilUtc" = {Now.AddSeconds(90)}""");
        }

        if (swap)
        {
            await SwapAsync(tokenB, Now.AddDays(60));
        }
        else
        {
            await SetTokenThroughTheAdminRouteAsync(tokenB);
        }

        Assert.Equal(swap ? Now.AddSeconds(90) : (DateTime?)null, (await ReadRowAsync()).PausedUntilUtc);
    }

    // AK: at most one warning per UTC day from seven days before exp, again right after midnight.
    [Fact]
    public async Task TheExpiryWarning_ComesOncePerUtcDay_AndAgainAfterMidnight()
    {
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 24, 23, 59, 0, TimeSpan.Zero));
        await SeedAsync(BotAccountStatus.Valid, Jwt(Now.AddDays(3), "a"), Now.AddDays(3), lastVerifiedAtUtc: Now, lastVerifiedCredentialVersion: 1, botSevenTvUserId: "bot-a");
        var logger = new RecordingLogger<BotAccountRunner>();
        var (runner, _) = CreateRunner(Substitute.For<ISevenTvWriteClient>(), logger: logger);

        await runner.RunPassAsync();
        _clock.Advance(TimeSpan.FromSeconds(59));
        await runner.RunPassAsync();
        Assert.Equal(1, ExpiryWarnings(logger));

        _clock.Advance(TimeSpan.FromSeconds(1));
        await runner.RunPassAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await runner.RunPassAsync();
        Assert.Equal(2, ExpiryWarnings(logger));
    }

    // AK75 in the runner's own lines: no token, no seal, not even a prefix of either.
    [Fact]
    public async Task TheRunnersLogs_NameNeitherTokenNorSeal()
    {
        var token = Jwt(Now.AddDays(3), "a");
        var seal = await SeedAsync(BotAccountStatus.Unverified, token, Now.AddDays(3));
        var logger = new RecordingLogger<BotAccountRunner>();
        var client = Substitute.For<ISevenTvWriteClient>();
        client.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Timeout()), SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Unauthorized()));
        var (runner, _) = CreateRunner(client, logger: logger);

        await runner.RunPassAsync();
        await runner.RunPassAsync();

        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, e =>
        {
            Assert.DoesNotContain(token.Split('.')[1], e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(seal[..20], e.Message, StringComparison.Ordinal);
        });
    }

    // ------------------------------------------------------------------------------------ Lock and epoch

    // AK81, lock part: a second instance without the lock runs only the deadline step; it opens nothing, calls 7TV for
    // nothing and hands the wish part nothing.
    [Fact]
    public async Task ASecondInstance_WithoutTheLock_RunsOnlyTheDeadlineStep()
    {
        await SeedAsync(BotAccountStatus.Unverified, Jwt(Now.AddDays(30), "a"), Now.AddDays(30));
        var (first, _) = CreateRunner(ClientAnswering(SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Unavailable())));
        Assert.Equal(BotAccountPassResult.Completed, await first.RunPassAsync());

        var client = Substitute.For<ISevenTvWriteClient>();
        var opener = new CountingOpener(_opener);
        var (second, secondPass) = CreateRunner(client, opener);

        Assert.Equal(BotAccountPassResult.NoLock, await second.RunPassAsync());
        Assert.Equal(BotAccountPassResult.NoLock, await second.RunPassAsync());

        Assert.Equal(2, secondPass.DeadlineSteps);
        Assert.Empty(secondPass.Sessions);
        Assert.Equal(0, opener.Calls);
        await client.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);
    }

    // Sol-P1-4: the first runner's lock connection is terminated while its 7TV read is held; the second takes the lock
    // over, and the first books nothing from the answer it gets afterwards.
    [Fact]
    public async Task ALockLostDuringAHeldRead_BooksNothing_AndTheSecondRunnerTakesOver()
    {
        await SeedAsync(BotAccountStatus.Unverified, Jwt(Now.AddDays(30), "a"), Now.AddDays(30));
        var held = new HeldVerification();
        var firstClient = Substitute.For<ISevenTvWriteClient>();
        firstClient.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => held.EnterAsync());
        var (first, firstPass) = CreateRunner(firstClient);

        var pass = first.RunPassAsync();
        await held.Entered.WaitAsync(HoldTimeout);
        var firstEpoch = (await ReadRowAsync()).RunnerEpoch;
        await using (var killer = CreateDb())
        {
            Assert.Equal(1, await TerminateLockHoldersAsync(killer));
        }

        var secondClient = ClientAnswering(SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Unavailable()));
        var (second, secondPass) = CreateRunner(secondClient);
        Assert.Equal(BotAccountPassResult.Completed, await second.RunPassAsync());
        Assert.Equal(firstEpoch + 1, (await ReadRowAsync()).RunnerEpoch);

        held.Release(SevenTvWriteResult<string>.Ok("bot-a"));
        Assert.Equal(BotAccountPassResult.LockLost, await pass.WaitAsync(HoldTimeout));

        var row = await ReadRowAsync();
        Assert.Equal(BotAccountStatus.Unverified, row.Status);
        Assert.Null(row.BotSevenTvUserId);
        Assert.Empty(firstPass.Sessions);
        Assert.Single(secondPass.Sessions);

        // Lost stays lost: the first one does not get it back while the second holds it.
        Assert.Equal(BotAccountPassResult.NoLock, await first.RunPassAsync());
    }

    // R2-3: the first runner's transaction with the epoch fence is still open when the second takes the lock. The second
    // waits with its raise until the first commits; after that every fence on the first epoch fails, also on an intact
    // working connection.
    [Fact]
    public async Task TheEpochRaise_WaitsForAnOpenFence_AndAfterwardsTheOldEpochIsFencedOut()
    {
        var (first, _) = CreateRunner(Substitute.For<ISevenTvWriteClient>());
        Assert.Equal(BotAccountPassResult.Completed, await first.RunPassAsync());
        var firstEpoch = (await ReadRowAsync()).RunnerEpoch;
        Assert.Equal(1, firstEpoch);

        await using var working = CreateDb();
        await using (var transaction = await working.Database.BeginTransactionAsync())
        {
            Assert.NotNull(await working.LockBotAccountForShareAsync(firstEpoch, CancellationToken.None));

            await using (var killer = CreateDb())
            {
                Assert.Equal(1, await TerminateLockHoldersAsync(killer));
            }

            var secondDb = fixture.CreateTaggedDbContext("bot-runner-two", DatabaseName);
            _disposables.Add(secondDb);
            var (second, _) = CreateRunner(Substitute.For<ISevenTvWriteClient>(), db: secondDb);
            var secondPass = second.RunPassAsync();

            await fixture.WaitUntilBlockedOnLockAsync("bot-runner-two", secondPass);
            Assert.Contains("RunnerEpoch", await fixture.WaitingQueryAsync("bot-runner-two"), StringComparison.Ordinal);

            await transaction.CommitAsync();
            Assert.Equal(BotAccountPassResult.Completed, await secondPass.WaitAsync(HoldTimeout));
        }

        Assert.Equal(firstEpoch + 1, (await ReadRowAsync()).RunnerEpoch);
        await using (var transaction = await working.Database.BeginTransactionAsync())
        {
            Assert.Null(await working.LockBotAccountForShareAsync(firstEpoch, CancellationToken.None));
            Assert.NotNull(await working.LockBotAccountForShareAsync(firstEpoch + 1, CancellationToken.None));
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task TheFence_NeedsAnOpenTransaction()
    {
        await using var db = CreateDb();

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.LockBotAccountForShareAsync(1, CancellationToken.None));
    }

    // Plan 2.2: only the runner raises RunnerEpoch; a token PUT and the kill switch leave it alone.
    [Fact]
    public async Task TokenPutAndKillSwitch_LeaveTheRunnerEpochAlone()
    {
        var (runner, _) = CreateRunner(Substitute.For<ISevenTvWriteClient>());
        await runner.RunPassAsync();
        var epoch = (await ReadRowAsync()).RunnerEpoch;

        Assert.Equal(BotAccountTokenSetOutcome.Set, await SetTokenThroughTheAdminRouteAsync(Jwt(Now.AddDays(30), "b")));
        await using (var db = CreateDb())
        {
            Assert.True(await CreateAdminService(db).SetKillSwitchAsync(true, new AuditActor("900001", "someadmin")));
        }

        Assert.Equal(epoch, (await ReadRowAsync()).RunnerEpoch);
    }

    // The lock is taken anew with a fresh epoch once a booking found the epoch moved (ReportLockLost).
    [Fact]
    public async Task AReportedLockLoss_EndsThePass_AndTheNextPassTakesTheLockWithANewEpoch()
    {
        var (runner, pass) = CreateRunner(Substitute.For<ISevenTvWriteClient>());
        pass.OnRun = session => session.ReportLockLost();

        Assert.Equal(BotAccountPassResult.LockLost, await runner.RunPassAsync());
        var epoch = (await ReadRowAsync()).RunnerEpoch;

        pass.OnRun = null;
        Assert.Equal(BotAccountPassResult.Completed, await runner.RunPassAsync());
        Assert.Equal(epoch + 1, (await ReadRowAsync()).RunnerEpoch);
        Assert.Equal(epoch + 1, pass.Sessions[^1].RunnerEpoch);
    }

    // ------------------------------------------------------------------------------------ Session

    [Fact]
    public async Task TheSession_HasABudgetOfFifty_AWriteSpacing_AndAPauseThatNeverShrinks()
    {
        await SeedAsync(BotAccountStatus.Valid, Jwt(Now.AddDays(30), "a"), Now.AddDays(30), lastVerifiedAtUtc: Now, lastVerifiedCredentialVersion: 1, botSevenTvUserId: "bot-a");
        var (runner, pass) = CreateRunner(Substitute.For<ISevenTvWriteClient>(), writeDelaySeconds: 2);
        await runner.RunPassAsync();
        var session = Assert.Single(pass.Sessions);

        for (var i = 0; i < BotAccountRunner.ActionBudgetPerPass; i++)
        {
            Assert.True(session.TryTakeAction());
        }

        Assert.False(session.TryTakeAction());
        Assert.Equal(0, session.RemainingActions);

        await session.WaitForWriteSlotAsync();
        var second = session.WaitForWriteSlotAsync();
        Assert.False(second.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(2));
        await second.WaitAsync(HoldTimeout);

        Assert.True(await session.HoldsLockAsync());
        Assert.True(await session.PauseAsync(TimeSpan.FromSeconds(60)));
        var pausedUntil = (await ReadRowAsync()).PausedUntilUtc;
        Assert.True(await session.PauseAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(pausedUntil, (await ReadRowAsync()).PausedUntilUtc);
    }

    [Fact]
    public async Task AFailingDeadlineStep_DoesNotHoldTheRestOfThePass()
    {
        var (runner, pass) = CreateRunner(Substitute.For<ISevenTvWriteClient>());
        pass.DeadlineFailure = new InvalidOperationException("row data");

        Assert.Equal(BotAccountPassResult.Completed, await runner.RunPassAsync());
        Assert.Single(pass.Sessions);
    }

    // ------------------------------------------------------------------------------------ Helpers

    private AppDbContext CreateDb() => fixture.CreateDbContext(DatabaseName);

    private (BotAccountRunner Runner, RecordingWishPass Pass) CreateRunner(
        ISevenTvWriteClient client,
        IBotAccountTokenOpener? opener = null,
        RecordingLogger<BotAccountRunner>? logger = null,
        AppDbContext? db = null,
        int writeDelaySeconds = 0)
    {
        if (db is null)
        {
            db = CreateDb();
            _disposables.Add(db);
        }

        var pass = new RecordingWishPass();
        var runner = new BotAccountRunner(
            db,
            opener ?? _opener,
            client,
            pass,
            new BotAccountOptions { Enabled = true, WriteDelaySeconds = writeDelaySeconds },
            _clock,
            (ILogger<BotAccountRunner>?)logger ?? NullLogger<BotAccountRunner>.Instance);
        _disposables.Add(runner);
        return (runner, pass);
    }

    private BotAccountService CreateAdminService(AppDbContext db) => new(
        db,
        _sealer,
        new BotAccountOptions { Enabled = true },
        Substitute.For<IRedisPublisher>(),
        _clock,
        NullLogger<BotAccountService>.Instance);

    private async Task<BotAccountTokenSetOutcome> SetTokenThroughTheAdminRouteAsync(string token)
    {
        await using var db = CreateDb();
        return await CreateAdminService(db).SetTokenAsync(token, new AuditActor("900001", "someadmin"));
    }

    // The renewal swap as plan 2.13 writes it: a new seal and CredentialVersion + 1, IdentityVersion unchanged, valid,
    // LastVerifiedAtUtc empty (the swap's own route arrives in T16).
    private async Task SwapAsync(string token, DateTime expiresAtUtc)
    {
        var seal = _sealer.Seal(token);
        await using var db = CreateDb();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE "BotAccountCredentials" SET "SealedToken" = {seal}, "ExpiresAtUtc" = {expiresAtUtc}, "Status" = 'valid',
                 "LastVerifiedAtUtc" = NULL, "CredentialVersion" = "CredentialVersion" + 1
             """);
    }

    private async Task<string> SeedAsync(
        string status,
        string token,
        DateTime? expiresAtUtc,
        int credentialVersion = 1,
        DateTime? lastVerifiedAtUtc = null,
        int? lastVerifiedCredentialVersion = null,
        string? botSevenTvUserId = null,
        bool killSwitch = false)
    {
        var seal = _sealer.Seal(token);
        await SeedRawAsync(status, seal, expiresAtUtc, credentialVersion, lastVerifiedAtUtc, lastVerifiedCredentialVersion, botSevenTvUserId, killSwitch);
        return seal;
    }

    private async Task SeedRawAsync(
        string status,
        string seal,
        DateTime? expiresAtUtc,
        int credentialVersion = 1,
        DateTime? lastVerifiedAtUtc = null,
        int? lastVerifiedCredentialVersion = null,
        string? botSevenTvUserId = null,
        bool killSwitch = false)
    {
        await using var db = CreateDb();
        db.BotAccountCredentials.Add(new BotAccountCredential
        {
            SealedToken = seal,
            ExpiresAtUtc = expiresAtUtc,
            SetAtUtc = Now,
            Status = status,
            CredentialVersion = credentialVersion,
            IdentityVersion = 1,
            LastVerifiedAtUtc = lastVerifiedAtUtc,
            LastVerifiedCredentialVersion = lastVerifiedCredentialVersion,
            BotSevenTvUserId = botSevenTvUserId,
            KillSwitch = killSwitch,
        });
        await db.SaveChangesAsync();
    }

    private async Task<BotAccountCredential> ReadRowAsync()
    {
        await using var db = CreateDb();
        return await db.BotAccountCredentials.AsNoTracking().SingleAsync();
    }

    private async Task<List<AuditLogEntry>> TokenStateAuditsAsync()
    {
        await using var db = CreateDb();
        return await db.AuditLogEntries.AsNoTracking().Where(e => e.Action == AuditActions.BotAccountTokenState).ToListAsync();
    }

    private static async Task<int> TerminateLockHoldersAsync(AppDbContext db) =>
        await db.Database.SqlQuery<int>(
                $"""
                 SELECT count(*)::int AS "Value" FROM (
                     SELECT pg_terminate_backend(pid) FROM pg_locks
                     WHERE locktype = 'advisory' AND granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                 ) AS t
                 """)
            .SingleAsync();

    // jsonb normalises the spacing; compared as values.
    private static Dictionary<string, string> Details(AuditLogEntry entry) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(entry.DetailsJson!)!;

    private static int ExpiryWarnings(RecordingLogger<BotAccountRunner> logger) =>
        logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("expires", StringComparison.Ordinal));

    private static ISevenTvWriteClient ClientAnswering(SevenTvWriteResult<string> result)
    {
        var client = Substitute.For<ISevenTvWriteClient>();
        client.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(result);
        return client;
    }

    private static string Jwt(DateTime expiresAt, string subject)
    {
        var exp = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();
        return $"{B64("""{"alg":"HS256","typ":"JWT"}""")}.{B64($$"""{"sub":"{{subject}}","exp":{{exp}}}""")}.c2lnbmF0dXJl";
    }

    private static string B64(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // A verification the test holds open until it releases an answer.
    private sealed class HeldVerification
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SevenTvWriteResult<string>> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public Task<SevenTvWriteResult<string>> EnterAsync()
        {
            _entered.TrySetResult();
            return _answer.Task;
        }

        public void Release(SevenTvWriteResult<string> answer) => _answer.SetResult(answer);
    }

    private sealed class CountingOpener(IBotAccountTokenOpener inner) : IBotAccountTokenOpener
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string? TryOpen(string? sealedToken)
        {
            Interlocked.Increment(ref _calls);
            return inner.TryOpen(sealedToken);
        }
    }

    private sealed class RecordingWishPass : IWishBotPass
    {
        public List<IBotAccountSession> Sessions { get; } = [];
        public int DeadlineSteps { get; private set; }
        public Action<IBotAccountSession>? OnRun { get; set; }
        public Exception? DeadlineFailure { get; set; }

        public Task RunDeadlineStepAsync(CancellationToken cancellationToken = default)
        {
            DeadlineSteps++;
            return DeadlineFailure is null ? Task.CompletedTask : Task.FromException(DeadlineFailure);
        }

        public Task RunAsync(IBotAccountSession session, CancellationToken cancellationToken = default)
        {
            Sessions.Add(session);
            OnRun?.Invoke(session);
            return Task.CompletedTask;
        }
    }
}
