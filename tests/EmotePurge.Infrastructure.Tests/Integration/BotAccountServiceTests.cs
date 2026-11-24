using System.Text;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using EmotePurge.Infrastructure.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The bot account's admin writer (#487, plan 2.2, 2.10, T4) against real Postgres in a database of its own, so the
// audit log holds only what these tests wrote. AK73's effects (status, one nudge, one audit entry without the token),
// the versions (Revision 3 and 4), the fingerprint, the idempotent delete, the kill switch upsert and the races of two
// token writes at once. The sealer is the real one with a generated key; the opener only reads seals back.
[Collection("Postgres")]
public class BotAccountServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "bot_account_service_tests";

    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;

    private static readonly AuditActor Actor = new("900001", "someadmin");

    private readonly HandWoundTimeProvider _clock = new();
    private readonly IRedisPublisher _publisher = Substitute.For<IRedisPublisher>();
    private readonly RsaBotAccountTokenSealer _sealer = new(BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey));

    private DateTime Now => _clock.Now.UtcDateTime;

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
        await reset.Database.ExecuteSqlRawAsync("""TRUNCATE "BotAccountCredentials", "AuditLogEntries";""");
    }

    public Task DisposeAsync()
    {
        _sealer.Dispose();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------------ PUT /token

    // AK73, valid: unverified, sealed for the worker, the versions one up, the fingerprint of the key in use, one audit
    // entry with the expiry only, one nudge without payload. What the old token left on the row is cleared; the kill
    // switch, the pause and the runner epoch stay.
    [Fact]
    public async Task SetToken_Valid_StoresTheSealUnverified_WithOneAuditEntryAndOneNudge()
    {
        var pausedUntil = Now.AddMinutes(5);
        await SeedAsync(new BotAccountCredential
        {
            SealedToken = "v1.old",
            Status = BotAccountStatus.Invalid,
            BotSevenTvUserId = "01OLDBOT",
            LastVerifiedAtUtc = Now.AddDays(-1),
            LastErrorCode = "unauthorized",
            KillSwitch = true,
            KillSwitchVersion = 3,
            PausedUntilUtc = pausedUntil,
            CredentialVersion = 4,
            IdentityVersion = 2,
            RunnerEpoch = 7,
        });
        var expires = Now.AddDays(30);
        var token = Jwt(expires);

        var outcome = await Sut().SetTokenAsync(token, Actor);

        Assert.Equal(BotAccountTokenSetOutcome.Set, outcome);
        var row = await LoadAsync();
        Assert.Equal(BotAccountStatus.Unverified, row.Status);
        Assert.Equal(token, Open(row.SealedToken));
        Assert.Equal((expires, Now), (row.ExpiresAtUtc, row.SetAtUtc));
        Assert.Equal(((string?)null, (DateTime?)null, (string?)null), (row.BotSevenTvUserId, row.LastVerifiedAtUtc, row.LastErrorCode));
        Assert.Equal((5, 3), (row.CredentialVersion, row.IdentityVersion));
        Assert.Equal(_sealer.KeyFingerprint, row.TokenKeyFingerprint);
        Assert.Equal((true, 3L, 7L), (row.KillSwitch, row.KillSwitchVersion, row.RunnerEpoch));
        Assert.Null(row.PausedUntilUtc);

        var entry = Assert.Single(await AuditAsync());
        Assert.Equal((AuditActions.BotAccountTokenSet, Actor.TwitchUserId, Actor.Login, (string?)null), (entry.Action, entry.ActorTwitchUserId, entry.ActorLogin, entry.ChannelName));
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        Assert.Equal(["expiresAtUtc"], details.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(expires, details.RootElement.GetProperty("expiresAtUtc").GetDateTime());
        Assert.DoesNotContain(token, entry.DetailsJson!, StringComparison.Ordinal);
        Assert.DoesNotContain(row.SealedToken!, entry.DetailsJson!, StringComparison.Ordinal);

        await AssertOneNudgeAsync();
    }

    // The first token of an installation creates the row (no seed row, T1).
    [Fact]
    public async Task SetToken_WithoutARow_CreatesIt_WithVersionsAtOne()
    {
        var outcome = await Sut().SetTokenAsync(Jwt(Now.AddDays(30)), Actor);

        Assert.Equal(BotAccountTokenSetOutcome.Set, outcome);
        var row = await LoadAsync();
        Assert.Equal((BotAccountStatus.Unverified, 1, 1, false, 0L), (row.Status, row.CredentialVersion, row.IdentityVersion, row.KillSwitch, row.KillSwitchVersion));
    }

    // AK73, refusals: nothing written, audited or announced, and an existing token stays untouched.
    [Theory]
    [InlineData(null, BotAccountTokenSetOutcome.Malformed)]
    [InlineData("", BotAccountTokenSetOutcome.Malformed)]
    [InlineData("not-a-jwt", BotAccountTokenSetOutcome.Malformed)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJib3QifQ.c2ln", BotAccountTokenSetOutcome.Malformed)]
    [InlineData("expired", BotAccountTokenSetOutcome.Expired)]
    [InlineData("expires-now", BotAccountTokenSetOutcome.Expired)]
    public async Task SetToken_Refused_WritesNothing(string? token, BotAccountTokenSetOutcome expected)
    {
        await SeedAsync(new BotAccountCredential { SealedToken = "v1.kept", Status = BotAccountStatus.Valid, CredentialVersion = 1, IdentityVersion = 1 });
        token = token switch
        {
            "expired" => Jwt(Now.AddSeconds(-1)),
            "expires-now" => Jwt(Now),
            _ => token,
        };

        var outcome = await Sut().SetTokenAsync(token, Actor);

        Assert.Equal(expected, outcome);
        var row = await LoadAsync();
        Assert.Equal(("v1.kept", BotAccountStatus.Valid, 1, 1), (row.SealedToken, row.Status, row.CredentialVersion, row.IdentityVersion));
        Assert.Empty(await AuditAsync());
        Assert.Empty(_publisher.ReceivedCalls());
    }

    [Fact]
    public async Task SetToken_WithTheSwitchOff_IsDisabled_WithoutTouchingTheSealer()
    {
        var sealer = Substitute.For<IBotAccountTokenSealer>();

        var outcome = await Sut(sealer: sealer, enabled: false).SetTokenAsync(Jwt(Now.AddDays(30)), Actor);

        Assert.Equal(BotAccountTokenSetOutcome.Disabled, outcome);
        Assert.Empty(sealer.ReceivedCalls());
        Assert.Null(await TryLoadAsync());
    }

    // Revision 3 edge case: two PUT /token at once. They meet at the row lock, so both run, the later one wins, each
    // writes its own audit entry and the versions move by two. The lock is held by a third transaction until both
    // writes are waiting on it, so they really overlap.
    [Fact]
    public async Task TwoConcurrentSetTokens_BothCommit_LastWins_VersionsUpByTwo()
    {
        await SeedAsync(new BotAccountCredential { CredentialVersion = 10, IdentityVersion = 4 });
        var first = Jwt(Now.AddDays(30), subject: "first");
        var second = Jwt(Now.AddDays(29), subject: "second");

        await using var holder = CreateDb();
        Task<BotAccountTokenSetOutcome> a, b;
        await using (var transaction = await holder.Database.BeginTransactionAsync())
        {
            await holder.Database.ExecuteSqlRawAsync("""SELECT 1 FROM "BotAccountCredentials" WHERE "Id" = 1 FOR UPDATE""");
            a = Task.Run(() => Sut().SetTokenAsync(first, Actor));
            b = Task.Run(() => Sut().SetTokenAsync(second, Actor));
            await Task.Delay(500);
            Assert.False(a.IsCompleted || b.IsCompleted);
            await transaction.CommitAsync();
        }

        Assert.Equal([BotAccountTokenSetOutcome.Set, BotAccountTokenSetOutcome.Set], await Task.WhenAll(a, b));
        var row = await LoadAsync();
        Assert.Equal((12, 6), (row.CredentialVersion, row.IdentityVersion));
        var stored = Open(row.SealedToken);
        Assert.Contains(stored, new[] { first, second });

        // The row carries the expiry of the token it holds: seal and expiry come from the same (last) write.
        Assert.Equal(stored == first ? Now.AddDays(30) : Now.AddDays(29), row.ExpiresAtUtc);
        Assert.Equal(2, (await AuditAsync()).Count(e => e.Action == AuditActions.BotAccountTokenSet));
        await _publisher.Received(2).PublishAsync(BotCommands.Channel, BotCommands.BotAccountPrefix, Arg.Any<CancellationToken>());
    }

    // The nudge is an accelerator: a Redis failure after the commit does not turn the stored token into an error.
    [Fact]
    public async Task SetToken_WhenThePublishFails_StillSucceeds()
    {
        _publisher.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var outcome = await Sut().SetTokenAsync(Jwt(Now.AddDays(30)), Actor);

        Assert.Equal(BotAccountTokenSetOutcome.Set, outcome);
        Assert.Equal(BotAccountStatus.Unverified, (await LoadAsync()).Status);
    }

    // ------------------------------------------------------------------------------------ DELETE /token

    [Fact]
    public async Task DeleteToken_ClearsTheTokenFields_KeepsTheKillSwitch_VersionsUpByOne()
    {
        await SeedAsync(new BotAccountCredential
        {
            SealedToken = "v1.gone",
            ExpiresAtUtc = Now.AddDays(10),
            SetAtUtc = Now.AddDays(-20),
            Status = BotAccountStatus.Valid,
            BotSevenTvUserId = "01BOT",
            LastVerifiedAtUtc = Now.AddHours(-1),
            LastErrorCode = "x",
            TokenKeyFingerprint = "abc",
            KillSwitch = true,
            KillSwitchVersion = 1,
            PausedUntilUtc = Now.AddMinutes(5),
            CredentialVersion = 3,
            IdentityVersion = 2,
            RunnerEpoch = 5,
        });

        var deleted = await Sut().DeleteTokenAsync(Actor);

        Assert.True(deleted);
        var row = await LoadAsync();
        Assert.Equal(BotAccountStatus.None, row.Status);
        Assert.Null(row.SealedToken);
        Assert.Null(row.ExpiresAtUtc);
        Assert.Null(row.SetAtUtc);
        Assert.Null(row.BotSevenTvUserId);
        Assert.Null(row.LastVerifiedAtUtc);
        Assert.Null(row.LastErrorCode);
        Assert.Null(row.TokenKeyFingerprint);
        Assert.Null(row.PausedUntilUtc);
        Assert.Equal((true, 1L, 4, 3, 5L), (row.KillSwitch, row.KillSwitchVersion, row.CredentialVersion, row.IdentityVersion, row.RunnerEpoch));

        var entry = Assert.Single(await AuditAsync());
        Assert.Equal((AuditActions.BotAccountTokenDelete, (string?)null), (entry.Action, entry.DetailsJson));
        await AssertOneNudgeAsync();
    }

    // [E] plan T4: no row is a 204 without an audit entry, and the delete creates no row.
    [Fact]
    public async Task DeleteToken_WithoutARow_DoesNothing()
    {
        var deleted = await Sut().DeleteTokenAsync(Actor);

        Assert.False(deleted);
        Assert.Null(await TryLoadAsync());
        Assert.Empty(await AuditAsync());
        Assert.Empty(_publisher.ReceivedCalls());
    }

    // A row without a token (the kill switch was set first) has nothing to delete either; a repeated delete is a no-op.
    [Fact]
    public async Task DeleteToken_OnARowWithoutAToken_DoesNothing()
    {
        await SeedAsync(new BotAccountCredential { KillSwitch = true, KillSwitchVersion = 1, CredentialVersion = 2, IdentityVersion = 2 });

        var deleted = await Sut().DeleteTokenAsync(Actor);

        Assert.False(deleted);
        var row = await LoadAsync();
        Assert.Equal((2, 2, true), (row.CredentialVersion, row.IdentityVersion, row.KillSwitch));
        Assert.Empty(await AuditAsync());
    }

    // ------------------------------------------------------------------------------------ PUT /kill-switch

    // Plan T4: the kill switch without a token row is an upsert; only KillSwitchVersion moves.
    [Fact]
    public async Task KillSwitch_WithoutARow_CreatesIt_OnlyTheKillSwitchVersionMoves()
    {
        var changed = await Sut().SetKillSwitchAsync(true, Actor);

        Assert.True(changed);
        var row = await LoadAsync();
        Assert.Equal((true, 1L, 0, 0, BotAccountStatus.None, (string?)null), (row.KillSwitch, row.KillSwitchVersion, row.CredentialVersion, row.IdentityVersion, row.Status, row.SealedToken));
        var entry = Assert.Single(await AuditAsync());
        Assert.Equal(AuditActions.BotAccountKillSwitch, entry.Action);
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        Assert.Equal(["enabled"], details.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.True(details.RootElement.GetProperty("enabled").GetBoolean());
        await AssertOneNudgeAsync();
    }

    // Revision 4: on, off, on is +3 and three entries; the same value again changes nothing and records nothing.
    [Fact]
    public async Task KillSwitch_OnOffOn_IsPlusThree_AndTheSameValueIsUnchanged()
    {
        await SeedAsync(new BotAccountCredential { SealedToken = "v1.kept", Status = BotAccountStatus.Valid, KillSwitchVersion = 5, CredentialVersion = 2, IdentityVersion = 2 });
        var sut = Sut();

        Assert.True(await sut.SetKillSwitchAsync(true, Actor));
        Assert.True(await sut.SetKillSwitchAsync(false, Actor));
        Assert.True(await sut.SetKillSwitchAsync(true, Actor));
        Assert.False(await sut.SetKillSwitchAsync(true, Actor));

        var row = await LoadAsync();
        Assert.Equal((true, 8L, 2, 2, "v1.kept", BotAccountStatus.Valid), (row.KillSwitch, row.KillSwitchVersion, row.CredentialVersion, row.IdentityVersion, row.SealedToken, row.Status));
        Assert.Equal(3, (await AuditAsync()).Count(e => e.Action == AuditActions.BotAccountKillSwitch));
        await _publisher.Received(3).PublishAsync(BotCommands.Channel, BotCommands.BotAccountPrefix, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KillSwitch_OffWithoutARow_IsUnchanged()
    {
        Assert.False(await Sut().SetKillSwitchAsync(false, Actor));

        Assert.Equal((false, 0L), ((await TryLoadAsync())?.KillSwitch ?? false, (await TryLoadAsync())?.KillSwitchVersion ?? 0L));
        Assert.Empty(await AuditAsync());
        Assert.Empty(_publisher.ReceivedCalls());
    }

    // ------------------------------------------------------------------------------------ GET /

    [Fact]
    public async Task Get_WithoutARow_ReadsAsNone()
    {
        var state = await Sut().GetAsync();

        Assert.Equal(new BotAccountStateDto(false, BotAccountStatus.None, null, null, null, null, null, false, null), state);
    }

    // The read carries the fields of spec K4 and nothing a token or seal could be found in (AK74, admin part):
    // serialized after a real PUT, neither value appears.
    [Fact]
    public async Task Get_AfterSetToken_ShowsTheState_AndNeitherTokenNorSeal()
    {
        var token = Jwt(Now.AddDays(30).AddHours(5));
        await Sut().SetTokenAsync(token, Actor);
        var seal = (await LoadAsync()).SealedToken!;

        var state = await Sut().GetAsync();

        Assert.Equal((true, BotAccountStatus.Unverified, 30), (state.TokenPresent, state.Status, state.ExpiresInDays));
        var json = JsonSerializer.Serialize(state);
        Assert.DoesNotContain(token, json, StringComparison.Ordinal);
        Assert.DoesNotContain(seal, json, StringComparison.Ordinal);
        Assert.DoesNotContain(seal[..40], json, StringComparison.Ordinal);
    }

    // Whole days, rounded down, never below zero.
    [Theory]
    [InlineData(-90, 0)]
    [InlineData(0, 0)]
    [InlineData(60 * 23, 0)]
    [InlineData(60 * 24, 1)]
    [InlineData(60 * 24 * 7 - 1, 6)]
    public async Task Get_ExpiresInDays_CountsWholeDays(int minutes, int expected)
    {
        await SeedAsync(new BotAccountCredential { SealedToken = "v1.x", Status = BotAccountStatus.Valid, ExpiresAtUtc = Now.AddMinutes(minutes) });

        Assert.Equal(expected, (await Sut().GetAsync()).ExpiresInDays);
    }

    // ------------------------------------------------------------------------------------ helpers

    private AppDbContext CreateDb() => fixture.CreateDbContext(DatabaseName);

    private BotAccountService Sut(IBotAccountTokenSealer? sealer = null, bool enabled = true) =>
        new(
            CreateDb(),
            sealer ?? _sealer,
            new BotAccountOptions { Enabled = enabled },
            _publisher,
            _clock,
            NullLogger<BotAccountService>.Instance);

    private async Task SeedAsync(BotAccountCredential row)
    {
        await using var db = CreateDb();
        db.BotAccountCredentials.Add(row);
        await db.SaveChangesAsync();
    }

    private async Task<BotAccountCredential?> TryLoadAsync()
    {
        await using var db = CreateDb();
        return await db.BotAccountCredentials.AsNoTracking().SingleOrDefaultAsync();
    }

    private async Task<BotAccountCredential> LoadAsync() => await TryLoadAsync() ?? throw new InvalidOperationException("No row.");

    private async Task<List<AuditLogEntry>> AuditAsync()
    {
        await using var db = CreateDb();
        return await db.AuditLogEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    private async Task AssertOneNudgeAsync()
    {
        await _publisher.Received(1).PublishAsync(BotCommands.Channel, "BOTACCOUNT:", Arg.Any<CancellationToken>());
        Assert.Single(_publisher.ReceivedCalls());
    }

    private static string? Open(string? seal)
    {
        using var opener = new RsaBotAccountTokenOpener(BotAccountTokenKeys.ImportPrivateKey(BotAccountTestKeys.PrivateKey));
        return opener.TryOpen(seal);
    }

    private static string Jwt(DateTime expiresAt, string subject = "bot")
    {
        var exp = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();
        return $"{B64("""{"alg":"HS256","typ":"JWT"}""")}.{B64($$"""{"sub":"{{subject}}","exp":{{exp}}}""")}.c2lnbmF0dXJl";
    }

    private static string B64(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
