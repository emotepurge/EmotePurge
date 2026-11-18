using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
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

    // The expiry run (#484 T3) resolves per scope whatever the switch (the worker checks the switch, not the
    // container), and the stand-in bot availability is registered and never available until K4 replaces it.
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData(null)]
    public async Task WishExpiry_Resolves_WhateverTheSwitch_AndTheDefaultBotIsNeverAvailable(string? value)
    {
        using var provider = BuildProvider(value);
        using var scope = provider.CreateScope();

        Assert.IsType<WishExpiryService>(scope.ServiceProvider.GetRequiredService<IWishExpiryService>());
        var availability = provider.GetRequiredService<IWishBotAvailability>();
        Assert.IsType<WishBotNeverAvailable>(availability);
        Assert.False(await availability.IsAvailableAsync());
    }

    // The trigger parameters of SevenTvSyncService and EmoteService are optional (null = no hook), so a
    // miswired graph would silently drop the hook. A recording double behind the registration cannot be
    // reached without a database, so this reads the injected field instead — by its type, not its name, which
    // the compiler generates for a primary-constructor parameter and may change.
    [Fact]
    public void SlotWriters_ReceiveTheRegisteredTrigger()
    {
        using var provider = BuildProvider("true");
        using var scope = provider.CreateScope();
        var registered = provider.GetRequiredService<IWishReconcileTrigger>();

        Assert.Same(registered, InjectedTrigger(scope.ServiceProvider.GetRequiredService<ISevenTvSyncService>()));
        Assert.Same(registered, InjectedTrigger(scope.ServiceProvider.GetRequiredService<IEmoteService>()));
    }

    private static object? InjectedTrigger(object service)
    {
        var fields = service.GetType()
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(IWishReconcileTrigger))
            .ToList();
        return Assert.Single(fields).GetValue(service);
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
