using System.Security.Cryptography;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Real Postgres + real UserService/cipher; only ITwitchAuthClient is faked — the actual Twitch
// token endpoint is deliberately exercised live (Regel 16), not against an HTTP stub.
[Collection("Postgres")]
public class TwitchUserTokenServiceTests(PostgresFixture fixture)
{
    private readonly AesGcmTokenCipher _cipher = CreateCipher();

    // The token paths never touch the role cache — a substitute keeps this class on the Postgres
    // fixture alone instead of dragging a Redis container in for a dependency it does not exercise.
    private readonly IModRoleCache _modRoleCache = Substitute.For<IModRoleCache>();

    private static AesGcmTokenCipher CreateCipher()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Twitch:TokenEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            })
            .Build();
        return new AesGcmTokenCipher(configuration);
    }

    private TwitchUserTokenService CreateService(
        AppDbContext db, ITwitchAuthClient authClient, TwitchTokenRefreshGate gate)
    {
        return new TwitchUserTokenService(
            new UserService(db, _cipher, _modRoleCache), authClient, gate, NullLogger<TwitchUserTokenService>.Instance);
    }

    private async Task SeedUserWithTokensAsync(string userId, DateTime accessTokenExpiresAtUtc, string? scopes = null)
    {
        await using var db = fixture.CreateDbContext();
        var userService = new UserService(db, _cipher, _modRoleCache);
        await userService.UpsertLoginAsync(userId, userId, userId);
        await userService.StoreTwitchTokensAsync(
            userId, "stored-access", accessTokenExpiresAtUtc, "stored-refresh",
            scopes ?? TwitchOAuthDefaults.RequestedScopes);
    }

    [Fact]
    public async Task ValidClaimToken_IsServedDirectly_WithoutRefresh()
    {
        var authClient = Substitute.For<ITwitchAuthClient>();
        authClient.ValidateTokenAsync("claim-token", Arg.Any<CancellationToken>()).Returns((bool?)true);
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, authClient, new TwitchTokenRefreshGate());

        var result = await service.GetValidAccessTokenAsync(new TwitchPrincipalInfo("uts-claim", "uts-claim", "claim-token"));

        Assert.Equal("claim-token", result.AccessToken);
        Assert.False(result.ReauthRequired);
        await authClient.DidNotReceive().RefreshUserTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExpiredClaim_WithFreshStoredToken_ServesStoredTokenWithoutRefresh()
    {
        await SeedUserWithTokensAsync("uts-stored", DateTime.UtcNow.AddHours(2));
        var authClient = Substitute.For<ITwitchAuthClient>();
        authClient.ValidateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((bool?)true);
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, authClient, new TwitchTokenRefreshGate());

        var result = await service.GetValidAccessTokenAsync(new TwitchPrincipalInfo("uts-stored", "uts-stored", null));

        Assert.Equal("stored-access", result.AccessToken);
        Assert.False(result.ReauthRequired);
        await authClient.DidNotReceive().RefreshUserTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExpiredStoredToken_IsRefreshed_AndRotatedRefreshTokenIsPersisted()
    {
        await SeedUserWithTokensAsync("uts-refresh", DateTime.UtcNow.AddMinutes(-5));
        var authClient = Substitute.For<ITwitchAuthClient>();
        authClient.RefreshUserTokenAsync("stored-refresh", Arg.Any<CancellationToken>())
            .Returns(new TwitchTokenRefreshResult(TwitchRefreshStatus.Success,
                new TwitchTokenResult("new-access", DateTime.UtcNow.AddHours(4), "rotated-refresh", TwitchOAuthDefaults.RequestedScopes)));
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, authClient, new TwitchTokenRefreshGate());

        var result = await service.GetValidAccessTokenAsync(new TwitchPrincipalInfo("uts-refresh", "uts-refresh", null));

        Assert.Equal("new-access", result.AccessToken);
        Assert.False(result.ReauthRequired);

        await using var verifyDb = fixture.CreateDbContext();
        var stored = await new UserService(verifyDb, _cipher, _modRoleCache).GetTwitchTokensAsync("uts-refresh");
        Assert.NotNull(stored);
        Assert.Equal("rotated-refresh", stored.RefreshToken);
        Assert.Equal("new-access", stored.AccessToken);
    }

    [Fact]
    public async Task ParallelRequests_TriggerExactlyOneRefresh()
    {
        await SeedUserWithTokensAsync("uts-flight", DateTime.UtcNow.AddMinutes(-5));
        var refreshCalls = 0;
        var authClient = Substitute.For<ITwitchAuthClient>();
        authClient.RefreshUserTokenAsync("stored-refresh", Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref refreshCalls);
                await Task.Delay(50);
                return new TwitchTokenRefreshResult(TwitchRefreshStatus.Success,
                    new TwitchTokenResult("new-access", DateTime.UtcNow.AddHours(4), "rotated-refresh", TwitchOAuthDefaults.RequestedScopes));
            });

        // One gate (the process-wide singleton), but a service+DbContext per caller — exactly how
        // parallel scoped requests look in production.
        var gate = new TwitchTokenRefreshGate();
        var principal = new TwitchPrincipalInfo("uts-flight", "uts-flight", null);
        var callers = Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var db = fixture.CreateDbContext();
            return await CreateService(db, authClient, gate).GetValidAccessTokenAsync(principal);
        });

        var results = await Task.WhenAll(callers);

        Assert.Equal(1, refreshCalls);
        Assert.All(results, r => Assert.Equal("new-access", r.AccessToken));
    }

    [Fact]
    public async Task InvalidGrant_ClearsStoredTokens_AndReportsReauthRequired()
    {
        await SeedUserWithTokensAsync("uts-invalid", DateTime.UtcNow.AddMinutes(-5));
        var authClient = Substitute.For<ITwitchAuthClient>();
        authClient.RefreshUserTokenAsync("stored-refresh", Arg.Any<CancellationToken>())
            .Returns(new TwitchTokenRefreshResult(TwitchRefreshStatus.InvalidGrant, null));
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, authClient, new TwitchTokenRefreshGate());

        var result = await service.GetValidAccessTokenAsync(new TwitchPrincipalInfo("uts-invalid", "uts-invalid", null));

        Assert.Null(result.AccessToken);
        Assert.True(result.ReauthRequired);
        await using var verifyDb = fixture.CreateDbContext();
        Assert.Null(await new UserService(verifyDb, _cipher, _modRoleCache).GetTwitchTokensAsync("uts-invalid"));
    }

    [Fact]
    public async Task TransientRefreshFailure_KeepsStoredTokens_AndIsNotReauthRequired()
    {
        await SeedUserWithTokensAsync("uts-transient", DateTime.UtcNow.AddMinutes(-5));
        var authClient = Substitute.For<ITwitchAuthClient>();
        authClient.RefreshUserTokenAsync("stored-refresh", Arg.Any<CancellationToken>())
            .Returns(new TwitchTokenRefreshResult(TwitchRefreshStatus.TransientFailure, null));
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, authClient, new TwitchTokenRefreshGate());

        var result = await service.GetValidAccessTokenAsync(new TwitchPrincipalInfo("uts-transient", "uts-transient", null));

        Assert.Null(result.AccessToken);
        Assert.False(result.ReauthRequired);
        await using var verifyDb = fixture.CreateDbContext();
        var stored = await new UserService(verifyDb, _cipher, _modRoleCache).GetTwitchTokensAsync("uts-transient");
        Assert.NotNull(stored);
        Assert.Equal("stored-refresh", stored.RefreshToken);
    }

    [Fact]
    public async Task NoStoredTokens_ReportsReauthRequired()
    {
        await using var db = fixture.CreateDbContext();
        await new UserService(db, _cipher, _modRoleCache).UpsertLoginAsync("uts-none", "uts-none", "uts-none");
        var authClient = Substitute.For<ITwitchAuthClient>();
        var service = CreateService(db, authClient, new TwitchTokenRefreshGate());

        var result = await service.GetValidAccessTokenAsync(new TwitchPrincipalInfo("uts-none", "uts-none", null));

        Assert.Null(result.AccessToken);
        Assert.True(result.ReauthRequired);
        await authClient.DidNotReceive().RefreshUserTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScopeDrift_ReportsReauthRequired_WithoutBurningARefresh()
    {
        // Stored grant lacks one of the currently requested scopes — refreshing can never add it.
        await SeedUserWithTokensAsync("uts-drift", DateTime.UtcNow.AddHours(2), scopes: "user:read:moderated_channels");
        var authClient = Substitute.For<ITwitchAuthClient>();
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, authClient, new TwitchTokenRefreshGate());

        var result = await service.GetValidAccessTokenAsync(new TwitchPrincipalInfo("uts-drift", "uts-drift", null));

        Assert.Null(result.AccessToken);
        Assert.True(result.ReauthRequired);
        await authClient.DidNotReceive().RefreshUserTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClaimTokenRejectedByValidate_FallsThroughToRefresh()
    {
        // Password change/disconnect: the claim still looks valid locally, but /validate says no —
        // the hourly validate-on-use is what catches this within the hour instead of at expiry.
        await SeedUserWithTokensAsync("uts-revoked", DateTime.UtcNow.AddMinutes(-5));
        var authClient = Substitute.For<ITwitchAuthClient>();
        authClient.ValidateTokenAsync("claim-token", Arg.Any<CancellationToken>()).Returns((bool?)false);
        authClient.RefreshUserTokenAsync("stored-refresh", Arg.Any<CancellationToken>())
            .Returns(new TwitchTokenRefreshResult(TwitchRefreshStatus.Success,
                new TwitchTokenResult("new-access", DateTime.UtcNow.AddHours(4), "rotated-refresh", TwitchOAuthDefaults.RequestedScopes)));
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, authClient, new TwitchTokenRefreshGate());

        var result = await service.GetValidAccessTokenAsync(new TwitchPrincipalInfo("uts-revoked", "uts-revoked", "claim-token"));

        Assert.Equal("new-access", result.AccessToken);
        Assert.False(result.ReauthRequired);
    }
}
