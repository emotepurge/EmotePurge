using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The handler contract of the three chat-log backfill routes (#351, spec 5.1-5.4): the flag, the
/// body checks that precede the service, the mapping of every service outcome to its status and
/// error code, the wire shape of the status read, and the permissions flag. The authorization
/// matrix (401/400/403/success) lives in <c>AuthFilterMatrixTests</c>, the rate-limit policies in
/// <c>EmoteRoutePolicyTests</c>.
/// </summary>
public class ChatLogBackfillEndpointsTests : IClassFixture<ApiFactory>
{
    private const string Channel = "handofblood";
    private const string SetId = "01FRY81K4800085N93FNKSBYXS";
    private const string ValidBody = """{"emoteSetId":"01FRY81K4800085N93FNKSBYXS","months":3}""";

    private readonly ApiFactory _factory;

    public ChatLogBackfillEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.ChatLogBackfill.ClearReceivedCalls();
        _factory.ChannelAccess.ClearReceivedCalls();

        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    // ---- The flag (D17) ----

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task FlagOff_Answers404BackfillDisabled_ForAManager_AndNeverCallsTheService(string method)
    {
        using var disabled = _factory.WithBackfillDisabled();

        var response = await SendAsync(disabled, method, ValidBody);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.BackfillDisabled, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.ChatLogBackfill.ReceivedCalls());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task FlagOff_StillAnswers403_ForACallerWhoCannotManage_SoTheFeatureIsNotRevealed(string method)
    {
        // The filter runs first: a 7TV editor never learns whether the feature exists.
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        using var disabled = _factory.WithBackfillDisabled();

        var response = await SendAsync(disabled, method, ValidBody);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task FlagOff_ChecksTheFlag_BeforeTheBody()
    {
        // A disabled feature must not leak its validation ladder either.
        using var disabled = _factory.WithBackfillDisabled();

        var response = await SendAsync(disabled, "POST", "{}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.BackfillDisabled, await ReadErrorCodeAsync(response));
    }

    // ---- GET ----

    [Fact]
    public async Task Get_Answers404ChannelNotFound_WhenTheServiceKnowsNoSuchChannel()
    {
        _factory.ChatLogBackfill.GetStatusAsync(Channel, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((ChatLogBackfillStatusDto?)null);

        var response = await SendAsync(_factory, "GET");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ChannelNotFound, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Get_PassesTheUtcDateOfTheRequest_ToTheService()
    {
        _factory.ChatLogBackfill.GetStatusAsync(Channel, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(BackfillTestData.Status());
        var before = DateOnly.FromDateTime(DateTime.UtcNow);

        var response = await SendAsync(_factory, "GET");

        var after = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var today = Assert.Single(_factory.ChatLogBackfill.ReceivedCalls()).GetArguments()[1];
        Assert.InRange((DateOnly)today!, before, after);
    }

    [Fact]
    public async Task Get_SerializesTheWireShapeOfSpec51_CamelCaseDatesAndZTimestamps()
    {
        // The settings tab reads this by hand-written TypeScript; field names, ISO dates and UTC
        // timestamps with a Z are the contract (spec 5.1).
        _factory.ChatLogBackfill.GetStatusAsync(Channel, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(BackfillTestData.Status());

        var response = await SendAsync(_factory, "GET");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(
            ["countingSince", "archive", "requestDelaySeconds", "options", "activeEmoteSetId", "coverage",
                "activeRun", "lastRun", "importedFrom", "importedTo", "importedContiguous", "cooldownUntilUtc"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-10-08", root.GetProperty("countingSince").GetString());
        Assert.Equal("logs.cyex.app", root.GetProperty("archive").GetProperty("name").GetString());
        Assert.Equal("https://logs.cyex.app/", root.GetProperty("archive").GetProperty("url").GetString());
        Assert.Equal(10, root.GetProperty("requestDelaySeconds").GetInt32());
        Assert.Equal("01HQ0000000000000000000000", root.GetProperty("activeEmoteSetId").GetString());
        Assert.Equal("2026-07-08", root.GetProperty("importedFrom").GetString());
        Assert.Equal("2026-10-08", root.GetProperty("importedTo").GetString());
        Assert.True(root.GetProperty("importedContiguous").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cooldownUntilUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("activeRun").ValueKind);

        var option = root.GetProperty("options")[0];
        Assert.Equal(
            ["months", "windowFrom", "windowTo", "days", "weeks", "available", "reason"],
            option.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-09-08", option.GetProperty("windowFrom").GetString());

        var interval = root.GetProperty("coverage")[0];
        Assert.Equal(
            ["from", "to", "emoteSetId", "archiveHost", "emoteSetName"],
            interval.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-07-08", interval.GetProperty("from").GetString());
        Assert.Equal("2026-10-08", interval.GetProperty("to").GetString());

        var run = root.GetProperty("lastRun");
        Assert.Equal(
            ["id", "status", "requestedMonths", "windowFrom", "windowTo", "weeksDone", "weeksTotal", "queuePosition",
                "pausedUntilUtc", "requestedAtUtc", "startedAtUtc", "finishedAtUtc", "requestedByLogin", "emoteSetId",
                "emoteSetName", "emoteCount", "errorCode", "errorHttpStatus", "bytesReceived", "messagesRead"],
            run.EnumerateObject().Select(p => p.Name));
        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.Equal("2026-10-09T18:02:11Z", run.GetProperty("requestedAtUtc").GetString());
        Assert.Equal("2026-10-09T18:05:40Z", run.GetProperty("finishedAtUtc").GetString());
        Assert.Equal(812, run.GetProperty("emoteCount").GetInt32());
        Assert.Equal("Normal", run.GetProperty("emoteSetName").GetString());
    }

    // ---- POST: what is checked before the service ----

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"emoteSetId":"01FRY81K4800085N93FNKSBYXS"}""")]
    [InlineData("""{"emoteSetId":"01FRY81K4800085N93FNKSBYXS","months":null}""")]
    [InlineData("""{"emoteSetId":"01FRY81K4800085N93FNKSBYXS","months":0}""")]
    [InlineData("""{"emoteSetId":"01FRY81K4800085N93FNKSBYXS","months":2}""")]
    [InlineData("""{"emoteSetId":"01FRY81K4800085N93FNKSBYXS","months":12}""")]
    [InlineData("""{"emoteSetId":"01FRY81K4800085N93FNKSBYXS","months":-3}""")]
    public async Task Post_Answers400MonthsInvalid_ForAMissingOrUnofferedMonths_WithoutCallingTheService(string body)
    {
        var response = await SendAsync(_factory, "POST", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.BackfillMonthsInvalid, await ReadErrorCodeAsync(response));
        await AssertNothingEnqueuedAsync();
    }

    [Fact]
    public async Task Post_Answers400MonthsInvalid_ForAnEmptyBody()
    {
        // A JSON content type with no content; a request with no content type at all never reaches the
        // handler (routing rejects the JSON-only endpoint and the /api fallback answers 404).
        var response = await SendAsync(_factory, "POST", body: "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.BackfillMonthsInvalid, await ReadErrorCodeAsync(response));
        await AssertNothingEnqueuedAsync();
    }

    [Theory]
    [InlineData("""{"months":3}""")]
    [InlineData("""{"emoteSetId":null,"months":3}""")]
    [InlineData("""{"emoteSetId":"","months":3}""")]
    [InlineData("""{"emoteSetId":"has space","months":3}""")]
    [InlineData("""{"emoteSetId":"../etc","months":3}""")]
    [InlineData("""{"emoteSetId":"0123456789012345678901234567890123456789","months":3}""")]
    public async Task Post_Answers400InvalidEmoteSetId_ForAMissingOrMalformedSetId_WithoutCallingTheService(string body)
    {
        var response = await SendAsync(_factory, "POST", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidEmoteSetId, await ReadErrorCodeAsync(response));
        await AssertNothingEnqueuedAsync();
    }

    [Fact]
    public async Task Post_Answers400InvalidRequestBody_ForABodyThatIsNotTheContract()
    {
        // months as a word is not a binding the framework can make: the global handler's 400, not a 500.
        var response = await SendAsync(_factory, "POST", """{"emoteSetId":"01FRY81K4800085N93FNKSBYXS","months":"six"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
        await AssertNothingEnqueuedAsync();
    }

    [Fact]
    public async Task Post_ForwardsChannelSetMonthsTodayAndTheActor_AndAnswers202WithTheRun()
    {
        var run = BackfillTestData.Run(status: "queued", queuePosition: 1);
        _factory.ChatLogBackfill.EnqueueAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DateOnly>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChatLogBackfillEnqueueResult.Enqueued(run));
        var userId = NewUserId();
        var before = DateOnly.FromDateTime(DateTime.UtcNow);

        var response = await SendAsync(_factory, "POST", ValidBody, userId);

        var after = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("queued", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("queuePosition").GetInt32());
        Assert.Equal(SetId, json.RootElement.GetProperty("emoteSetId").GetString());

        var call = Assert.Single(_factory.ChatLogBackfill.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IChatLogBackfillService.EnqueueAsync));
        var args = call.GetArguments();
        Assert.Equal(Channel, args[0]);
        Assert.Equal(SetId, args[1]);
        Assert.Equal(3, args[2]);
        Assert.InRange((DateOnly)args[3]!, before, after);
        Assert.Equal(userId, ((AuditActor)args[4]!).TwitchUserId);
    }

    // ---- POST: every service outcome ----

    public static TheoryData<ChatLogBackfillEnqueueStatus, HttpStatusCode, string?> EnqueueOutcomes => new()
    {
        { ChatLogBackfillEnqueueStatus.NotFound, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotFound },
        { ChatLogBackfillEnqueueStatus.NotActive, HttpStatusCode.Conflict, ApiErrorCodes.ChannelNotJoined },
        { ChatLogBackfillEnqueueStatus.ChannelExcluded, HttpStatusCode.Conflict, ApiErrorCodes.ChannelExcluded },
        { ChatLogBackfillEnqueueStatus.TwitchIdUnknown, HttpStatusCode.Conflict, ApiErrorCodes.BackfillTwitchIdUnknown },
        { ChatLogBackfillEnqueueStatus.ChannelGone, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotFound },
        { ChatLogBackfillEnqueueStatus.ChannelIdentityChanged, HttpStatusCode.Conflict, ApiErrorCodes.BackfillChannelIdentityChanged },
        { ChatLogBackfillEnqueueStatus.RequesterGone, HttpStatusCode.Unauthorized, null },
        { ChatLogBackfillEnqueueStatus.WindowEmpty, HttpStatusCode.Conflict, ApiErrorCodes.BackfillWindowEmpty },
        { ChatLogBackfillEnqueueStatus.AlreadyActive, HttpStatusCode.Conflict, ApiErrorCodes.BackfillAlreadyActive },
        { ChatLogBackfillEnqueueStatus.MonthsInvalid, HttpStatusCode.BadRequest, ApiErrorCodes.BackfillMonthsInvalid },
        { ChatLogBackfillEnqueueStatus.SetNotMember, HttpStatusCode.NotFound, ApiErrorCodes.EmoteSetNotFound },
        { ChatLogBackfillEnqueueStatus.SetEmpty, HttpStatusCode.Conflict, ApiErrorCodes.BackfillSetEmpty },
        { ChatLogBackfillEnqueueStatus.SetTruncated, HttpStatusCode.Conflict, ApiErrorCodes.BackfillSetTruncated },
        { ChatLogBackfillEnqueueStatus.SevenTvUnavailable, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelSevenTvUnavailable },
    };

    [Theory]
    [MemberData(nameof(EnqueueOutcomes))]
    public async Task Post_MapsEveryNonEnqueuedOutcome_ToItsStatusAndErrorCode(
        ChatLogBackfillEnqueueStatus status, HttpStatusCode expected, string? errorCode)
    {
        _factory.ChatLogBackfill.EnqueueAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DateOnly>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(status == ChatLogBackfillEnqueueStatus.SevenTvUnavailable
                ? ChatLogBackfillEnqueueResult.SevenTvUnavailable(null)
                : ChatLogBackfillEnqueueResult.Failed(status));

        var response = await SendAsync(_factory, "POST", ValidBody);

        Assert.Equal(expected, response.StatusCode);
        if (errorCode is null)
        {
            // RequesterGone: a bare 401, like a revoked session.
            Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
        }
        else
        {
            Assert.Equal(errorCode, await ReadErrorCodeAsync(response));
        }
    }

    [Fact]
    public void EveryEnqueueStatus_HasAMappingCase_InThisTheory()
    {
        // Enqueued is the 202 test above; every other member must be in the outcome table, so a new
        // status cannot slip into the service without a row here (and a case in the handler's switch).
        var covered = EnqueueOutcomes.Select(row => (ChatLogBackfillEnqueueStatus)row[0]).ToHashSet();
        var all = Enum.GetValues<ChatLogBackfillEnqueueStatus>().Where(s => s != ChatLogBackfillEnqueueStatus.Enqueued);

        Assert.True(covered.SetEquals(all));
    }

    [Theory]
    [InlineData(7, "7")]
    [InlineData(7.2, "8")]
    [InlineData(0.2, "1")]
    public async Task Post_503_CarriesRetryAfterInWholeSeconds_WhenSevenTvSentOne(double seconds, string expectedHeader)
    {
        _factory.ChatLogBackfill.EnqueueAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DateOnly>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChatLogBackfillEnqueueResult.SevenTvUnavailable(TimeSpan.FromSeconds(seconds)));

        var response = await SendAsync(_factory, "POST", ValidBody);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(expectedHeader, Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal(ApiErrorCodes.ForeignChannelSevenTvUnavailable, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Post_503_HasNoRetryAfter_WhenSevenTvSentNone()
    {
        _factory.ChatLogBackfill.EnqueueAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DateOnly>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChatLogBackfillEnqueueResult.SevenTvUnavailable(null));

        var response = await SendAsync(_factory, "POST", ValidBody);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    // ---- DELETE ----

    [Fact]
    public void EveryCancelResult_HasAMappingCase_InTheDeleteTheory()
    {
        var covered = typeof(ChatLogBackfillEndpointsTests)
            .GetMethod(nameof(Delete_MapsEveryCancelResult))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false)
            .Cast<InlineDataAttribute>()
            .Select(a => (ChatLogBackfillCancelResult)a.GetData(null!)!.First()[0]!)
            .ToHashSet();

        Assert.True(covered.SetEquals(Enum.GetValues<ChatLogBackfillCancelResult>()));
    }

    [Theory]
    [InlineData(ChatLogBackfillCancelResult.Cancelled, HttpStatusCode.NoContent, null)]
    [InlineData(ChatLogBackfillCancelResult.NoActiveRun, HttpStatusCode.NotFound, "backfill_no_active_run")]
    [InlineData(ChatLogBackfillCancelResult.NotFound, HttpStatusCode.NotFound, "channel_not_found")]
    public async Task Delete_MapsEveryCancelResult(ChatLogBackfillCancelResult result, HttpStatusCode expected, string? errorCode)
    {
        _factory.ChatLogBackfill.CancelAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(result);
        var userId = NewUserId();

        var response = await SendAsync(_factory, "DELETE", userId: userId);

        Assert.Equal(expected, response.StatusCode);
        if (errorCode is not null)
        {
            Assert.Equal(errorCode, await ReadErrorCodeAsync(response));
        }

        var call = Assert.Single(_factory.ChatLogBackfill.ReceivedCalls());
        Assert.Equal(Channel, call.GetArguments()[0]);
        Assert.Equal(userId, ((AuditActor)call.GetArguments()[1]!).TwitchUserId);
    }

    // ---- /permissions (5.4) ----

    [Fact]
    public async Task Permissions_CarryTheBackfillFlag_AsConfigured()
    {
        using var disabled = _factory.WithBackfillDisabled();

        using var off = JsonDocument.Parse(await (await SendAsync(disabled, "GET", path: $"/api/channels/{Channel}/permissions")).Content.ReadAsStringAsync());
        using var on = JsonDocument.Parse(await (await SendAsync(_factory, "GET", path: $"/api/channels/{Channel}/permissions")).Content.ReadAsStringAsync());

        Assert.False(off.RootElement.GetProperty("chatLogBackfillEnabled").GetBoolean());
        Assert.True(on.RootElement.GetProperty("chatLogBackfillEnabled").GetBoolean());
    }

    private static string NewUserId() => Guid.NewGuid().ToString("N");

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }

    private async Task AssertNothingEnqueuedAsync() =>
        await _factory.ChatLogBackfill.DidNotReceiveWithAnyArgs()
            .EnqueueAsync(default!, default!, default, default, default!, default);

    private static async Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> factory, string method, string? body = null, string? userId = null, string? path = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var request = new HttpRequestMessage(new HttpMethod(method), path ?? $"/api/channels/{Channel}/backfill");
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId ?? NewUserId());
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }
}

/// <summary>Canned service answers for the backfill route tests (values from the examples of spec 5.1).</summary>
internal static class BackfillTestData
{
    public static ChatLogBackfillRunDto Run(string status = "completed", int? queuePosition = null) => new(
        Id: 12,
        Status: status,
        RequestedMonths: 3,
        WindowFrom: new DateOnly(2026, 7, 8),
        WindowTo: new DateOnly(2026, 10, 8),
        WeeksDone: status == "completed" ? 14 : 0,
        WeeksTotal: 14,
        QueuePosition: queuePosition,
        PausedUntilUtc: null,
        RequestedAtUtc: new DateTime(2026, 10, 9, 18, 2, 11, DateTimeKind.Utc),
        StartedAtUtc: new DateTime(2026, 10, 9, 18, 2, 13, DateTimeKind.Utc),
        FinishedAtUtc: status == "completed" ? new DateTime(2026, 10, 9, 18, 5, 40, DateTimeKind.Utc) : null,
        RequestedByLogin: "somemod",
        EmoteSetId: "01FRY81K4800085N93FNKSBYXS",
        EmoteSetName: "Normal",
        EmoteCount: 812,
        ErrorCode: null,
        ErrorHttpStatus: null,
        BytesReceived: 123456789,
        MessagesRead: 345678);

    public static ChatLogBackfillStatusDto Status() => new(
        CountingSince: new DateOnly(2026, 10, 8),
        Archive: new ChatLogBackfillArchiveDto("logs.cyex.app", "https://logs.cyex.app/"),
        RequestDelaySeconds: 10,
        Options:
        [
            new ChatLogBackfillOptionDto(1, new DateOnly(2026, 9, 8), new DateOnly(2026, 10, 8), 30, 5, true, null),
            new ChatLogBackfillOptionDto(3, new DateOnly(2026, 7, 8), new DateOnly(2026, 10, 8), 92, 14, true, null),
        ],
        ActiveEmoteSetId: "01HQ0000000000000000000000",
        Coverage:
        [
            new ChatLogBackfillCoverageInterval(
                new DateOnly(2026, 7, 8), new DateOnly(2026, 10, 8), "01HQ0000000000000000000000", "logs.cyex.app", "Normal"),
        ],
        ActiveRun: null,
        LastRun: Run(),
        ImportedFrom: new DateOnly(2026, 7, 8),
        ImportedTo: new DateOnly(2026, 10, 8),
        ImportedContiguous: true,
        CooldownUntilUtc: null);
}
