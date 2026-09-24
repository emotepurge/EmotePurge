using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The fail-fast half of the retention job's configuration, and its registration. Nothing connects: the
// Redis multiplexer is substituted and no query ever runs (SevenTvLeaderboardRegistrationTests pattern).
public class RetentionOptionsTests
{
    [Fact]
    public void Defaults_AreTheDryRunAndTheApprovedPacing()
    {
        var options = new RetentionOptions();

        options.Validate();

        // Enforce defaults to false on purpose: the first deploy must only count (plan, decision 4).
        Assert.False(options.Enforce);
        Assert.Equal(24, options.IntervalHours);
        Assert.Equal(10, options.StartupDelayMinutes);
        Assert.Equal(100, options.MaxAccountsPerRun);
    }

    [Theory]
    [InlineData(0, 10, 100, "Retention:IntervalHours")]
    [InlineData(-1, 10, 100, "Retention:IntervalHours")]
    [InlineData(24, -1, 100, "Retention:StartupDelayMinutes")]
    [InlineData(24, 10, 0, "Retention:MaxAccountsPerRun")]
    [InlineData(24, 10, -5, "Retention:MaxAccountsPerRun")]
    public void Validate_RejectsUnusableValues_NamingTheKey(int intervalHours, int startupDelayMinutes, int maxAccountsPerRun, string key)
    {
        var options = new RetentionOptions
        {
            IntervalHours = intervalHours,
            StartupDelayMinutes = startupDelayMinutes,
            MaxAccountsPerRun = maxAccountsPerRun
        };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void Validate_AcceptsAZeroStartupDelay()
    {
        new RetentionOptions { StartupDelayMinutes = 0 }.Validate();
    }

    [Fact]
    public void ThePeriods_AreTheOnesDecidedOn20260923()
    {
        // The privacy policy (#247) quotes these; a change here is a change of that text.
        Assert.Equal(TimeSpan.FromDays(30), RetentionPolicy.TwitchTokens);
        Assert.Equal(TimeSpan.FromDays(365), RetentionPolicy.InactiveAccount);
        Assert.Equal(TimeSpan.FromDays(365), RetentionPolicy.EndedVoteSession);
        Assert.Equal(TimeSpan.FromDays(365), RetentionPolicy.AuditLogEntry);
        Assert.Equal(TimeSpan.FromDays(180), RetentionPolicy.DeactivatedChannel);
    }

    [Fact]
    public void Registration_BindsTheSection_AndResolvesTheServiceWithTheSystemClock()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Retention:Enforce"] = "true",
            ["Retention:MaxAccountsPerRun"] = "7"
        });
        using var scope = provider.CreateScope();

        var options = provider.GetRequiredService<RetentionOptions>();
        Assert.True(options.Enforce);
        Assert.Equal(7, options.MaxAccountsPerRun);
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.IsType<DataRetentionService>(scope.ServiceProvider.GetRequiredService<IDataRetentionService>());
    }

    [Fact]
    public void Registration_KeepsATimeProviderRegisteredBeforeIt()
    {
        var clock = Substitute.For<TimeProvider>();

        using var provider = BuildProvider([], services => services.AddSingleton(clock));

        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Registration_FailsFastOnAnInvalidValue()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(new Dictionary<string, string?> { ["Retention:IntervalHours"] = "0" }));

        Assert.Contains("Retention:IntervalHours", ex.Message);
    }

    private static ServiceProvider BuildProvider(
        Dictionary<string, string?> retentionSettings, Action<IServiceCollection>? before = null)
    {
        var settings = new Dictionary<string, string?>(retentionSettings)
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused",
            ["Redis:ConnectionString"] = "localhost:6379"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // Both hosts provide it; ModRoleCache (behind the account deletion) reads its TTL from it.
        services.AddSingleton<IConfiguration>(configuration);
        before?.Invoke(services);
        services.AddEmotePurgeInfrastructure(configuration);
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        return services.BuildServiceProvider();
    }
}
