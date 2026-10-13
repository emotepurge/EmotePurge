using System.Reflection;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Plan #372 review P3-3: every service that reads <see cref="SlotReconcileOptions"/> takes it as an
/// optional constructor parameter, so a registration that stopped handing it over would not fail —
/// the service would silently run with the flag off. This resolves the readers the way api and
/// worker do (both call <c>AddEmotePurgeInfrastructure</c>) and reads the value each one holds.
/// </summary>
/// <remarks>
/// Container-free like <see cref="SevenTvLeaderboardRegistrationTests"/>: the Redis multiplexer, the
/// one registration that would dial on resolve, is substituted; nothing connects.
/// </remarks>
public class SlotReconcileRegistrationTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void EveryReader_SeesTheConfiguredFlag(string? configured, bool expected)
    {
        using var provider = BuildProvider(configured);
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;

        Assert.Equal(expected, FlagOf(services.GetRequiredService<ISevenTvSyncService>(), "slotReconcileEnabled"));
        Assert.Equal(expected, FlagOf(services.GetRequiredService<IEmoteService>(), "slotReconcileEnabled"));
        Assert.Equal(expected, FlagOf(services.GetRequiredService<IEmoteSetStatusService>(), "slotsEnabled"));
        Assert.Equal(expected, FlagOf(services.GetRequiredService<ISevenTvApiClient>(), "slotReconcileEnabled"));
        Assert.Equal(expected, FlagOf(services.GetRequiredService<IUsageStatQueryService>(), "slotReconcileEnabled"));
        Assert.Equal(expected, FlagOf(services.GetRequiredService<IVoteSessionQueryService>(), "slotReconcileEnabled"));
        Assert.Equal(expected, FlagOf(services.GetRequiredService<IChatLogBackfillService>(), "slotReconcileEnabled"));
    }

    // The services keep the flag in a private field read once at construction (E26); reading it is
    // the only way to see what the container really handed over without running a reconcile.
    private static bool FlagOf(object service, string fieldName)
    {
        var field = service.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException($"{service.GetType().Name} has no field {fieldName}; update this test with the rename.");
        return (bool)field.GetValue(service)!;
    }

    private static ServiceProvider BuildProvider(string? configured)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused",
            ["Redis:ConnectionString"] = "localhost:6379"
        };
        if (configured is not null)
        {
            values[$"{SlotReconcileOptions.SectionName}:Enabled"] = configured;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        // The host provides IConfiguration in api and worker (the excluded-channel filter reads it).
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEmotePurgeInfrastructure(configuration);
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        return services.BuildServiceProvider();
    }
}
