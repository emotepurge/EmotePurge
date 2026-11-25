using System.Net;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// <c>GET /api/health/channels</c> (#516): anonymous, payload-free, 200 or 503 and nothing else. The
/// verdict itself is tested in the Infrastructure suite; here the handler's mapping is pinned against a
/// substituted service, and the wiring (legacy roster, active channel without a wish) against the real
/// service over a substituted roster reader and channel list. Each case gets a host of its own because
/// the real service is a singleton with a cache.
/// </summary>
public class ApiChannelHealthEndpointTests : IClassFixture<ApiFactory>
{
    private const string Path = "/api/health/channels";

    private readonly ApiFactory _factory;

    public ApiChannelHealthEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        factory.Channels.ClearSubstitute();
    }

    [Theory]
    [InlineData(false, HttpStatusCode.OK)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    public async Task Handler_MapsTheServicesDecisionToTheStatusCode_WithoutPayload(bool alarming, HttpStatusCode expected)
    {
        var joinHealth = Substitute.For<IChannelJoinHealthService>();
        joinHealth.IsAlarmingAsync(Arg.Any<CancellationToken>()).Returns(alarming);
        using var factory = NewHost(services => services.AddSingleton(joinHealth));
        using var client = factory.CreateClient();

        // No credentials of any kind: the route is public.
        using var response = await client.GetAsync(Path);

        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LegacyRosterWithoutSchemaVersion_Answers503()
    {
        var roster = RosterReader(new WorkerRosterSnapshot(
            DateTime.UtcNow, "legacy-worker", DateTime.UtcNow.AddHours(-1), true, false,
            [new WorkerRosterChannel("somechannel", true, null, null, false, null, false)]));
        using var factory = NewHost(services => services.AddSingleton(roster));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ActiveChannelWithoutAWishPastTheGrace_Answers503()
    {
        var roster = RosterReader(CurrentRoster([]));
        _factory.Channels.ListActiveChannelActivationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ActiveChannelActivation>>(
                [new ActiveChannelActivation("somechannel", DateTime.UtcNow.AddHours(-1))]));
        using var factory = NewHost(services => services.AddSingleton(roster));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmedChannelMatchingTheDatabase_Answers200WithoutPayload()
    {
        var roster = RosterReader(CurrentRoster(
            [new WorkerRosterChannel("somechannel", true, null, null, false, null, false, IrcIntent: true)]));
        _factory.Channels.ListActiveChannelActivationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ActiveChannelActivation>>(
                [new ActiveChannelActivation("somechannel", DateTime.UtcNow.AddHours(-1))]));
        using var factory = NewHost(services => services.AddSingleton(roster));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OverdueChannel_LeavesTheMachineHealthEndpointAt200()
    {
        // The two endpoints answer different questions: a channel the worker cannot join is a
        // channel-level fault, and the container-level check must not turn red for it.
        var roster = RosterReader(CurrentRoster([]));
        _factory.Channels.ListActiveChannelActivationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ActiveChannelActivation>>(
                [new ActiveChannelActivation("somechannel", DateTime.UtcNow.AddHours(-1))]));
        _factory.WorkerHealth.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(new WorkerHealthSnapshot(true, null, null, TwitchLastFrameUtc: DateTime.UtcNow));
        using var factory = NewHost(services => services.AddSingleton(roster));
        using var client = factory.CreateClient();

        using var channels = await client.GetAsync(Path);
        using var health = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, channels.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static WorkerRosterSnapshot CurrentRoster(IReadOnlyList<WorkerRosterChannel> channels) =>
        new(DateTime.UtcNow, "current-worker", DateTime.UtcNow.AddHours(-1), true, false, channels,
            WorkerRosterSnapshot.CurrentSchemaVersion);

    private static IWorkerRosterReader RosterReader(WorkerRosterSnapshot snapshot)
    {
        var reader = Substitute.For<IWorkerRosterReader>();
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        reader.ReadStrictAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        return reader;
    }

    private WebApplicationFactory<Program> NewHost(Action<IServiceCollection> configure) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(configure));
}
