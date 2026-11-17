using EmotePurge.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Api and Worker both call AddEmotePurgeInfrastructure, so this is the proof that both start with and
// without Wishes__Enabled: the options bind from the environment spelling, default to off, and the wish
// services resolve in every case (nothing dials; the multiplexer is substituted).
public class WishRegistrationTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void WishOptions_BindFromTheSwitch_AndDefaultToOff(string? value, bool expected)
    {
        using var provider = BuildProvider(value);

        Assert.Equal(expected, provider.GetRequiredService<WishOptions>().Enabled);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData(null)]
    public void WishServices_Resolve_WhateverTheSwitch(string? value)
    {
        using var provider = BuildProvider(value);
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IWishService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IWishChangedPublisher>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IWishOverviewService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IWishReconcileService>());
        Assert.Same(
            provider.GetRequiredService<IWishReconcileService>(), scope.ServiceProvider.GetRequiredService<IWishReconcileService>());
    }

    private static ServiceProvider BuildProvider(string? wishesEnabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Wishes:Enabled"] = wishesEnabled,
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused",
            ["Redis:ConnectionString"] = "localhost:6379"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEmotePurgeInfrastructure(configuration);
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
    }
}
