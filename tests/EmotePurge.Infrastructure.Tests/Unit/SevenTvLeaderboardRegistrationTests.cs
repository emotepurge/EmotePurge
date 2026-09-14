using EmotePurge.Core.Services;
using EmotePurge.Infrastructure;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The one thing about the leaderboard's registration that cannot be seen from the service itself:
/// its circuit breaker is a <i>second</i> object of the same policy class, and adding it left the
/// foreign-channel preview holding the same typed instance it has always had (spec section 6, AK 12).
/// </summary>
/// <remarks>
/// Nothing here connects. The container is built and the leaderboard's own graph is resolved — its
/// guards and the service above them, including the 7TV client, which constructs an HttpClient but
/// never sends anything. The one registration that would dial on resolve, the Redis multiplexer, is
/// substituted; no database, no Redis, no 7TV, so this stays a unit test.
/// </remarks>
public class SevenTvLeaderboardRegistrationTests
{
    [Fact]
    public void TheLeaderboardBreaker_IsASecondInstance_AlongsideThePreviewsTypedOne()
    {
        using var provider = BuildProvider();

        var previewBreaker = provider.GetRequiredService<ForeignSevenTvBreakerPolicy>();
        var leaderboardBreaker = provider.GetRequiredKeyedService<ForeignSevenTvBreakerPolicy>("seventv-leaderboard");

        // Different failure domains: a 7TV search-bucket lockout must not close the preview path,
        // which never touches that bucket.
        Assert.NotSame(previewBreaker, leaderboardBreaker);
        // And the preview still gets a plain typed resolution, not a keyed one.
        Assert.Same(previewBreaker, provider.GetRequiredService<ForeignSevenTvBreakerPolicy>());
    }

    [Fact]
    public void TheStockBudgetAndAlarm_AreOnePerProcess()
    {
        using var provider = BuildProvider();

        // A stock, a rolling window budget or an alarm latch handed out per request would guard
        // nothing at all.
        Assert.Same(
            provider.GetRequiredService<SevenTvLeaderboardStore<SevenTvLeaderboardResult>>(),
            provider.CreateScope().ServiceProvider.GetRequiredService<SevenTvLeaderboardStore<SevenTvLeaderboardResult>>());
        Assert.Same(
            provider.GetRequiredService<SevenTvLeaderboardRequestBudget>(),
            provider.CreateScope().ServiceProvider.GetRequiredService<SevenTvLeaderboardRequestBudget>());
        Assert.Same(
            provider.GetRequiredService<SevenTvLeaderboardBudgetAlarm>(),
            provider.CreateScope().ServiceProvider.GetRequiredService<SevenTvLeaderboardBudgetAlarm>());
    }

    [Fact]
    public void TheServiceItself_ResolvesWithItsWholeDependencyGraph()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        // The only way to find out that the factory lambda's keyed lookup, its typed client and its
        // five collaborators actually line up: a factory registration is never checked until
        // somebody resolves it.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISevenTvLeaderboardService>());
    }

    [Fact]
    public void TheWindowBudget_CarriesTheTwoFiguresTheSpecFixes()
    {
        using var provider = BuildProvider();

        var budget = provider.GetRequiredService<SevenTvLeaderboardRequestBudget>();

        Assert.Equal(10, budget.MaxRequests);
        Assert.Equal(TimeSpan.FromMinutes(60), budget.Window);
    }

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused",
                ["Redis:ConnectionString"] = "localhost:6379"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEmotePurgeInfrastructure(configuration);
        // Substituted for the same reason WorkerServiceRegistrationTests substitutes it: the real
        // registration is a factory that dials on first resolve, and the telemetry handler wrapped
        // around the 7TV client reaches it. Nothing here connects to anything.
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        return services.BuildServiceProvider();
    }
}
