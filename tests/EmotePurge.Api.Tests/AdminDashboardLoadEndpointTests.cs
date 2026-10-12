using System.Net;
using System.Text;
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
/// <c>GET /api/admin/dashboard/load</c> (#381 cut B). Like the activity tests, each call gets a host of
/// its own because the <c>IAdminDashboardReader</c> facade is a singleton with a cache.
/// </summary>
public class AdminDashboardLoadEndpointTests : IClassFixture<ApiFactory>
{
    private const string Path = "/api/admin/dashboard/load";
    private const int MaxResponseBytes = 256 * 1024;

    private readonly ApiFactory _factory;

    public AdminDashboardLoadEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ChannelAccess.ClearReceivedCalls();
        factory.AdminDashboardQuery.ClearSubstitute();
        factory.AdminDashboardQuery
            .GetLoadAsync(Arg.Any<AdminDashboardLoadRange>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(NewLoad(call.Arg<AdminDashboardLoadRange>(), call.Arg<DateTime>(), call.Arg<int>(), 3)));
    }

    [Fact]
    public async Task Get_WithoutRange_Returns24HoursWithTheContractShape()
    {
        using var factory = NewHost();

        var (status, text, _) = await SendAsync(factory, Path);

        Assert.Equal(HttpStatusCode.OK, status);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        Assert.Equal("24h", root.GetProperty("range").GetString());
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal("raw", root.GetProperty("sourceKind").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$", root.GetProperty("fromUtc").GetString()!);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$", root.GetProperty("generatedAtUtc").GetString()!);
        var buckets = root.GetProperty("bucketStartsUtc");
        Assert.Equal(3, buckets.GetArrayLength());
        foreach (var bucket in buckets.EnumerateArray())
        {
            Assert.EndsWith("Z", bucket.GetString());
        }

        AssertAllListsHaveLength(root, 3);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("host").GetProperty("cpuBusyP95")[1].ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("swap").ValueKind);
        await _factory.AdminDashboardQuery.Received(1).GetLoadAsync(
            AdminDashboardLoadRange.Hours24, Arg.Any<DateTime>(), 30, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_WithAnEmptyRange_FallsBackToTheDefault()
    {
        using var factory = NewHost();

        var (status, text, _) = await SendAsync(factory, Path + "?range=");

        Assert.Equal(HttpStatusCode.OK, status);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("24h", body.RootElement.GetProperty("range").GetString());
    }

    [Theory]
    [InlineData("1h", AdminDashboardLoadRange.Hour1)]
    [InlineData("24h", AdminDashboardLoadRange.Hours24)]
    [InlineData("7d", AdminDashboardLoadRange.Days7)]
    [InlineData("30d", AdminDashboardLoadRange.Days30)]
    [InlineData("90d", AdminDashboardLoadRange.Days90)]
    [InlineData("13mo", AdminDashboardLoadRange.Months13)]
    public async Task Get_WithEachRange_CallsTheServiceWithThatRangeAndTheConfiguredInterval(
        string wire, AdminDashboardLoadRange expected)
    {
        using var factory = NewHost(builder => builder.UseSetting("SystemLoad:IntervalSeconds", "20"));

        var (status, text, _) = await SendAsync(factory, $"{Path}?range={wire}");

        Assert.Equal(HttpStatusCode.OK, status);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(wire, body.RootElement.GetProperty("range").GetString());
        Assert.Equal(20, body.RootElement.GetProperty("samplingIntervalSeconds").GetInt32());
        await _factory.AdminDashboardQuery.Received(1).GetLoadAsync(
            expected, Arg.Any<DateTime>(), 20, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("2h")]
    [InlineData("24H")]
    [InlineData("30")]
    [InlineData("%20")]
    [InlineData("24h%20")]
    public async Task Get_WithAnyOtherRange_Answers400(string range)
    {
        using var factory = NewHost();

        var (status, text, _) = await SendAsync(factory, $"{Path}?range={range}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("invalid_dashboard_range", body.RootElement.GetProperty("errorCode").GetString());
        await _factory.AdminDashboardQuery.DidNotReceiveWithAnyArgs().GetLoadAsync(default, default, default, default);
    }

    [Fact]
    public async Task Get_WhenNothingWasObserved_Answers200WithEmptyLists()
    {
        _factory.AdminDashboardQuery
            .GetLoadAsync(Arg.Any<AdminDashboardLoadRange>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(NewLoad(call.Arg<AdminDashboardLoadRange>(), call.Arg<DateTime>(), call.Arg<int>(), 0)));
        using var factory = NewHost();

        var (status, text, _) = await SendAsync(factory, Path);

        Assert.Equal(HttpStatusCode.OK, status);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        Assert.False(root.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("dataThroughUtc").ValueKind);
        Assert.Equal(0, root.GetProperty("bucketStartsUtc").GetArrayLength());
        AssertAllListsHaveLength(root, 0);
    }

    [Fact]
    public async Task Get_WithTheWidestAllowedFullyPopulatedResponse_StaysWithinTheByteBudgetAndLeaksNoInstanceId()
    {
        _factory.AdminDashboardQuery
            .GetLoadAsync(Arg.Any<AdminDashboardLoadRange>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(NewLoad(call.Arg<AdminDashboardLoadRange>(), call.Arg<DateTime>(), call.Arg<int>(), 400, full: true)));
        using var factory = NewHost();

        var (status, text, _) = await SendAsync(factory, Path + "?range=13mo");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= MaxResponseBytes, $"{Encoding.UTF8.GetByteCount(text)} bytes");
        Assert.DoesNotContain("processinstanceid", text, StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(400, body.RootElement.GetProperty("bucketStartsUtc").GetArrayLength());
        AssertAllListsHaveLength(body.RootElement, 400);
    }

    [Fact]
    public async Task Get_WhenTheRebuildGateIsBusy_Answers503WithRetryAfterAndNoBody()
    {
        var reader = Substitute.For<IAdminDashboardReader>();
        reader.GetLoadAsync(Arg.Any<AdminDashboardLoadRange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AdminDashboardLoadRead.BusyFor(5)));
        using var factory = NewHost(builder => builder.ConfigureTestServices(services => services.AddSingleton(reader)));

        var (status, text, headers) = await SendAsync(factory, Path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(string.Empty, text);
        Assert.Equal("5", Assert.Single(headers.GetValues("Retry-After")));
    }

    [Fact]
    public async Task Get_TwiceWithinTheTtl_ReadsTheQueryServiceOnce()
    {
        using var factory = NewHost();

        await SendAsync(factory, Path);
        await SendAsync(factory, Path);

        await _factory.AdminDashboardQuery.Received(1).GetLoadAsync(
            AdminDashboardLoadRange.Hours24, Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_AsAnonymousCaller_Answers401()
    {
        using var factory = NewHost();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsNonAdmin_Answers403()
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(false);
        using var factory = _factory.WithWebHostBuilder(_ => { });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString("N"));
        request.Headers.Add(TestAuthHandler.LoginHeader, "notanadmin");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Every property that is a JSON array must have exactly n entries (one column per bucket).
    private static void AssertAllListsHaveLength(JsonElement element, int n)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            Assert.Equal(n, element.GetArrayLength());
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            AssertAllListsHaveLength(property.Value, n);
        }
    }

    private static AdminDashboardLoad NewLoad(AdminDashboardLoadRange range, DateTime nowUtc, int samplingInterval, int buckets, bool full = false)
    {
        var width = AdminDashboardLoadRanges.GetBucketSeconds(range);
        var start = nowUtc.AddSeconds(-(long)width * buckets);
        var starts = Enumerable.Range(0, buckets).Select(i => start.AddSeconds((long)width * i)).ToList();

        // The service rounds on the server (percent 1 decimal, cores 4, load average 2, rates 2), so the
        // widest realistic value carries the maximum digits of its column, not a raw double.
        IReadOnlyList<double?> Doubles(double value, int decimals = 1) =>
            Enumerable.Range(0, buckets).Select(i => full || i != 1 ? (double?)Math.Round(value + (i / 7.0), decimals) : null).ToList();
        IReadOnlyList<long?> Longs(long value) =>
            Enumerable.Range(0, buckets).Select(i => full || i != 1 ? (long?)(value + i) : null).ToList();
        IReadOnlyList<int?> Ints(int value) =>
            Enumerable.Range(0, buckets).Select(i => full || i != 1 ? (int?)(value + i) : null).ToList();
        IReadOnlyList<string?> Text(string value) =>
            Enumerable.Range(0, buckets).Select(_ => (string?)value).ToList();
        IReadOnlyList<int> Counts(int value) => Enumerable.Repeat(value, buckets).ToList();

        var host = new AdminDashboardHostSeries(
            Doubles(12.3), Doubles(45.6), Longs(8_000_000_000), Longs(9_000_000_000), Longs(16_000_000_000),
            Doubles(1.23, 2), Doubles(2.34, 2), Text("AW"), Text("W"), Text("A"), Text("W"),
            Counts(120), Counts(118), Doubles(10.1), Doubles(11.2), Longs(4_000_000_000), Longs(4_100_000_000),
            Doubles(0.5, 2), Doubles(0.6, 2));
        AdminDashboardProcessSeries Process() => new(
            Doubles(0.1234, 4), Doubles(0.5678, 4), Doubles(0.2345, 4), Doubles(0.6789, 4), Doubles(2, 4),
            Doubles(12.3), Doubles(45.6), Longs(500_000_000), Longs(600_000_000), Longs(1_000_000_000),
            Doubles(50.1), Doubles(60.2), Counts(120));
        var workload = new AdminDashboardWorkloadSeries(
            "perMinute", Ints(40), Ints(12), Doubles(1234.56, 2), Doubles(78.9, 2), Doubles(3600), Ints(1));

        return new AdminDashboardLoad(
            buckets > 0, AdminDashboardLoadRanges.ToWire(range), AdminDashboardLoadRanges.GetSource(range) == AdminDashboardLoadSource.Raw ? "raw" : "hourly",
            width, start, nowUtc, nowUtc, buckets > 0 ? nowUtc : null, samplingInterval, starts,
            host, Process(), Process(), workload, null);
    }

    private WebApplicationFactory<Program> NewHost(Action<IWebHostBuilder>? configure = null)
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(true);
        return _factory.WithWebHostBuilder(builder => configure?.Invoke(builder));
    }

    private static async Task<(HttpStatusCode StatusCode, string Body, System.Net.Http.Headers.HttpResponseHeaders Headers)> SendAsync(
        WebApplicationFactory<Program> factory, string path)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString("N"));
        request.Headers.Add(TestAuthHandler.LoginHeader, "someadmin");
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers);
    }
}
