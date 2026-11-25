using System.Net;
using System.Text.Json;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// <c>GET /api/admin/dashboard/activity</c> and the <c>maxActiveChannels</c> ceiling on <c>/roster</c> (#381).
/// Each call gets a host of its own: the <c>IAdminDashboardReader</c> facade is a singleton with a cache,
/// and a cache shared between tests would turn "called once" assertions into order dependencies.
/// </summary>
public class AdminDashboardActivityEndpointTests : IClassFixture<ApiFactory>
{
    private const string Path = "/api/admin/dashboard/activity";

    private readonly ApiFactory _factory;

    public AdminDashboardActivityEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ChannelAccess.ClearReceivedCalls();
        factory.AdminDashboardQuery.ClearSubstitute();
        factory.AdminDashboardQuery
            .GetActivityAsync(Arg.Any<AdminDashboardActivityRange>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(NewActivity(call.Arg<AdminDashboardActivityRange>(), call.Arg<DateTime>())));
    }

    [Fact]
    public async Task Get_WithoutRange_ReturnsThirtyDaysWithTheContractShape()
    {
        using var factory = NewHost();

        var (status, body) = await SendAsync(factory, Path);
        using var disposal = body;

        Assert.Equal(HttpStatusCode.OK, status);
        var root = body.RootElement;
        Assert.Equal("30d", root.GetProperty("range").GetString());
        Assert.True(root.GetProperty("todayIsPartial").GetBoolean());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", root.GetProperty("fromDate").GetString()!);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", root.GetProperty("toDate").GetString()!);
        Assert.Equal(JsonValueKind.String, root.GetProperty("generatedAtUtc").ValueKind);
        var days = root.GetProperty("days");
        Assert.Equal(30, days.GetArrayLength());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", days[0].GetProperty("date").GetString()!);
        Assert.Equal(JsonValueKind.Number, days[0].GetProperty("liveChannels").ValueKind);
        Assert.Equal(JsonValueKind.Null, days[1].GetProperty("liveChannels").ValueKind);
        await _factory.AdminDashboardQuery.Received(1).GetActivityAsync(
            AdminDashboardActivityRange.Days30, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_WithAnEmptyRange_FallsBackToTheDefault()
    {
        using var factory = NewHost();

        var (status, body) = await SendAsync(factory, Path + "?range=");
        using var disposal = body;

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("30d", body.RootElement.GetProperty("range").GetString());
    }

    [Fact]
    public async Task Get_WithThirteenMonths_CallsTheServiceWithThatRange()
    {
        using var factory = NewHost();

        var (status, body) = await SendAsync(factory, Path + "?range=13mo");
        using var disposal = body;

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("13mo", body.RootElement.GetProperty("range").GetString());
        await _factory.AdminDashboardQuery.Received(1).GetActivityAsync(
            AdminDashboardActivityRange.Months13, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("7d")]
    [InlineData("30D")]
    [InlineData("%2030d")]
    [InlineData("%20")]
    [InlineData("30d%20")]
    public async Task Get_WithAnyOtherRange_Answers400(string range)
    {
        using var factory = NewHost();

        var (status, body) = await SendAsync(factory, $"{Path}?range={range}");
        using var disposal = body;

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_dashboard_range", body.RootElement.GetProperty("errorCode").GetString());
        await _factory.AdminDashboardQuery.DidNotReceiveWithAnyArgs()
            .GetActivityAsync(default, default, default);
    }

    [Fact]
    public async Task Get_TwiceWithinTheTtl_ReadsTheQueryServiceOnceAndKeepsRangesApart()
    {
        using var factory = NewHost();

        (await SendAsync(factory, Path)).Body.Dispose();
        (await SendAsync(factory, Path)).Body.Dispose();
        await _factory.AdminDashboardQuery.Received(1).GetActivityAsync(
            AdminDashboardActivityRange.Days30, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());

        (await SendAsync(factory, Path + "?range=90d")).Body.Dispose();
        (await SendAsync(factory, Path + "?range=90d")).Body.Dispose();
        await _factory.AdminDashboardQuery.Received(1).GetActivityAsync(
            AdminDashboardActivityRange.Days90, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _factory.AdminDashboardQuery.Received(1).GetActivityAsync(
            AdminDashboardActivityRange.Days30, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(Path)]
    [InlineData("/api/admin/rate-limits/trend")]
    public async Task Get_AsAnonymousCaller_Answers401(string path)
    {
        using var factory = NewHost();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(Path)]
    [InlineData("/api/admin/rate-limits/trend")]
    public async Task Get_AsNonAdmin_Answers403(string path)
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(false);
        using var factory = _factory.WithWebHostBuilder(_ => { });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString("N"));
        request.Headers.Add(TestAuthHandler.LoginHeader, "notanadmin");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Roster_ReportsTheConfiguredAdmissionCapInItsCeilings()
    {
        var roster = Substitute.For<IWorkerRosterReader>();
        roster.ReadAsync(Arg.Any<CancellationToken>()).Returns((WorkerRosterSnapshot?)null);
        var channelQuery = Substitute.For<IAdminChannelQueryService>();
        channelQuery.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));
        using var factory = NewHost(builder =>
        {
            builder.UseSetting("Channels:MaxActiveChannels", "42");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(roster);
                services.AddScoped(_ => channelQuery);
            });
        });

        var (status, body) = await SendAsync(factory, "/api/admin/roster");
        using var disposal = body;

        Assert.Equal(HttpStatusCode.OK, status);
        var ceilings = body.RootElement.GetProperty("ceilings");
        Assert.Equal(42, ceilings.GetProperty("maxActiveChannels").GetInt32());
        Assert.True(ceilings.TryGetProperty("twitchConcurrentChannelLimit", out _));
        // Removed in #516: the operating budget it reported (20) was never the binding limit.
        Assert.False(ceilings.TryGetProperty("twitchJoinBudgetChannels", out _));
        // Without a snapshot the join verdict is still shipped, and never "healthy".
        Assert.NotEqual("healthy", body.RootElement.GetProperty("joinHealth").GetProperty("status").GetString());
        Assert.False(body.RootElement.TryGetProperty("ircUnjoined", out _));
    }

    [Fact]
    public async Task Roster_WithASnapshot_ShipsTheJoinHealthVerdictAndTheSortedUnjoinedList()
    {
        var now = DateTime.UtcNow;
        var roster = Substitute.For<IWorkerRosterReader>();
        roster.ReadAsync(Arg.Any<CancellationToken>()).Returns(new WorkerRosterSnapshot(
            now, "worker", now.AddHours(-1), true, false, [], WorkerRosterSnapshot.CurrentSchemaVersion));
        var channelQuery = Substitute.For<IAdminChannelQueryService>();
        channelQuery.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["waiting", "banned", "stuck", "unwished"]));
        var joinHealth = Substitute.For<IChannelJoinHealthService>();
        joinHealth.EvaluateAsync(Arg.Any<WorkerRosterSnapshot?>(), Arg.Any<CancellationToken>()).Returns(
            new ChannelJoinHealthReport(
                ChannelJoinHealthStatus.Failing,
                300,
                [
                    new ChannelJoinHealthEntry("waiting", 40, null, false, false),
                    new ChannelJoinHealthEntry("banned", 900, "banned", true, false),
                    new ChannelJoinHealthEntry("stuck", 400, "noConfirmation", true, true),
                ],
                [new ChannelJoinHealthMissingIntent("unwished", 700, true)],
                AlarmingCount: 2,
                ExcludedCount: 1));
        using var factory = NewHost(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(roster);
            services.AddScoped(_ => channelQuery);
            services.AddSingleton(joinHealth);
        }));

        var (status, body) = await SendAsync(factory, "/api/admin/roster");
        using var disposal = body;

        Assert.Equal(HttpStatusCode.OK, status);
        var root = body.RootElement;
        var health = root.GetProperty("joinHealth");
        Assert.Equal("failing", health.GetProperty("status").GetString());
        Assert.Equal(300, health.GetProperty("graceSeconds").GetInt32());
        Assert.Equal(2, health.GetProperty("alarmingTotal").GetInt32());
        Assert.Equal(1, health.GetProperty("excludedTotal").GetInt32());
        // Alarming first, then overdue-but-excluded, then still waiting.
        var unjoined = root.GetProperty("ircUnjoined").EnumerateArray().ToList();
        Assert.Equal(["stuck", "banned", "waiting"], unjoined.Select(e => e.GetProperty("channelName").GetString()));
        Assert.Equal(3, root.GetProperty("ircUnjoinedTotal").GetInt32());
        Assert.Equal("noConfirmation", unjoined[0].GetProperty("reason").GetString());
        Assert.True(unjoined[0].GetProperty("countsForAlarm").GetBoolean());
        Assert.False(unjoined[1].GetProperty("countsForAlarm").GetBoolean());
        Assert.True(unjoined[1].GetProperty("overdue").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unjoined[2].GetProperty("reason").ValueKind);
        Assert.Equal(400, unjoined[0].GetProperty("unconfirmedSeconds").GetInt32());
        var missing = Assert.Single(root.GetProperty("missingIntent").EnumerateArray());
        Assert.Equal("unwished", missing.GetProperty("channelName").GetString());
        Assert.Equal(700, missing.GetProperty("waitingSeconds").GetInt32());
        Assert.True(missing.GetProperty("overdue").GetBoolean());
        Assert.Equal(1, root.GetProperty("missingIntentTotal").GetInt32());
        Assert.Equal(1, root.GetProperty("missingIntentOverdueTotal").GetInt32());
        // Superseded by ircUnjoined plus missingIntent, which split it by whether the worker holds a wish.
        Assert.False(root.TryGetProperty("missingFromIrc", out _));
        Assert.False(root.TryGetProperty("missingFromIrcTotal", out _));
    }

    [Fact]
    public async Task Roster_WithMoreThanFiftyUnconfirmedWishes_ListsOnlyChannelsWithoutAWishAsMissingIntent()
    {
        // The real join-health service and policy over a full roster: 60 wished, unconfirmed channels
        // (more than the 50-row cap of ircUnjoined), plus three active channels the worker holds no IRC
        // wish for — one of them present in the roster as a 7TV-only row. The missing list is decided
        // on the full roster before anything is capped, so no wished channel may appear in it.
        var now = DateTime.UtcNow;
        var wished = Enumerable.Range(0, 60).Select(i => $"wished{i:D2}").ToList();
        var rows = wished
            .Select((name, i) => new WorkerRosterChannel(
                name, false, null, null, true, null, true, IrcIntent: true, IrcUnconfirmedSeconds: i * 10, IrcJoinFailureReason: null))
            .Append(new WorkerRosterChannel("seventvonly", false, null, "set", true, null, true))
            .ToList();
        var roster = Substitute.For<IWorkerRosterReader>();
        roster.ReadAsync(Arg.Any<CancellationToken>()).Returns(new WorkerRosterSnapshot(
            now, "worker", now.AddHours(-2), true, false, rows, WorkerRosterSnapshot.CurrentSchemaVersion));
        var active = wished.Select(name => new ActiveChannelActivation(name, now.AddDays(-3)))
            .Append(new ActiveChannelActivation("seventvonly", now.AddDays(-3)))
            .Append(new ActiveChannelActivation("lostjoin", now.AddDays(-3)))
            .Append(new ActiveChannelActivation("justadded", now.AddSeconds(-30)))
            .ToList();
        var channels = Substitute.For<IChannelService>();
        channels.ListActiveChannelActivationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ActiveChannelActivation>>(active));
        var channelQuery = Substitute.For<IAdminChannelQueryService>();
        channelQuery.ListActiveChannelNamesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(active.Select(a => a.ChannelName).ToList()));
        using var factory = NewHost(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(roster);
            services.AddScoped(_ => channelQuery);
            services.AddScoped(_ => channels);
        }));

        var (status, body) = await SendAsync(factory, "/api/admin/roster");
        using var disposal = body;

        Assert.Equal(HttpStatusCode.OK, status);
        var root = body.RootElement;
        Assert.Equal(50, root.GetProperty("ircUnjoined").GetArrayLength());
        Assert.Equal(60, root.GetProperty("ircUnjoinedTotal").GetInt32());
        var missing = root.GetProperty("missingIntent").EnumerateArray().ToList();
        // Overdue first (longest wait, then name), the one still within its grace last.
        Assert.Equal(
            ["lostjoin", "seventvonly", "justadded"],
            missing.Select(e => e.GetProperty("channelName").GetString()));
        Assert.Equal([true, true, false], missing.Select(e => e.GetProperty("overdue").GetBoolean()));
        Assert.DoesNotContain(missing, e => wished.Contains(e.GetProperty("channelName").GetString()!));
        Assert.Equal(3, root.GetProperty("missingIntentTotal").GetInt32());
        Assert.Equal(2, root.GetProperty("missingIntentOverdueTotal").GetInt32());
    }

    private static AdminDashboardActivity NewActivity(AdminDashboardActivityRange range, DateTime nowUtc)
    {
        var (from, to) = AdminDashboardActivityRanges.GetWindow(range, nowUtc);
        var days = new List<AdminDashboardActivityDay>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            days.Add(new AdminDashboardActivityDay(date, days.Count == 1 ? null : days.Count));
        }

        return new AdminDashboardActivity(AdminDashboardActivityRanges.ToWire(range), from, to, true, nowUtc, days);
    }

    private WebApplicationFactory<Program> NewHost(Action<IWebHostBuilder>? configure = null)
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(true);
        return _factory.WithWebHostBuilder(builder => configure?.Invoke(builder));
    }

    private static async Task<(HttpStatusCode StatusCode, JsonDocument Body)> SendAsync(
        WebApplicationFactory<Program> factory, string path)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString("N"));
        request.Headers.Add(TestAuthHandler.LoginHeader, "someadmin");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text));
    }
}
