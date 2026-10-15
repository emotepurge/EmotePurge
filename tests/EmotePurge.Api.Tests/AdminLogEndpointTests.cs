using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.LogViewer;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The admin log viewer routes (#383, spec section 8): the 400/404/409/503 contract on the real routes
/// with the services substituted, the suppression scope around every handler, and the concurrency gate.
/// Each test gets a host of its own because the gate is a singleton with state.
/// </summary>
public class AdminLogEndpointTests : IClassFixture<ApiFactory>
{
    private static readonly DateTime FromUtc = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);

    private readonly ApiFactory _factory;

    public AdminLogEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ChannelAccess.ClearReceivedCalls();
        factory.LogQuery.ClearSubstitute();
        factory.LogStatus.ClearSubstitute();
        factory.LogFilters.ClearSubstitute();
        factory.LogQuery
            .GetPageAsync(Arg.Any<string?>(), Arg.Any<LogRangeRequest>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new LogPage([], null, FromUtc, FromUtc.AddHours(1), false)));
    }

    [Theory]
    [InlineData("GET", "/api/admin/logs")]
    [InlineData("GET", "/api/admin/logs/1")]
    [InlineData("GET", "/api/admin/logs/summary")]
    [InlineData("GET", "/api/admin/logs/status")]
    [InlineData("GET", "/api/admin/log-filters")]
    [InlineData("POST", "/api/admin/log-filters")]
    [InlineData("PUT", "/api/admin/log-filters/1")]
    [InlineData("DELETE", "/api/admin/log-filters/1")]
    public async Task EveryLogRoute_AsNonAdmin_Answers403_WithoutTouchingAService(string method, string path)
    {
        using var host = NewHost(admin: false);

        var result = await SendAsync(host, method, path, body: method is "POST" or "PUT" ? """{"name":"a","filter":""}""" : null);

        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Empty(_factory.LogQuery.ReceivedCalls());
        Assert.Empty(_factory.LogStatus.ReceivedCalls());
        Assert.Empty(_factory.LogFilters.ReceivedCalls());
    }

    [Theory]
    [InlineData("/api/admin/logs?q=level:debug")]
    [InlineData("/api/admin/logs/summary?q=level:debug")]
    public async Task InvalidFilter_Answers400_WithReasonPositionAndLength(string path)
    {
        _factory.LogQuery
            .GetPageAsync(Arg.Any<string?>(), Arg.Any<LogRangeRequest>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new LogFilterSyntaxException(LogFilterErrorReasons.InvalidLevel, 6, 5));
        _factory.LogQuery
            .GetSummaryAsync(Arg.Any<string?>(), Arg.Any<LogRangeRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new LogFilterSyntaxException(LogFilterErrorReasons.InvalidLevel, 6, 5));
        using var host = NewHost();

        var result = await SendAsync(host, "GET", path);

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        var root = result.Json.RootElement;
        Assert.Equal("invalid_log_filter", root.GetProperty("errorCode").GetString());
        Assert.Equal("invalid_level", root.GetProperty("reason").GetString());
        Assert.Equal(6, root.GetProperty("position").GetInt32());
        Assert.Equal(5, root.GetProperty("length").GetInt32());
    }

    [Fact]
    public async Task InvalidRange_FromTheService_Answers400InvalidLogRange()
    {
        _factory.LogQuery
            .GetPageAsync(Arg.Any<string?>(), Arg.Any<LogRangeRequest>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new LogRangeException("mixed"));
        using var host = NewHost();

        var result = await SendAsync(host, "GET", "/api/admin/logs?range=2d");

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal("invalid_log_range", result.Json.RootElement.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData("/api/admin/logs?from=garbage&to=2026-10-14T11:00:00Z")]
    [InlineData("/api/admin/logs?keysOnly=true")]
    [InlineData("/api/admin/logs?ids=")]
    [InlineData("/api/admin/logs?ids=1,,2")]
    [InlineData("/api/admin/logs?ids=1,abc")]
    [InlineData("/api/admin/logs?ids=0")]
    [InlineData("/api/admin/logs?ids=-5")]
    public async Task MalformedRangeOrIds_Answer400InvalidLogRange_WithoutCallingTheService(string path)
    {
        using var host = NewHost();

        var result = await SendAsync(host, "GET", path);

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal("invalid_log_range", result.Json.RootElement.GetProperty("errorCode").GetString());
        Assert.Empty(_factory.LogQuery.ReceivedCalls());
    }

    [Fact]
    public async Task MoreThanTwoHundredIds_Answers400InvalidLogRange()
    {
        using var host = NewHost();
        var ids = string.Join(',', Enumerable.Range(1, 201));

        var result = await SendAsync(host, "GET", $"/api/admin/logs?ids={ids}");

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal("invalid_log_range", result.Json.RootElement.GetProperty("errorCode").GetString());
        Assert.Empty(_factory.LogQuery.ReceivedCalls());
    }

    [Fact]
    public async Task Ids_AreParsedAndAnswerTheListShape()
    {
        _factory.LogQuery.GetByIdsAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<LogEntryView>>([NewEntry(7)]));
        using var host = NewHost();

        var result = await SendAsync(host, "GET", "/api/admin/logs?ids=7,3");

        Assert.Equal(HttpStatusCode.OK, result.Status);
        await _factory.LogQuery.Received(1).GetByIdsAsync(
            Arg.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(new long[] { 7, 3 })), Arg.Any<CancellationToken>());
        var root = result.Json.RootElement;
        Assert.Single(root.GetProperty("entries").EnumerateArray());
        Assert.Equal(7, root.GetProperty("entries")[0].GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Page_ForwardsTheQueryAndShapesTheEntries()
    {
        var entry = NewEntry(42) with { PropertiesJson = """{"channel":"abc","count":3}""" };
        _factory.LogQuery
            .GetPageAsync(Arg.Any<string?>(), Arg.Any<LogRangeRequest>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LogPage([entry, NewEntry(41) with { PropertiesJson = null }], "next", FromUtc, FromUtc.AddHours(1), false)));
        using var host = NewHost();

        var result = await SendAsync(host, "GET", "/api/admin/logs?q=cat:Foo&range=7d&before=abc&limit=50");

        Assert.Equal(HttpStatusCode.OK, result.Status);
        await _factory.LogQuery.Received(1).GetPageAsync(
            "cat:Foo", Arg.Is<LogRangeRequest>(r => r.Range == "7d" && r.FromUtc == null && r.ToUtc == null), "abc", 50, Arg.Any<CancellationToken>());
        var root = result.Json.RootElement;
        Assert.Equal("next", root.GetProperty("nextCursor").GetString());
        Assert.Equal("2026-10-14T10:00:00Z", root.GetProperty("rangeFromUtc").GetString());
        var first = root.GetProperty("entries")[0];
        Assert.Equal("warn", first.GetProperty("level").GetString());
        Assert.Equal("worker", first.GetProperty("source").GetString());
        Assert.Equal("abc", first.GetProperty("properties").GetProperty("channel").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("entries")[1].GetProperty("properties").ValueKind);
        Assert.Empty(root.GetProperty("entries")[1].GetProperty("properties").EnumerateObject());
        Assert.False(first.TryGetProperty("exception", out _));
    }

    [Fact]
    public async Task KeysOnly_ShapeAndLimit()
    {
        _factory.LogQuery.GetKeysAsync(Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LogKeyPage([new LogEntryKey(9, FromUtc), new LogEntryKey(8, FromUtc)], FromUtc, FromUtc.AddHours(2), true)));
        using var host = NewHost();

        var result = await SendAsync(host, "GET", "/api/admin/logs?keysOnly=true&from=2026-10-14T10:00:00Z&q=level:error");

        Assert.Equal(HttpStatusCode.OK, result.Status);
        await _factory.LogQuery.Received(1).GetKeysAsync(
            "level:error", Arg.Is<DateTime>(d => d == FromUtc && d.Kind == DateTimeKind.Utc), null, Arg.Any<CancellationToken>());
        var root = result.Json.RootElement;
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nextCursor").ValueKind);
        var entries = root.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal(["id", "timestampUtc"], entries[0].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("2026-10-14T10:00:00Z", entries[0].GetProperty("timestampUtc").GetString());
        Assert.Equal("2026-10-14T12:00:00Z", root.GetProperty("rangeToUtc").GetString());
    }

    [Fact]
    public async Task Detail_AddsTheExceptionAndTheProcessStart_AndAnUnknownIdIs404()
    {
        _factory.LogQuery.GetEntryAsync(5, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LogEntryDetailView?>(new LogEntryDetailView(NewEntry(5), "boom", FromUtc.AddHours(-1), 3600)));
        _factory.LogQuery.GetEntryAsync(6, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LogEntryDetailView?>(null));
        using var host = NewHost();

        var found = await SendAsync(host, "GET", "/api/admin/logs/5");
        var missing = await SendAsync(host, "GET", "/api/admin/logs/6");

        Assert.Equal(HttpStatusCode.OK, found.Status);
        Assert.Equal("boom", found.Json.RootElement.GetProperty("exception").GetString());
        Assert.Equal(3600, found.Json.RootElement.GetProperty("uptimeSeconds").GetInt64());
        Assert.Equal("2026-10-14T09:00:00Z", found.Json.RootElement.GetProperty("processStartedUtc").GetString());
        Assert.Equal(5, found.Json.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Equal("log_entry_not_found", missing.Json.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Summary_UsesTheWireTokens()
    {
        _factory.LogQuery.GetSummaryAsync(Arg.Any<string?>(), Arg.Any<LogRangeRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LogSummary(
                FromUtc, FromUtc.AddHours(1), FromUtc, FromUtc.AddHours(1), 600, [FromUtc], [1], [2], [3], 6,
                new LogFacets([new LogFacetValue("api", 6)], [new LogFacetValue("info", 1)], [new LogCategoryFacetValue("A.B", "B", 6)], 0),
                new LogDropTotals(10, 1, 2, 3, 4, 5, 6),
                [new LogProcessStart(LogSource.Worker, "inst", FromUtc)])));
        using var host = NewHost();

        var result = await SendAsync(host, "GET", "/api/admin/logs/summary");

        Assert.Equal(HttpStatusCode.OK, result.Status);
        var root = result.Json.RootElement;
        Assert.Equal(6, root.GetProperty("total").GetInt32());
        Assert.Equal(3, root.GetProperty("counts").GetProperty("error")[0].GetInt32());
        Assert.Equal(6, root.GetProperty("dropped").GetProperty("rejected").GetInt64());
        Assert.Equal("worker", root.GetProperty("processStarts")[0].GetProperty("source").GetString());
        Assert.Equal("B", root.GetProperty("facets").GetProperty("category")[0].GetProperty("short").GetString());
        Assert.Equal(600, root.GetProperty("bucketSeconds").GetInt32());
    }

    [Fact]
    public async Task Status_AlwaysListsApiAndWorker()
    {
        _factory.LogStatus.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new LogCaptureStatusView(
            FromUtc,
            [
                new LogSourceStatus(LogSource.Api, LogSourceState.Healthy, "i1", FromUtc, FromUtc),
                new LogSourceStatus(LogSource.Worker, LogSourceState.Missing, null, null, null),
            ],
            new LogCoverage(false, 4, [new LogIncompleteSince(LogSource.Worker, FromUtc)]),
            new LogRetentionStatus(null, false))));
        using var host = NewHost();

        var result = await SendAsync(host, "GET", "/api/admin/logs/status");

        Assert.Equal(HttpStatusCode.OK, result.Status);
        var sources = result.Json.RootElement.GetProperty("sources");
        Assert.Equal(["api", "worker"], sources.EnumerateArray().Select(s => s.GetProperty("source").GetString()!).ToArray());
        Assert.Equal("healthy", sources[0].GetProperty("state").GetString());
        Assert.Equal("missing", sources[1].GetProperty("state").GetString());
        var coverage = result.Json.RootElement.GetProperty("coverage24h");
        Assert.False(coverage.GetProperty("complete").GetBoolean());
        Assert.Equal("worker", coverage.GetProperty("incompleteSince")[0].GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, result.Json.RootElement.GetProperty("retention").GetProperty("oldestEntryUtc").ValueKind);
    }

    [Fact]
    public async Task Gate_ThirdConcurrentQuery_Gets503WithRetryAfter5AndNoBody()
    {
        var release = new TaskCompletionSource<LogPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.LogQuery
            .GetPageAsync(Arg.Any<string?>(), Arg.Any<LogRangeRequest>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => release.Task);
        using var host = NewHost(gate: new LogQueryConcurrencyGate(2, TimeSpan.FromMilliseconds(200)));

        _factory.LogQuery
            .GetSummaryAsync(Arg.Any<string?>(), Arg.Any<LogRangeRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new TaskCompletionSource<LogSummary>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var first = SendAsync(host, "GET", "/api/admin/logs");
        var second = SendAsync(host, "GET", "/api/admin/logs/summary?q=x");
        await WaitUntilAsync(() => _factory.LogQuery.ReceivedCalls().Count() >= 2);

        var third = await SendAsync(host, "GET", "/api/admin/logs");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, third.Status);
        Assert.Equal("5", third.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
        Assert.Equal(string.Empty, third.Text);
        release.SetResult(new LogPage([], null, FromUtc, FromUtc, false));
        Assert.Equal(HttpStatusCode.OK, (await first).Status);
        _ = second;
    }

    [Fact]
    public async Task Status_NotGated()
    {
        var release = new TaskCompletionSource<LogPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.LogQuery
            .GetPageAsync(Arg.Any<string?>(), Arg.Any<LogRangeRequest>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => release.Task);
        _factory.LogStatus.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new LogCaptureStatusView(
            FromUtc,
            [new LogSourceStatus(LogSource.Api, LogSourceState.Healthy, null, null, null), new LogSourceStatus(LogSource.Worker, LogSourceState.Missing, null, null, null)],
            new LogCoverage(true, 0, []),
            new LogRetentionStatus(null, false))));
        _factory.LogFilters.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<LogFilterView>>([]));
        using var host = NewHost(gate: new LogQueryConcurrencyGate(1, TimeSpan.FromMilliseconds(100)));

        var hanging = SendAsync(host, "GET", "/api/admin/logs");
        await WaitUntilAsync(() => _factory.LogQuery.ReceivedCalls().Any());

        var status = await SendAsync(host, "GET", "/api/admin/logs/status");
        var filters = await SendAsync(host, "GET", "/api/admin/log-filters");
        var gated = await SendAsync(host, "GET", "/api/admin/logs");

        Assert.Equal(HttpStatusCode.OK, status.Status);
        Assert.Equal(HttpStatusCode.OK, filters.Status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, gated.Status);
        release.SetResult(new LogPage([], null, FromUtc, FromUtc, false));
        await hanging;
    }

    [Fact]
    public async Task ScopeFilter_HandlerSeesSuppressionActive()
    {
        var seen = new List<bool>();
        _factory.LogQuery
            .GetPageAsync(Arg.Any<string?>(), Arg.Any<LogRangeRequest>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                seen.Add(LogCaptureSuppression.IsActive);
                return Task.FromResult(new LogPage([], null, FromUtc, FromUtc, false));
            });
        using var host = NewHost();

        var result = await SendAsync(host, "GET", "/api/admin/logs");

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal([true], seen);
        Assert.False(LogCaptureSuppression.IsActive);
    }

    [Theory]
    [InlineData("GET", "/api/admin/log-filters", null)]
    [InlineData("POST", "/api/admin/log-filters", """{"name":"a","filter":""}""")]
    [InlineData("PUT", "/api/admin/log-filters/1", """{"name":"a","filter":""}""")]
    [InlineData("DELETE", "/api/admin/log-filters/1", null)]
    public async Task ScopeFilter_FilterRoutesRunSuppressedToo(string method, string path, string? body)
    {
        var seen = new List<bool>();
        var view = new LogFilterView(1, "a", "", 0);
        _factory.LogFilters.ListAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { seen.Add(LogCaptureSuppression.IsActive); return Task.FromResult<IReadOnlyList<LogFilterView>>([]); });
        _factory.LogFilters.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { seen.Add(LogCaptureSuppression.IsActive); return Task.FromResult(new LogFilterWriteResult(LogFilterWriteStatus.Ok, view)); });
        _factory.LogFilters.UpdateAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { seen.Add(LogCaptureSuppression.IsActive); return Task.FromResult(new LogFilterWriteResult(LogFilterWriteStatus.Ok, view)); });
        _factory.LogFilters.DeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => { seen.Add(LogCaptureSuppression.IsActive); return Task.FromResult(true); });
        using var host = NewHost();

        var result = await SendAsync(host, method, path, body);

        Assert.True(result.Status is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent, result.Status.ToString());
        Assert.Equal([true], seen);
    }

    [Fact]
    public async Task ScopeFilter_Timeout_Is503LogQueryTimeout_WithoutMiddlewareLog()
    {
        _factory.LogQuery
            .GetPageAsync(Arg.Any<string?>(), Arg.Any<LogRangeRequest>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new LogQueryTimeoutException());
        var captured = new CapturingLoggerProvider();
        using var host = NewHost(captured: captured);

        var result = await SendAsync(host, "GET", "/api/admin/logs");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
        Assert.Equal("log_query_timeout", result.Json.RootElement.GetProperty("errorCode").GetString());
        Assert.Empty(captured.OutsideViewerCategory());
    }

    [Fact]
    public async Task ScopeFilter_OtherException_Is500UnexpectedError_WithoutMiddlewareLog()
    {
        _factory.LogQuery
            .GetPageAsync(Arg.Any<string?>(), Arg.Any<LogRangeRequest>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        var captured = new CapturingLoggerProvider();
        using var host = NewHost(captured: captured);

        var result = await SendAsync(host, "GET", "/api/admin/logs");

        Assert.Equal(HttpStatusCode.InternalServerError, result.Status);
        Assert.Equal("unexpected_error", result.Json.RootElement.GetProperty("errorCode").GetString());
        Assert.Empty(captured.OutsideViewerCategory());
    }

    [Fact]
    public async Task FilterOrder_AdminBeforeScope()
    {
        using var host = NewHost(admin: false);

        var result = await SendAsync(host, "GET", "/api/admin/logs?q=level:debug");

        // A non-admin never reaches the scope filter, so the service is not asked and no query error leaks.
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Empty(_factory.LogQuery.ReceivedCalls());
    }

    [Fact]
    public async Task LogFilters_ListCreateUpdateDelete_MapTheServiceOutcomes()
    {
        var view = new LogFilterView(3, "Errors", "level:error", 1);
        _factory.LogFilters.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<LogFilterView>>([view]));
        _factory.LogFilters.CreateAsync("Errors", "level:error", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LogFilterWriteResult(LogFilterWriteStatus.Ok, view)));
        _factory.LogFilters.UpdateAsync(3, "Errors", "level:error", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LogFilterWriteResult(LogFilterWriteStatus.Ok, view)));
        _factory.LogFilters.DeleteAsync(3, Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        using var host = NewHost();
        const string body = """{"name":"Errors","filter":"level:error"}""";

        var list = await SendAsync(host, "GET", "/api/admin/log-filters");
        var created = await SendAsync(host, "POST", "/api/admin/log-filters", body);
        var updated = await SendAsync(host, "PUT", "/api/admin/log-filters/3", body);
        var deleted = await SendAsync(host, "DELETE", "/api/admin/log-filters/3");

        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal(["id", "name", "filter", "sortOrder"], list.Json.RootElement[0].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal(3, created.Json.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal(HttpStatusCode.NoContent, deleted.Status);
    }

    [Theory]
    [InlineData(LogFilterWriteStatus.InvalidName, 400, "invalid_log_filter_name")]
    [InlineData(LogFilterWriteStatus.InvalidFilter, 400, "invalid_log_filter")]
    [InlineData(LogFilterWriteStatus.NameTaken, 409, "log_filter_name_taken")]
    [InlineData(LogFilterWriteStatus.LimitReached, 409, "log_filter_limit_reached")]
    [InlineData(LogFilterWriteStatus.NotFound, 404, "log_filter_not_found")]
    public async Task LogFilterWrites_MapEveryFailureToItsStatusAndCode(LogFilterWriteStatus status, int http, string code)
    {
        var outcome = new LogFilterWriteResult(status, null, "invalid_level", 2, 4);
        _factory.LogFilters.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(outcome));
        _factory.LogFilters.UpdateAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(outcome));
        using var host = NewHost();
        const string body = """{"name":"x","filter":"y"}""";

        var post = await SendAsync(host, "POST", "/api/admin/log-filters", body);
        var put = await SendAsync(host, "PUT", "/api/admin/log-filters/1", body);

        foreach (var result in new[] { post, put })
        {
            Assert.Equal(http, (int)result.Status);
            Assert.Equal(code, result.Json.RootElement.GetProperty("errorCode").GetString());
        }

        if (status == LogFilterWriteStatus.InvalidFilter)
        {
            Assert.Equal("invalid_level", post.Json.RootElement.GetProperty("reason").GetString());
            Assert.Equal(2, post.Json.RootElement.GetProperty("position").GetInt32());
            Assert.Equal(4, post.Json.RootElement.GetProperty("length").GetInt32());
        }
    }

    [Fact]
    public async Task DeleteFilter_Missing_Answers404()
    {
        _factory.LogFilters.DeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        using var host = NewHost();

        var result = await SendAsync(host, "DELETE", "/api/admin/log-filters/9");

        Assert.Equal(HttpStatusCode.NotFound, result.Status);
        Assert.Equal("log_filter_not_found", result.Json.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task CreateFilter_WithAnUnreadableBody_Answers400InvalidRequestBody()
    {
        using var host = NewHost();

        var result = await SendAsync(host, "POST", "/api/admin/log-filters", "{not json");

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal("invalid_request_body", result.Json.RootElement.GetProperty("errorCode").GetString());
    }

    private static LogEntryView NewEntry(long id) =>
        new(id, FromUtc, LogEntryLevel.Warning, LogSource.Worker, "inst", "EmotePurge.Worker.X", 12, "Evt", "msg", false, null, null, false);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time.");
            await Task.Delay(10);
        }
    }

    private WebApplicationFactory<Program> NewHost(bool admin = true, LogQueryConcurrencyGate? gate = null, CapturingLoggerProvider? captured = null)
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(admin);
        return _factory.WithWebHostBuilder(builder =>
        {
            if (captured is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(captured));
            }

            builder.ConfigureTestServices(services =>
            {
                if (gate is not null)
                {
                    services.AddSingleton(gate);
                }
            });
        });
    }

    private static async Task<Result> SendAsync(WebApplicationFactory<Program> host, string method, string path, string? body = null)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString("N"));
        request.Headers.Add(TestAuthHandler.LoginHeader, "someadmin");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = text.Length > 0 && text[0] is '{' or '[' ? JsonDocument.Parse(text) : JsonDocument.Parse("null");
        return new Result(response.StatusCode, response.Headers, text, json);
    }

    private sealed record Result(HttpStatusCode Status, System.Net.Http.Headers.HttpResponseHeaders Headers, string Text, JsonDocument Json);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _categories = [];

        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, _categories);

        public IReadOnlyList<string> OutsideViewerCategory()
        {
            lock (_categories)
            {
                return _categories.Where(c => c != "EmotePurge.Infrastructure.Logging.ViewerQueries").ToList();
            }
        }

        public void Dispose()
        {
        }

        private sealed class Capture(string category, List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    lock (sink)
                    {
                        sink.Add(category);
                    }
                }
            }
        }
    }
}
