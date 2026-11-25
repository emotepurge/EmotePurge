using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The account-wide availability (#487, plan 2.3, T4) against the migrated head schema: switch, status, expiry, kill
// switch, and the pause that deliberately does not count. Shares the head-schema database with BotAccountSchemaTests,
// so every test clears the one row first and last.
[Collection("Postgres")]
public class BotAccountAvailabilityTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ClearSql = """DELETE FROM "BotAccountCredentials";""";

    private readonly HandWoundTimeProvider _clock = new();

    private DateTime Now => _clock.Now.UtcDateTime;

    public async Task InitializeAsync() => await ClearAsync();

    public async Task DisposeAsync() => await ClearAsync();

    [Fact]
    public async Task Valid_WithAFutureExpiry_AndNoKillSwitch_IsAvailable()
    {
        await SeedAsync(BotAccountStatus.Valid, Now.AddDays(3));

        Assert.True(await Sut().IsAvailableAsync());
    }

    // Plan T4: a valid status with an exp in the past is not available (the worker may not have written expired yet).
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Valid_WithAnExpiryNotInTheFuture_IsNotAvailable(int seconds)
    {
        await SeedAsync(BotAccountStatus.Valid, Now.AddSeconds(seconds));

        Assert.False(await Sut().IsAvailableAsync());
    }

    // Plan T4: a pause after a 429 is not unavailability.
    [Fact]
    public async Task Valid_DuringAPause_IsStillAvailable()
    {
        await SeedAsync(BotAccountStatus.Valid, Now.AddDays(3), pausedUntil: Now.AddHours(1));

        Assert.True(await Sut().IsAvailableAsync());
    }

    [Fact]
    public async Task Valid_WithTheKillSwitch_IsNotAvailable()
    {
        await SeedAsync(BotAccountStatus.Valid, Now.AddDays(3), killSwitch: true);

        Assert.False(await Sut().IsAvailableAsync());
    }

    [Theory]
    [InlineData(BotAccountStatus.None)]
    [InlineData(BotAccountStatus.Unverified)]
    [InlineData(BotAccountStatus.Invalid)]
    [InlineData(BotAccountStatus.Expired)]
    public async Task AnyOtherStatus_IsNotAvailable(string status)
    {
        await SeedAsync(status, Now.AddDays(3));

        Assert.False(await Sut().IsAvailableAsync());
    }

    [Fact]
    public async Task Valid_WithoutAnExpiry_IsNotAvailable()
    {
        await SeedAsync(BotAccountStatus.Valid, expiresAt: null);

        Assert.False(await Sut().IsAvailableAsync());
    }

    [Fact]
    public async Task WithoutARow_IsNotAvailable() => Assert.False(await Sut().IsAvailableAsync());

    // The switch of the own process wins over a perfectly valid row, and is decided before any read: a context whose
    // database does not exist is never touched.
    [Fact]
    public async Task SwitchOff_IsNotAvailable_WithoutReadingTheRow()
    {
        await SeedAsync(BotAccountStatus.Valid, Now.AddDays(3));
        await using var unreachable = fixture.CreateDbContext("database_that_does_not_exist");

        Assert.False(await new BotAccountAvailability(unreachable, new BotAccountOptions { Enabled = false }, _clock).IsAvailableAsync());
        Assert.False(await Sut(enabled: false).IsAvailableAsync());
    }

    // K6b-5: one read gives the answer, the bot's 7TV id and the identity version; the ids pass through also when the
    // account is not usable (the per-channel state needs them only together with the answer).
    [Theory]
    [InlineData(BotAccountStatus.Valid, false, true)]
    [InlineData(BotAccountStatus.Valid, true, false)]
    [InlineData(BotAccountStatus.Invalid, false, false)]
    public async Task GetState_IsOneReadOfAnswerIdAndIdentity(string status, bool killSwitch, bool expected)
    {
        await SeedAsync(status, Now.AddDays(3), killSwitch: killSwitch, botId: "01BOTACCOUNT", identityVersion: 6);
        var hook = new CommandHook(_ => false, _ => Task.CompletedTask);
        await using var db = fixture.CreateTaggedDbContext("bot-availability-state", [hook]);

        var state = await new BotAccountAvailability(db, new BotAccountOptions { Enabled = true }, _clock).GetStateAsync();

        Assert.Equal(new BotAccountAvailabilityState(expected, "01BOTACCOUNT", 6), state);
        Assert.Equal(1, hook.Count(c => c.Contains("\"BotAccountCredentials\"", StringComparison.Ordinal)));
        Assert.Equal(expected, await Sut().IsAvailableAsync());
    }

    [Fact]
    public async Task GetState_WithoutARow_IsUnavailable() =>
        Assert.Equal(BotAccountAvailabilityState.Unavailable, await Sut().GetStateAsync());

    // The switch off answers before any read, also for the state.
    [Fact]
    public async Task GetState_SwitchOff_IsUnavailable_WithoutReadingTheRow()
    {
        await SeedAsync(BotAccountStatus.Valid, Now.AddDays(3), botId: "01BOTACCOUNT", identityVersion: 6);
        await using var unreachable = fixture.CreateDbContext("database_that_does_not_exist");

        Assert.Equal(
            BotAccountAvailabilityState.Unavailable,
            await new BotAccountAvailability(unreachable, new BotAccountOptions { Enabled = false }, _clock).GetStateAsync());
    }

    private BotAccountAvailability Sut(bool enabled = true) =>
        new(fixture.CreateDbContext(), new BotAccountOptions { Enabled = enabled }, _clock);

    private async Task SeedAsync(
        string status, DateTime? expiresAt, bool killSwitch = false, DateTime? pausedUntil = null, string? botId = null, int identityVersion = 0)
    {
        await using var db = fixture.CreateDbContext();
        db.BotAccountCredentials.Add(new BotAccountCredential
        {
            SealedToken = "v1.x",
            Status = status,
            ExpiresAtUtc = expiresAt,
            KillSwitch = killSwitch,
            PausedUntilUtc = pausedUntil,
            BotSevenTvUserId = botId,
            IdentityVersion = identityVersion,
        });
        await db.SaveChangesAsync();
    }

    private async Task ClearAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(ClearSql);
    }
}
