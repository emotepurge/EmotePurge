using System.Net;
using System.Text.Json;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Unit.SevenTvWriteClientTestData;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="SevenTvWriteClient.AddEmoteAsync"/>: the proof of success (plan #487 2.4, revision 6a S6-4, AK128) and the
/// mapping of every answer form the live probe T0 measured (AK76, AK89; finding 13). <c>Ok</c> needs the mutation field
/// with the requested set's id; anything unclear after sending is <c>Unavailable</c>, never <c>Ok</c>.
/// </summary>
public class SevenTvWriteClientAddEmoteTests
{
    private const string MutationPath = """["emoteSets","emoteSet","addEmote"]""";

    [Fact]
    public async Task MatchingSetWithEntries_IsOk_WithTheReturnedEntries()
    {
        var handler = new ScriptedHttpHandler().ThenOk(AddAnswer());

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.Count);
        var own = result.Value[0];
        Assert.Equal(new SevenTvSetEntry(EmoteId, Alias, new DateTime(2026, 11, 24, 16, 55, 23, 498, DateTimeKind.Utc), BotId), own);
        Assert.Equal(DateTimeKind.Utc, own.AddedAtUtc!.Value.Kind);
    }

    [Fact]
    public async Task SendsTheMeasuredMutation_WithTheTokenAsBearer_AndTheThreeVariables()
    {
        var handler = new ScriptedHttpHandler().ThenOk(AddAnswer());

        await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://7tv.io/v4/gql"), request.RequestUri);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(SevenTvWriteClient.AddEmoteMutation, body.RootElement.GetProperty("query").GetString());
        var variables = body.RootElement.GetProperty("variables");
        Assert.Equal(SetId, variables.GetProperty("setId").GetString());
        Assert.Equal(EmoteId, variables.GetProperty("emoteId").GetString());
        Assert.Equal(Alias, variables.GetProperty("alias").GetString());
        Assert.Equal(3, variables.EnumerateObject().Count());
    }

    [Fact]
    public async Task MatchingSet_ErrorOnlyBelowTheEntries_IsOk_WithoutEntries()
    {
        var body =
            Fill("""
            {"data":{"emoteSets":{"emoteSet":{"addEmote":{"id":"@SET@","emotes":null}}}},
             "errors":[{"message":"internal","path":["emoteSets","emoteSet","addEmote","emotes"],"extensions":{"code":"INTERNAL_SERVER_ERROR","status":500}}]}
            """);
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task MatchingSet_WithoutTheEntries_IsOk_WithoutEntries()
    {
        var handler = new ScriptedHttpHandler().ThenOk(Fill("""{"data":{"emoteSets":{"emoteSet":{"addEmote":{"id":"@SET@"}}}}}"""));

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("""{"totalCount":2,"pageCount":1,"items":"not a list"}""")]
    [InlineData("""{"totalCount":2,"items":[]}""")]
    [InlineData("""{"totalCount":2,"pageCount":1,"items":[{"alias":"x","emote":{}}]}""")]
    [InlineData("""{"totalCount":2,"pageCount":1,"items":[42]}""")]
    public async Task MatchingSet_WithUnreadableEntries_IsOk_WithoutEntries(string emotes)
    {
        var handler = new ScriptedHttpHandler().ThenOk(
            Fill("""{"data":{"emoteSets":{"emoteSet":{"addEmote":{"id":"@SET@","emotes":@EMOTES@}}}}}""").Replace("@EMOTES@", emotes, StringComparison.Ordinal));

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task MatchingSet_LargerThanTheReturnedPage_IsOk_WithoutEntries()
    {
        // The return selection asks for page 1 of 500; the new entry may sit on a later page, so the caller reads.
        var handler = new ScriptedHttpHandler().ThenOk(AddAnswer(pageCount: 2));

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task EntryWithoutAddedAtOrAddedBy_IsKept_WithNullFields()
    {
        var items = Fill("""[{"alias":"@ALIAS@","emote":{"id":"@EMOTE@"}}]""");
        var handler = new ScriptedHttpHandler().ThenOk(AddAnswer(items: items));

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        var entry = Assert.Single(result.Value!);
        Assert.Null(entry.AddedAtUtc);
        Assert.Null(entry.AddedById);
    }

    public static TheoryData<string> AnswersWithoutProof => new()
    {
        // Mutation field null without an error.
        """{"data":{"emoteSets":{"emoteSet":{"addEmote":null}}}}""",
        // `data: null` without an error.
        """{"data":null}""",
        // Another set's id.
        Fill("""{"data":{"emoteSets":{"emoteSet":{"addEmote":{"id":"@OTHERSET@","emotes":{"totalCount":0,"pageCount":0,"items":[]}}}}}}"""),
        // No id at all.
        """{"data":{"emoteSets":{"emoteSet":{"addEmote":{"emotes":{"totalCount":0,"pageCount":0,"items":[]}}}}}}""",
        // The non-GraphQL body of a real 401, but as HTTP 200.
        UnauthorizedBody,
        // Error on the mutation path without a status.
        ErrorsOnly(Error("boom", "INTERNAL_SERVER_ERROR", null, MutationPath)),
        // Error on the mutation path with a 5xx.
        ErrorsOnly(Error("boom", "INTERNAL_SERVER_ERROR", 500, MutationPath)),
        // An error without a path next to a matching set: it is not below the mutation field.
        Fill("""{"data":{"emoteSets":{"emoteSet":{"addEmote":{"id":"@SET@"}}}},"errors":@ERRORS@}""").Replace("@ERRORS@", Errors(Error("hm", null, null)), StringComparison.Ordinal),
    };

    [Theory]
    [MemberData(nameof(AnswersWithoutProof))]
    public async Task AnswerWithoutProofOfSuccess_IsUnavailable(string body)
    {
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"data\":{\"emoteSets\":{\"emoteSet\":{\"addEmote\":{\"id\":\"01M16Z")]
    [InlineData("<html>bad gateway</html>")]
    [InlineData("[]")]
    public async Task UnreadableOrCutOffBody_IsUnavailable(string body)
    {
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task AliasTaken_Status409_IsAliasConflict()
    {
        // Probe P6: HTTP 200, code BAD_REQUEST, status 409, on the mutation path.
        var body = ErrorsOnly(Error(ConflictMessage, "BAD_REQUEST", 409, MutationPath));
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Rejected, result.Outcome);
        Assert.Equal(SevenTvWriteRejection.AliasConflict, result.Status.Rejection);
    }

    [Theory]
    [InlineData(NotEditorMessage)]
    [InlineData(PendingInvitationMessage)]
    [InlineData("a message 7TV has never sent")]
    public async Task LackingPrivileges_IsDecidedByTheCode_WhateverTheMessage(string message)
    {
        // Probes P7 and A5: HTTP 200, LACKING_PRIVILEGES, status 403, two different messages.
        var body = ErrorsOnly(Error(message, "LACKING_PRIVILEGES", 403, MutationPath));
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.LackingPrivileges, result.Outcome);
    }

    [Fact]
    public async Task LackingPrivilegesCode_WithoutStatus_IsLackingPrivileges()
    {
        var body = ErrorsOnly(Error(NotEditorMessage, "LACKING_PRIVILEGES", null, MutationPath));
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.LackingPrivileges, result.Outcome);
    }

    [Fact]
    public async Task Status403InAnHttp200_IsLackingPrivileges_NeverUnauthorized()
    {
        var body = ErrorsOnly(Error("forbidden", "FORBIDDEN", 403, MutationPath));
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.LackingPrivileges, result.Outcome);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task UnknownFourHundredOnTheMutationPath_IsRejectedUnknown(int status)
    {
        var body = ErrorsOnly(Error("nope", "BAD_REQUEST", status, MutationPath));
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Rejected, result.Outcome);
        Assert.Equal(SevenTvWriteRejection.Unknown, result.Status.Rejection);
    }

    [Fact]
    public async Task RealHttp401_WithTheMeasuredBody_IsUnauthorized()
    {
        var handler = new ScriptedHttpHandler().Then(HttpStatusCode.Unauthorized, UnauthorizedBody);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task Http403_WithTheSevenTvBody_IsUnauthorized()
    {
        var handler = new ScriptedHttpHandler().Then(HttpStatusCode.Forbidden, UnauthorizedBody);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Unauthorized, result.Outcome);
    }

    [Theory]
    [InlineData("<html><body>Forbidden</body></html>")]
    [InlineData("")]
    [InlineData("{\"error\":\"blocked\"}")]
    [InlineData("{\"error_code\":\"1000\"}")]
    public async Task Http403_WithAnyOtherBody_IsUnavailable(string body)
    {
        var handler = new ScriptedHttpHandler().Then(HttpStatusCode.Forbidden, body);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>nope</html>")]
    public async Task Http401_WithAnyBody_IsUnauthorized(string body)
    {
        var handler = new ScriptedHttpHandler().Then(HttpStatusCode.Unauthorized, body);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Unauthorized, result.Outcome);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(400)]
    public async Task OtherHttpFailure_IsUnavailable(int status)
    {
        var handler = new ScriptedHttpHandler().Then((HttpStatusCode)status, "{}");

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(7 * 60 * 60, 6 * 60 * 60)]
    public async Task Http429_TakesRetryAfter_CappedAtSixHours(int retryAfterSeconds, int expectedSeconds)
    {
        var handler = new ScriptedHttpHandler().Then(
            HttpStatusCode.TooManyRequests,
            string.Empty,
            response => response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds)));

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result.Status.RetryAfter);
    }

    [Fact]
    public async Task Http429_WithoutRetryAfter_WaitsSixtySeconds()
    {
        var handler = new ScriptedHttpHandler().Then(HttpStatusCode.TooManyRequests, string.Empty);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(60), result.Status.RetryAfter);
    }

    [Theory]
    [InlineData("\"headers\":{\"x-ratelimit-emote_set_change-reset\":42}", 42)]
    [InlineData("\"headers\":{\"x-ratelimit-emote_set_change-reset\":\"17\"}", 17)]
    [InlineData("\"headers\":{\"x-ratelimit-emote_set_change-reset\":99999}", 6 * 60 * 60)]
    [InlineData("\"headers\":{\"x-ratelimit-emote_set_change-limit\":100}", 60)]
    [InlineData(null, 60)]
    public async Task DisguisedRateLimit_TakesTheResetHint_OrWaitsSixtySeconds(string? extra, int expectedSeconds)
    {
        var body = ErrorsOnly(Error("rate limited", "RATE_LIMIT_EXCEEDED", 429, MutationPath, extra));
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result.Status.RetryAfter);
    }

    [Fact]
    public async Task DisguisedRateLimit_WinsOverAnotherError()
    {
        var body = ErrorsOnly(
            Error(NotEditorMessage, "LACKING_PRIVILEGES", 403, MutationPath),
            Error("rate limited", null, 429));
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.RateLimited, result.Outcome);
    }

    [Fact]
    public async Task UnclearErrorBesideAFourHundred_StaysUnavailable()
    {
        // Rejected means "7TV did not write"; one error that says nothing about that keeps the outcome open.
        var body = ErrorsOnly(
            Error("nope", "BAD_REQUEST", 400, MutationPath),
            Error("boom", null, null, MutationPath));
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).AddEmoteAsync(Token, SetId, EmoteId, Alias);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
    }
}
