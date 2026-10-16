using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The handler contract of <c>POST /api/channels/{channelName}/backfill/imported/remove</c> (#384, spec
/// 5.2): the flag before the body, the body checks that precede the service, the forwarding of the
/// confirmed coverage and the actor, and the mapping of every service outcome. The authorization matrix
/// lives in <c>AuthFilterMatrixTests</c>, the rate-limit policy in <c>EmoteRoutePolicyTests</c>.
/// </summary>
public class ChatLogImportRemovalEndpointTests : IClassFixture<ApiFactory>
{
    private const string Channel = "handofblood";
    private const string SetId = "01FRY81K4800085N93FNKSBYXS";
    private const string Path = "/api/channels/handofblood/backfill/imported/remove";

    private const string ValidBody =
        """{"expectedCoverage":[{"from":"2026-07-08","to":"2026-10-08","emoteSetId":"01FRY81K4800085N93FNKSBYXS","archiveHost":"logs.cyex.app"}]}""";

    private readonly ApiFactory _factory;

    public ChatLogImportRemovalEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.ChatLogImportRemoval.ClearReceivedCalls();
        _factory.ChannelAccess.ClearReceivedCalls();

        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    // ---- The flag (D17) ----

    [Fact]
    public async Task FlagOff_Answers404BackfillDisabled_ForAManager_AndNeverCallsTheService()
    {
        using var disabled = _factory.WithBackfillDisabled();

        var response = await SendAsync(disabled, ValidBody);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.BackfillDisabled, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.ChatLogImportRemoval.ReceivedCalls());
    }

    [Fact]
    public async Task FlagOff_StillAnswers403_ForACallerWhoCannotManage_SoTheFeatureIsNotRevealed()
    {
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        using var disabled = _factory.WithBackfillDisabled();

        var response = await SendAsync(disabled, ValidBody);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task FlagOff_ChecksTheFlag_BeforeTheBody()
    {
        // A bindable but invalid body: a disabled feature must not leak its validation ladder.
        using var disabled = _factory.WithBackfillDisabled();

        var response = await SendAsync(disabled, "{}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.BackfillDisabled, await ReadErrorCodeAsync(response));
    }

    // ---- The body ----

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"expectedCoverage":null}""")]
    [InlineData("""{"expectedCoverage":[null]}""")]
    [InlineData("""{"expectedCoverage":[{"to":"2026-10-08","emoteSetId":"S","archiveHost":"h"}]}""")]
    [InlineData("""{"expectedCoverage":[{"from":"2026-07-08","emoteSetId":"S","archiveHost":"h"}]}""")]
    [InlineData("""{"expectedCoverage":[{"from":"2026-07-08","to":"2026-10-08","archiveHost":"h"}]}""")]
    [InlineData("""{"expectedCoverage":[{"from":"2026-07-08","to":"2026-10-08","emoteSetId":"S"}]}""")]
    [InlineData("""{"expectedCoverage":[{"from":"2026-07-08","to":"2026-10-08","emoteSetId":"","archiveHost":"h"}]}""")]
    [InlineData("""{"expectedCoverage":[{"from":"2026-07-08","to":"2026-10-08","emoteSetId":"S","archiveHost":"  "}]}""")]
    [InlineData("""{"expectedCoverage":[{"from":"2026-10-08","to":"2026-10-08","emoteSetId":"S","archiveHost":"h"}]}""")]
    [InlineData("""{"expectedCoverage":[{"from":"2026-10-09","to":"2026-10-08","emoteSetId":"S","archiveHost":"h"}]}""")]
    public async Task InvalidBody_Answers400_WithoutCallingTheService(string body)
    {
        var response = await SendAsync(_factory, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
        await AssertServiceNotCalledAsync();
    }

    [Fact]
    public async Task EmptyBody_Answers400_WithoutCallingTheService()
    {
        // A JSON content type with no content; without a content type routing never reaches the handler.
        var response = await SendAsync(_factory, body: "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
        await AssertServiceNotCalledAsync();
    }

    [Theory]
    [InlineData("""{"expectedCoverage":[{"from":"not-a-date","to":"2026-10-08","emoteSetId":"S","archiveHost":"h"}]}""")]
    [InlineData("""{"expectedCoverage":"nope"}""")]
    [InlineData("""{"expectedCoverage":[""")]
    public async Task UnbindableBody_Answers400_WithoutCallingTheService(string body)
    {
        // Binding fails before the filter and the flag, exactly like the start route (UnbindableBodyTests).
        var response = await SendAsync(_factory, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
        await AssertServiceNotCalledAsync();
    }

    [Fact]
    public async Task TooManyIntervals_Answers400_AndExactlyTheLimitPasses()
    {
        _factory.ChatLogImportRemoval.RemoveImportedAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<ExpectedCoverageInterval>>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChatLogImportRemovalResult.WithoutCounts(ChatLogImportRemovalStatus.NothingToRemove));

        var tooMany = await SendAsync(_factory, BodyWithIntervals(401));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(tooMany));
        await AssertServiceNotCalledAsync();

        var atLimit = await SendAsync(_factory, BodyWithIntervals(400));
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
    }

    [Fact]
    public async Task OversizedText_Answers400_WithoutCallingTheService()
    {
        var longText = new string('x', 201);
        var body = $$"""{"expectedCoverage":[{"from":"2026-07-08","to":"2026-10-08","emoteSetId":"{{longText}}","archiveHost":"h"}]}""";

        var response = await SendAsync(_factory, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertServiceNotCalledAsync();
    }

    [Fact]
    public async Task EmptyList_IsAllowed_AndForwardedAsEmpty()
    {
        _factory.ChatLogImportRemoval.RemoveImportedAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<ExpectedCoverageInterval>>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChatLogImportRemovalResult.WithoutCounts(ChatLogImportRemovalStatus.NothingToRemove));

        var response = await SendAsync(_factory, """{"expectedCoverage":[]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = (IReadOnlyList<ExpectedCoverageInterval>)Assert.Single(_factory.ChatLogImportRemoval.ReceivedCalls()).GetArguments()[1]!;
        Assert.Empty(expected);
    }

    // ---- Forwarding and the mapping of the outcomes ----

    [Fact]
    public async Task Post_ForwardsChannelCoverageUnchangedAndTheActor_AndAnswers200WithTwoCounters()
    {
        var userId = NewUserId();
        _factory.ChatLogImportRemoval.RemoveImportedAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<ExpectedCoverageInterval>>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChatLogImportRemovalResult.Removed(1234, 92, 5));

        var response = await SendAsync(_factory, ValidBody, userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["removedUsageRows", "removedCoverageDays"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1234, json.RootElement.GetProperty("removedUsageRows").GetInt32());
        Assert.Equal(92, json.RootElement.GetProperty("removedCoverageDays").GetInt32());

        var args = Assert.Single(_factory.ChatLogImportRemoval.ReceivedCalls()).GetArguments();
        Assert.Equal(Channel, args[0]);
        var interval = Assert.Single((IReadOnlyList<ExpectedCoverageInterval>)args[1]!);
        Assert.Equal(new ExpectedCoverageInterval(new DateOnly(2026, 7, 8), new DateOnly(2026, 10, 8), SetId, "logs.cyex.app"), interval);
        Assert.Equal(userId, ((AuditActor)args[2]!).TwitchUserId);
    }

    [Fact]
    public async Task Post_AnswersTheSame200Shape_ForNothingToRemove_WithZeros()
    {
        _factory.ChatLogImportRemoval.RemoveImportedAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<ExpectedCoverageInterval>>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChatLogImportRemovalResult.WithoutCounts(ChatLogImportRemovalStatus.NothingToRemove));

        var response = await SendAsync(_factory, ValidBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, json.RootElement.GetProperty("removedUsageRows").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("removedCoverageDays").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("removedSeeds", out _));
    }

    [Theory]
    [InlineData(ChatLogImportRemovalStatus.RunActive, HttpStatusCode.Conflict, "backfill_remove_run_active")]
    [InlineData(ChatLogImportRemovalStatus.ScopeChanged, HttpStatusCode.Conflict, "backfill_remove_scope_changed")]
    [InlineData(ChatLogImportRemovalStatus.NotFound, HttpStatusCode.NotFound, "channel_not_found")]
    public async Task Post_MapsTheRefusals_ToTheirStatusAndCode(ChatLogImportRemovalStatus status, HttpStatusCode expectedStatus, string expectedCode)
    {
        _factory.ChatLogImportRemoval.RemoveImportedAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<ExpectedCoverageInterval>>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChatLogImportRemovalResult.WithoutCounts(status));

        var response = await SendAsync(_factory, ValidBody);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public void TheTwoNewCodes_HaveTheirWireValues()
    {
        Assert.Equal("backfill_remove_run_active", ApiErrorCodes.BackfillRemoveRunActive);
        Assert.Equal("backfill_remove_scope_changed", ApiErrorCodes.BackfillRemoveScopeChanged);
    }

    private static string NewUserId() => Guid.NewGuid().ToString("N");

    private static string BodyWithIntervals(int count)
    {
        var items = Enumerable.Range(0, count)
            .Select(i => $$"""{"from":"2026-01-01","to":"2026-01-02","emoteSetId":"S{{i}}","archiveHost":"h"}""");
        return $$"""{"expectedCoverage":[{{string.Join(",", items)}}]}""";
    }

    private async Task AssertServiceNotCalledAsync() =>
        await _factory.ChatLogImportRemoval.DidNotReceiveWithAnyArgs()
            .RemoveImportedAsync(default!, default!, default!, default);

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }

    private static async Task<HttpResponseMessage> SendAsync(WebApplicationFactory<Program> factory, string? body, string? userId = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var request = new HttpRequestMessage(HttpMethod.Post, Path);
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId ?? NewUserId());
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }
}
