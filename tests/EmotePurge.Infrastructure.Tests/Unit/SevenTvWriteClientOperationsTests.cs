using System.Net;
using System.Text.Json;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Unit.SevenTvWriteClientTestData;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="SevenTvWriteClient"/>'s verify, remove and invitation acceptance (plan #487 2.4, revision 6; AK125 client
/// part), and the transport outcomes every operation shares: time limit, network failure, the caller's own cancellation.
/// </summary>
public class SevenTvWriteClientOperationsTests
{
    private const string AcceptPath = """["userEditors","editor","updateState"]""";

    [Fact]
    public async Task Verify_ReturnsTheOwnUserId_FromUsersMe()
    {
        var handler = new ScriptedHttpHandler().ThenOk(Fill("""{"data":{"users":{"me":{"id":"@BOT@"}}}}"""));

        var result = await Client(handler).VerifyAsync(Token);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Equal(BotId, result.Value);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Equal(SevenTvWriteClient.VerifyQuery, JsonDocument.Parse(request.Body).RootElement.GetProperty("query").GetString());
    }

    [Theory]
    [InlineData("""{"data":{"users":{"me":null}}}""")]
    [InlineData("""{"data":{"users":{"me":{"id":""}}}}""")]
    [InlineData("""{"data":null}""")]
    public async Task Verify_WithoutAUser_IsUnavailable(string body)
    {
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).VerifyAsync(Token);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Verify_RealHttp401_IsUnauthorized()
    {
        // Probe P9: whoami after "Sign Out Everywhere".
        var handler = new ScriptedHttpHandler().Then(HttpStatusCode.Unauthorized, UnauthorizedBody);

        var result = await Client(handler).VerifyAsync(Token);

        Assert.Equal(SevenTvWriteOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task Verify_DisguisedRateLimit_IsRateLimited()
    {
        var handler = new ScriptedHttpHandler().ThenOk(ErrorsOnly(Error("slow", null, 429)));

        var result = await Client(handler).VerifyAsync(Token);

        Assert.Equal(SevenTvWriteOutcome.RateLimited, result.Outcome);
    }

    [Fact]
    public async Task Remove_IsOk_AndAlwaysSendsTheAlias()
    {
        // Probe P5: removeEmote answered `{ id }`.
        var handler = new ScriptedHttpHandler().ThenOk(Fill("""{"data":{"emoteSets":{"emoteSet":{"removeEmote":{"id":"@SET@"}}}}}"""));

        var result = await Client(handler).RemoveEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(SevenTvWriteClient.RemoveEmoteMutation, body.RootElement.GetProperty("query").GetString());
        Assert.Contains("$alias: String!", SevenTvWriteClient.RemoveEmoteMutation, StringComparison.Ordinal);
        var variables = body.RootElement.GetProperty("variables");
        Assert.Equal(SetId, variables.GetProperty("setId").GetString());
        Assert.Equal(EmoteId, variables.GetProperty("emoteId").GetString());
        Assert.Equal(Alias, variables.GetProperty("alias").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Remove_WithoutAnAlias_SendsNothing(string? alias)
    {
        var handler = new ScriptedHttpHandler();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Client(handler).RemoveEmoteAsync(Token, SetId, EmoteId, alias!));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Remove_LackingPrivileges_IsLackingPrivileges()
    {
        var body = ErrorsOnly(Error(NotEditorMessage, "LACKING_PRIVILEGES", 403, """["emoteSets","emoteSet","removeEmote"]"""));
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).RemoveEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.LackingPrivileges, result.Outcome);
    }

    [Fact]
    public async Task Remove_MutationFieldNull_IsUnavailable()
    {
        var handler = new ScriptedHttpHandler().ThenOk("""{"data":{"emoteSets":{"emoteSet":{"removeEmote":null}}}}""");

        var result = await Client(handler).RemoveEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task Remove_Http401_IsUnauthorized()
    {
        var handler = new ScriptedHttpHandler().Then(HttpStatusCode.Unauthorized, UnauthorizedBody);

        var result = await Client(handler).RemoveEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task Accept_ReturningAccepted_IsOk()
    {
        var handler = new ScriptedHttpHandler().ThenOk("""{"data":{"userEditors":{"editor":{"updateState":{"state":"ACCEPTED"}}}}}""");

        var result = await Client(handler).AcceptEditorInvitationAsync(Token, BotId, OwnerId);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
    }

    [Fact]
    public async Task Accept_SendsOnlyAccept_TheBotId_AndTheOwnerId()
    {
        // AK125: the state in the request is ACCEPT and nothing else; the variables are the two ids.
        var handler = new ScriptedHttpHandler().ThenOk("""{"data":{"userEditors":{"editor":{"updateState":{"state":"ACCEPTED"}}}}}""");

        await Client(handler).AcceptEditorInvitationAsync(Token, BotId, OwnerId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        var query = body.RootElement.GetProperty("query").GetString()!;
        Assert.Equal(SevenTvWriteClient.AcceptInvitationMutation, query);
        Assert.Contains("updateState(state: ACCEPT)", query, StringComparison.Ordinal);
        Assert.DoesNotContain("REJECT", query, StringComparison.Ordinal);
        Assert.DoesNotContain("delete", query, StringComparison.Ordinal);
        Assert.DoesNotContain("updatePermissions", query, StringComparison.Ordinal);
        var variables = body.RootElement.GetProperty("variables");
        Assert.Equal(
            [("editorId", BotId), ("userId", OwnerId)],
            variables.EnumerateObject().Select(property => (property.Name, property.Value.GetString())));
    }

    [Theory]
    [InlineData("""{"data":{"userEditors":{"editor":{"updateState":{"state":"PENDING"}}}}}""")]
    [InlineData("""{"data":{"userEditors":{"editor":{"updateState":{"state":"REJECTED"}}}}}""")]
    [InlineData("""{"data":{"userEditors":{"editor":{"updateState":{}}}}}""")]
    [InlineData("""{"data":{"userEditors":{"editor":{"updateState":null}}}}""")]
    [InlineData("""{"data":null}""")]
    [InlineData("{\"data\":{\"userEditors\":")]
    public async Task Accept_WithoutTheAcceptedState_IsUnavailable(string body)
    {
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AcceptEditorInvitationAsync(Token, BotId, OwnerId);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task Accept_LackingPrivileges_IsLackingPrivileges()
    {
        var body = ErrorsOnly(Error(NotEditorMessage, "LACKING_PRIVILEGES", 403, AcceptPath));
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AcceptEditorInvitationAsync(Token, BotId, OwnerId);

        Assert.Equal(SevenTvWriteOutcome.LackingPrivileges, result.Outcome);
    }

    [Fact]
    public async Task Accept_Http401_IsUnauthorized()
    {
        var handler = new ScriptedHttpHandler().Then(HttpStatusCode.Unauthorized, UnauthorizedBody);

        var result = await Client(handler).AcceptEditorInvitationAsync(Token, BotId, OwnerId);

        Assert.Equal(SevenTvWriteOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task Accept_DisguisedRateLimit_IsRateLimited_WithTheResetHint()
    {
        var body = ErrorsOnly(Error("slow", "RATE_LIMIT_EXCEEDED", 429, AcceptPath, "\"headers\":{\"x-ratelimit-reset\":12}"));
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AcceptEditorInvitationAsync(Token, BotId, OwnerId);

        Assert.Equal(SevenTvWriteOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(12), result.RetryAfter);
    }

    [Fact]
    public async Task Accept_Timeout_IsTimeout()
    {
        var handler = new ScriptedHttpHandler().ThenHang();

        var result = await Client(handler, timeout: TimeSpan.FromMilliseconds(200)).AcceptEditorInvitationAsync(Token, BotId, OwnerId);

        Assert.Equal(SevenTvWriteOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task Add_Timeout_IsTimeout()
    {
        var handler = new ScriptedHttpHandler().ThenHang();

        var result = await Client(handler, timeout: TimeSpan.FromMilliseconds(200)).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Timeout, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Read_TimeoutOnTheSecondPage_IsTimeout()
    {
        var handler = new ScriptedHttpHandler()
            .ThenOk(ReadPage($"[{ReadItem("E1", "a")}]", pageCount: 2))
            .ThenHang();

        var result = await Client(handler, timeout: TimeSpan.FromMilliseconds(200)).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Timeout, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task NetworkFailure_IsUnavailable()
    {
        var handler = new ScriptedHttpHandler().ThenThrow(new HttpRequestException("connection refused"));

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task CallerCancellation_IsThrown_NotMapped()
    {
        var handler = new ScriptedHttpHandler().ThenHang();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias, cancellation.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task MissingToken_SendsNothing(string? token)
    {
        var handler = new ScriptedHttpHandler();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Client(handler).VerifyAsync(token!));

        Assert.Empty(handler.Requests);
    }
}
