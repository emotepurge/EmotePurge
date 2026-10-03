using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Twitch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class TwitchAppTokenProviderTests
{
    [Fact]
    public async Task GetTokenAsync_CachesTheToken_InsteadOfFetchingPerCall()
    {
        var authClient = new FakeAuthClient(TokenExpiringIn(TimeSpan.FromDays(60)));
        var provider = CreateProvider(authClient);

        Assert.Equal("token-1", await provider.GetTokenAsync());
        Assert.Equal("token-1", await provider.GetTokenAsync());
        Assert.Equal(1, authClient.AppTokenFetches);
    }

    [Fact]
    public async Task GetTokenAsync_RefetchesATokenCloseToExpiry()
    {
        // 30 minutes is inside the one-hour margin — the cached token must not be handed out.
        var authClient = new FakeAuthClient(TokenExpiringIn(TimeSpan.FromMinutes(30)));
        var provider = CreateProvider(authClient);

        await provider.GetTokenAsync();
        await provider.GetTokenAsync();

        Assert.Equal(2, authClient.AppTokenFetches);
    }

    [Fact]
    public async Task GetTokenAsync_DoesNotCacheAFailure()
    {
        var authClient = new FakeAuthClient(TokenExpiringIn(TimeSpan.FromDays(60))) { FailNextFetch = true };
        var provider = CreateProvider(authClient);

        Assert.Null(await provider.GetTokenAsync());
        Assert.Equal("token-2", await provider.GetTokenAsync());
        Assert.Equal(2, authClient.AppTokenFetches);
    }

    [Fact]
    public async Task Invalidate_DropsTheCachedToken()
    {
        var authClient = new FakeAuthClient(TokenExpiringIn(TimeSpan.FromDays(60)));
        var provider = CreateProvider(authClient);

        Assert.Equal("token-1", await provider.GetTokenAsync());
        provider.Invalidate();
        Assert.Equal("token-2", await provider.GetTokenAsync());
        Assert.Equal(2, authClient.AppTokenFetches);
    }

    private static TwitchAppTokenProvider CreateProvider(FakeAuthClient authClient)
    {
        // The provider is a singleton over the transient typed client and therefore resolves
        // ITwitchAuthClient through a scope — the test mirrors that instead of shortcutting it.
        var services = new ServiceCollection();
        services.AddSingleton<ITwitchAuthClient>(authClient);
        var serviceProvider = services.BuildServiceProvider();
        return new TwitchAppTokenProvider(serviceProvider.GetRequiredService<IServiceScopeFactory>());
    }

    private static Func<int, TwitchTokenResult> TokenExpiringIn(TimeSpan lifetime) =>
        fetchNumber => new TwitchTokenResult($"token-{fetchNumber}", DateTime.UtcNow.Add(lifetime));

    private sealed class FakeAuthClient(Func<int, TwitchTokenResult> tokenFactory) : ITwitchAuthClient
    {
        public int AppTokenFetches { get; private set; }
        public bool FailNextFetch { get; set; }

        public Task<TwitchTokenResult?> GetAppAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            AppTokenFetches++;
            if (FailNextFetch)
            {
                FailNextFetch = false;
                return Task.FromResult<TwitchTokenResult?>(null);
            }

            return Task.FromResult<TwitchTokenResult?>(tokenFactory(AppTokenFetches));
        }

        public Task<TwitchTokenResult?> ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TwitchTokenRefreshResult> RefreshUserTokenAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool?> ValidateTokenAsync(string accessToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RevokeTokenAsync(string token, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
