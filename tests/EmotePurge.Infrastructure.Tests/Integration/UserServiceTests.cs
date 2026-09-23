using System.Security.Cryptography;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Postgres comes from the shared collection; Redis is a class fixture because InvalidateRoleCache
// spans both stores and the real ModRoleCache is what makes the removed-entry count meaningful.
[Collection("Postgres")]
public class UserServiceTests(PostgresFixture fixture, RedisFixture redisFixture) : IClassFixture<RedisFixture>
{
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

    private ModRoleCache CreateRoleCache() => new(redisFixture.Connection, new ConfigurationBuilder().Build(), NullLogger<ModRoleCache>.Instance);

    // The shared moderated-channel list is written by ModeratedChannelsProvider, not by ModRoleCache;
    // seeding the key directly keeps this test about the invalidation reaching it.
    private Task WriteModeratedChannelListAsync(string twitchUserId) =>
        redisFixture.Connection.GetDatabase().StringSetAsync($"modlist:{twitchUserId}", "[]");

    [Fact]
    public async Task StoreTwitchTokens_ThenGet_RoundTripsAllFields()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher(), CreateRoleCache());
        await service.UpsertLoginAsync("user-tokens-1", "usertokens1", "UserTokens1");
        var expiresAt = DateTime.UtcNow.AddHours(4);

        await service.StoreTwitchTokensAsync("user-tokens-1", "access-token", expiresAt, "refresh-token", "user:read:email");

        var tokens = await service.GetTwitchTokensAsync("user-tokens-1");
        Assert.NotNull(tokens);
        Assert.Equal("refresh-token", tokens.RefreshToken);
        Assert.Equal("access-token", tokens.AccessToken);
        Assert.Equal(expiresAt, tokens.AccessTokenExpiresAtUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal("user:read:email", tokens.Scopes);
    }

    [Fact]
    public async Task StoreTwitchTokens_NeverWritesPlaintextToTheDatabase()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher(), CreateRoleCache());
        await service.UpsertLoginAsync("user-tokens-2", "usertokens2", "UserTokens2");

        await service.StoreTwitchTokensAsync("user-tokens-2", "plain-access", DateTime.UtcNow.AddHours(4), "plain-refresh", null);

        await using var verifyDb = fixture.CreateDbContext();
        var row = await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == "user-tokens-2");
        Assert.NotNull(row.TwitchRefreshToken);
        Assert.NotNull(row.TwitchAccessToken);
        Assert.DoesNotContain("plain-refresh", row.TwitchRefreshToken);
        Assert.DoesNotContain("plain-access", row.TwitchAccessToken);
    }

    [Fact]
    public async Task GetTwitchTokens_WithDifferentCipherKey_ReturnsNull()
    {
        // Simulates a rotated/lost encryption key: the stored row exists but must behave exactly
        // like "no token stored" instead of surfacing garbage or an exception.
        await using var db = fixture.CreateDbContext();
        await new UserService(db, CreateCipher(), CreateRoleCache()).UpsertLoginAsync("user-tokens-3", "usertokens3", "UserTokens3");
        await new UserService(db, CreateCipher(), CreateRoleCache()).StoreTwitchTokensAsync(
            "user-tokens-3", "access", DateTime.UtcNow.AddHours(4), "refresh", null);

        await using var readDb = fixture.CreateDbContext();
        Assert.Null(await new UserService(readDb, CreateCipher(), CreateRoleCache()).GetTwitchTokensAsync("user-tokens-3"));
    }

    [Fact]
    public async Task ClearTwitchTokens_RemovesAllFourColumns()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher(), CreateRoleCache());
        await service.UpsertLoginAsync("user-tokens-4", "usertokens4", "UserTokens4");
        await service.StoreTwitchTokensAsync("user-tokens-4", "access", DateTime.UtcNow.AddHours(4), "refresh", "scopes");

        await service.ClearTwitchTokensAsync("user-tokens-4");

        Assert.Null(await service.GetTwitchTokensAsync("user-tokens-4"));
        await using var verifyDb = fixture.CreateDbContext();
        var row = await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == "user-tokens-4");
        Assert.Null(row.TwitchRefreshToken);
        Assert.Null(row.TwitchAccessToken);
        Assert.Null(row.TwitchAccessTokenExpiresAtUtc);
        Assert.Null(row.TwitchTokenScopes);
    }

    [Fact]
    public async Task GetTwitchTokens_ForUnknownUser_ReturnsNull()
    {
        await using var db = fixture.CreateDbContext();

        Assert.Null(await new UserService(db, CreateCipher(), CreateRoleCache()).GetTwitchTokensAsync("user-tokens-nobody"));
    }

    [Fact]
    public async Task StoreTwitchTokens_ForUnknownUser_IsANoOp()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher(), CreateRoleCache());

        await service.StoreTwitchTokensAsync("user-tokens-ghost", "access", DateTime.UtcNow, "refresh", null);

        Assert.Null(await service.GetTwitchTokensAsync("user-tokens-ghost"));
    }

    [Fact]
    public async Task RevokeSessions_WithActor_WritesTheAuditEntryInTheSameTransaction()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher(), CreateRoleCache());
        await service.UpsertLoginAsync("user-revoke-1", "userrevoke1", "UserRevoke1");

        var revoked = await service.RevokeSessionsAsync("user-revoke-1", new AuditActor("admin-1", "sensitron"));

        Assert.True(revoked);
        await using var verifyDb = fixture.CreateDbContext();
        var row = await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == "user-revoke-1");
        Assert.NotNull(row.SessionsValidFromUtc);

        var entry = await verifyDb.AuditLogEntries.AsNoTracking()
            .SingleAsync(e => e.TargetType == "user" && e.TargetId == "user-revoke-1");
        Assert.Equal(AuditActions.UserRevokeSessions, entry.Action);
        Assert.Equal("sensitron", entry.ActorLogin);
        Assert.Equal("admin-1", entry.ActorTwitchUserId);
        // The revoked user's login travels as a details snapshot — TargetId alone is just a number.
        Assert.Contains("userrevoke1", entry.DetailsJson);
    }

    [Fact]
    public async Task RevokeSessions_WithoutActor_RevokesButStaysUnaudited()
    {
        // The self-logout path: revocation happens, but no login/logout noise in the audit log.
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher(), CreateRoleCache());
        await service.UpsertLoginAsync("user-revoke-2", "userrevoke2", "UserRevoke2");

        var revoked = await service.RevokeSessionsAsync("user-revoke-2", actor: null);

        Assert.True(revoked);
        await using var verifyDb = fixture.CreateDbContext();
        Assert.NotNull((await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == "user-revoke-2")).SessionsValidFromUtc);
        Assert.False(await verifyDb.AuditLogEntries.AsNoTracking().AnyAsync(e => e.TargetId == "user-revoke-2"));
    }

    [Fact]
    public async Task RevokeSessions_ForUnknownUser_ReturnsFalse_AndWritesNothing()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher(), CreateRoleCache());

        var revoked = await service.RevokeSessionsAsync("user-revoke-nobody", new AuditActor("admin-1", "sensitron"));

        Assert.False(revoked);
        await using var verifyDb = fixture.CreateDbContext();
        Assert.False(await verifyDb.AuditLogEntries.AsNoTracking().AnyAsync(e => e.TargetId == "user-revoke-nobody"));
    }

    [Fact]
    public async Task InvalidateRoleCache_ForKnownUser_ClearsRedisEntries_AndAuditsTheCount()
    {
        await using var db = fixture.CreateDbContext();
        var roleCache = CreateRoleCache();
        var service = new UserService(db, CreateCipher(), roleCache);
        await service.UpsertLoginAsync("user-rolecache-1", "userrolecache1", "UserRoleCache1");
        await WriteModeratedChannelListAsync("user-rolecache-1");
        await roleCache.SetIsSubscriberAsync("user-rolecache-1", "9001", isSubscriber: true);

        var removedEntries = await service.InvalidateRoleCacheAsync("user-rolecache-1", new AuditActor("admin-1", "sensitron"));

        Assert.Equal(2, removedEntries);
        Assert.False(await redisFixture.Connection.GetDatabase().KeyExistsAsync("modlist:user-rolecache-1"));
        Assert.Null(await roleCache.TryGetIsSubscriberAsync("user-rolecache-1", "9001"));

        await using var verifyDb = fixture.CreateDbContext();
        var entry = await verifyDb.AuditLogEntries.AsNoTracking()
            .SingleAsync(e => e.TargetType == "user" && e.TargetId == "user-rolecache-1");
        Assert.Equal(AuditActions.UserInvalidateRoleCache, entry.Action);
        Assert.Equal("sensitron", entry.ActorLogin);
        // The count is the whole point of the entry: it tells the reader whether anything was
        // actually cached, which a bare "invalidated" line could not. Parsed rather than substring-
        // matched because the jsonb column hands the payload back reformatted, not byte-identical.
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        Assert.Equal("userrolecache1", details.RootElement.GetProperty("login").GetString());
        Assert.Equal(2, details.RootElement.GetProperty("removedEntries").GetInt32());
    }

    [Fact]
    public async Task CheckSession_ForUnknownUser_ReturnsNull()
    {
        // The caller (OnValidatePrincipal) must reject the principal rather than read this as
        // "never revoked" — a deleted account's cookie must stop working on its next request.
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher(), CreateRoleCache());

        Assert.Null(await service.CheckSessionAsync("user-checksession-nobody"));
    }

    [Fact]
    public async Task CheckSession_ReturnsTheRevocationCutoff_ForAKnownUser()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher(), CreateRoleCache());
        await service.UpsertLoginAsync("user-checksession-cutoff", "usercutoff", "UserCutoff");
        await service.RevokeSessionsAsync("user-checksession-cutoff", actor: null);

        var result = await service.CheckSessionAsync("user-checksession-cutoff");

        Assert.NotNull(result);
        Assert.NotNull(result.RevokedBefore);
    }

    [Fact]
    public async Task CheckSession_FirstCallForAFreshUser_StampsLastSeenAtUtc()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher(), CreateRoleCache());
        // UpsertLoginAsync never touches LastSeenAtUtc, so a freshly logged-in user starts null —
        // the same state a pre-migration backfill row would not be in, but a genuinely new row is.
        await service.UpsertLoginAsync("user-checksession-first", "userfirst", "UserFirst");

        var before = DateTime.UtcNow;
        await service.CheckSessionAsync("user-checksession-first");

        await using var verifyDb = fixture.CreateDbContext();
        var row = await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == "user-checksession-first");
        Assert.NotNull(row.LastSeenAtUtc);
        Assert.True(row.LastSeenAtUtc >= before);
    }

    [Fact]
    public async Task CheckSession_WithinThrottleWindow_DoesNotRewriteLastSeenAtUtc()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher(), CreateRoleCache());
        await service.UpsertLoginAsync("user-checksession-fresh", "userfresh", "UserFresh");
        var recentStamp = DateTime.UtcNow.AddHours(-1);
        await db.Users.Where(u => u.Id == "user-checksession-fresh")
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAtUtc, recentStamp));

        await service.CheckSessionAsync("user-checksession-fresh");

        await using var verifyDb = fixture.CreateDbContext();
        var row = await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == "user-checksession-fresh");
        Assert.Equal(recentStamp, row.LastSeenAtUtc!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CheckSession_AfterThrottleWindow_RewritesLastSeenAtUtc()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher(), CreateRoleCache());
        await service.UpsertLoginAsync("user-checksession-stale", "userstale", "UserStale");
        var staleStamp = DateTime.UtcNow.AddHours(-25);
        await db.Users.Where(u => u.Id == "user-checksession-stale")
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAtUtc, staleStamp));

        var before = DateTime.UtcNow;
        await service.CheckSessionAsync("user-checksession-stale");

        await using var verifyDb = fixture.CreateDbContext();
        var row = await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == "user-checksession-stale");
        Assert.NotNull(row.LastSeenAtUtc);
        Assert.True(row.LastSeenAtUtc >= before);
    }

    [Fact]
    public async Task InvalidateRoleCache_ForUnknownUser_ReturnsNull_AndTouchesNeitherStore()
    {
        // The unknown-user branch returns before Redis is reached — verified by seeding a key under
        // that very id and watching it survive, which a delete would have removed.
        await using var db = fixture.CreateDbContext();
        var roleCache = CreateRoleCache();
        var service = new UserService(db, CreateCipher(), roleCache);
        await WriteModeratedChannelListAsync("user-rolecache-nobody");

        var removedEntries = await service.InvalidateRoleCacheAsync("user-rolecache-nobody", new AuditActor("admin-1", "sensitron"));

        Assert.Null(removedEntries);
        Assert.True(await redisFixture.Connection.GetDatabase().KeyExistsAsync("modlist:user-rolecache-nobody"));
        await using var verifyDb = fixture.CreateDbContext();
        Assert.False(await verifyDb.AuditLogEntries.AsNoTracking().AnyAsync(e => e.TargetId == "user-rolecache-nobody"));
    }
}
