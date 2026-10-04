using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The handler contract of the channel tag routes (#201): status-to-HTTP mapping per service result,
/// body shapes and the thin input handling (null body, non-numeric id). Filter order and policies live
/// in <c>AuthFilterMatrixTests</c> / <c>EmoteRoutePolicyTests</c>; the rules behind the statuses in the
/// service tests.
/// </summary>
public class EmoteTagEndpointsTests : IClassFixture<ApiFactory>
{
    private const string Channel = "tagchannel";
    private const string Base = "/api/channels/" + Channel + "/tags";

    private readonly ApiFactory _factory;

    public EmoteTagEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.ChannelAccess.ClearReceivedCalls();
        _factory.EmoteTags.ClearReceivedCalls();
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    [Fact]
    public async Task List_Answers200WithNullSetFieldsAsJsonNull_AndForwardsTheSetId()
    {
        _factory.EmoteTags.ListAsync(Channel, "01GV88A38G0006FW5TVZVMG507", Arg.Any<CancellationToken>())
            .Returns(new EmoteTagListResult(
                EmoteTagListStatus.Ok, "01GV88A38G0006FW5TVZVMG507", false, [new EmoteTagSummaryDto(7, "Funny", 3, null)]));

        var response = await SendAsync("GET", Base + "?emoteSetId=01GV88A38G0006FW5TVZVMG507");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("01GV88A38G0006FW5TVZVMG507", body.GetProperty("emoteSetId").GetString());
        Assert.False(body.GetProperty("isActiveSet").GetBoolean());
        var tag = body.GetProperty("tags")[0];
        Assert.Equal((7, "Funny", 3), (tag.GetProperty("id").GetInt64(), tag.GetProperty("name").GetString(), tag.GetProperty("entryCount").GetInt32()));
        Assert.Equal(JsonValueKind.Null, tag.GetProperty("inSetCount").ValueKind);
    }

    [Fact]
    public async Task List_WithoutAnActiveSet_AnswersEmoteSetIdAsJsonNull()
    {
        _factory.EmoteTags.ListAsync(Channel, null, Arg.Any<CancellationToken>())
            .Returns(new EmoteTagListResult(EmoteTagListStatus.Ok, null, false, []));

        var body = await ReadJsonAsync(await SendAsync("GET", Base));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("emoteSetId").ValueKind);
        Assert.Equal(0, body.GetProperty("tags").GetArrayLength());
    }

    [Fact]
    public async Task List_ForAnUntrackedChannel_Answers404ChannelNotFound()
    {
        _factory.EmoteTags.ListAsync(Channel, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new EmoteTagListResult(EmoteTagListStatus.ChannelNotFound, null, false, []));

        var response = await SendAsync("GET", Base);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ChannelNotFound, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Entries_Answer200WithTheEntryShape()
    {
        _factory.EmoteTags.ListEntriesAsync(Channel, 7, null, Arg.Any<CancellationToken>())
            .Returns(new EmoteTagEntriesResult(
                EmoteTagEntriesStatus.Ok, "SET1", true,
                [new EmoteTagEntryDto("EMOTE1", "KEKW", "https://cdn.7tv.app/emote/EMOTE1/4x.webp", true, "KEKW2"),
                 new EmoteTagEntryDto("EMOTE2", "Pog", "https://cdn.7tv.app/emote/EMOTE2/4x.webp", null, null)]));

        var response = await SendAsync("GET", Base + "/7/entries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.GetProperty("isActiveSet").GetBoolean());
        var entries = body.GetProperty("entries");
        Assert.Equal("KEKW2", entries[0].GetProperty("currentName").GetString());
        Assert.True(entries[0].GetProperty("inSet").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entries[1].GetProperty("inSet").ValueKind);
        Assert.Equal("EMOTE2", entries[1].GetProperty("sevenTvEmoteId").GetString());
    }

    [Theory]
    [InlineData(EmoteTagEntriesStatus.ChannelNotFound, ApiErrorCodes.ChannelNotFound)]
    [InlineData(EmoteTagEntriesStatus.TagNotFound, ApiErrorCodes.TagNotFound)]
    public async Task Entries_Answer404WithTheCode(EmoteTagEntriesStatus status, string expectedCode)
    {
        _factory.EmoteTags.ListEntriesAsync(Channel, 7, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new EmoteTagEntriesResult(status, null, false, []));

        var response = await SendAsync("GET", Base + "/7/entries");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(expectedCode, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Create_Answers201WithIdAndName_AndPassesTheNameAndActorOn()
    {
        _factory.EmoteTags.CreateAsync(Channel, "Funny", Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new EmoteTagMutationResult(EmoteTagMutationStatus.Ok, new EmoteTagDto(9, "Funny")));

        var response = await SendAsync("POST", Base, """{"name":"Funny"}""", "user-1");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal((9, "Funny"), (body.GetProperty("id").GetInt64(), body.GetProperty("name").GetString()));
        await _factory.EmoteTags.Received(1).CreateAsync(
            Channel, "Funny", Arg.Is<AuditActor>(a => a.TwitchUserId == "user-1"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(EmoteTagMutationStatus.ChannelNotFound, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotFound)]
    [InlineData(EmoteTagMutationStatus.TagNotFound, HttpStatusCode.NotFound, ApiErrorCodes.TagNotFound)]
    [InlineData(EmoteTagMutationStatus.NameInvalid, HttpStatusCode.BadRequest, ApiErrorCodes.TagNameInvalid)]
    [InlineData(EmoteTagMutationStatus.NameTaken, HttpStatusCode.Conflict, ApiErrorCodes.TagNameTaken)]
    [InlineData(EmoteTagMutationStatus.LimitReached, HttpStatusCode.Conflict, ApiErrorCodes.TagLimitReached)]
    public async Task Create_Rename_AndDelete_MapEveryFailureStatus(EmoteTagMutationStatus status, HttpStatusCode expected, string code)
    {
        var failure = new EmoteTagMutationResult(status, null);
        _factory.EmoteTags.CreateAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>()).Returns(failure);
        _factory.EmoteTags.RenameAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>()).Returns(failure);
        _factory.EmoteTags.DeleteAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>()).Returns(status);

        foreach (var (method, path) in new[] { ("POST", Base), ("PATCH", Base + "/7"), ("DELETE", Base + "/7") })
        {
            var response = await SendAsync(method, path, """{"name":"x"}""");

            Assert.Equal(expected, response.StatusCode);
            Assert.Equal(code, await ReadErrorCodeAsync(response));
        }
    }

    [Fact]
    public async Task Create_WithANullBody_OrNoName_ReachesTheServiceWithANullName()
    {
        _factory.EmoteTags.CreateAsync(Channel, null, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new EmoteTagMutationResult(EmoteTagMutationStatus.NameInvalid, null));

        foreach (var body in new[] { "null", "{}" })
        {
            var response = await SendAsync("POST", Base, body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ApiErrorCodes.TagNameInvalid, await ReadErrorCodeAsync(response));
        }
    }

    [Fact]
    public async Task Rename_Answers200WithTheTag()
    {
        _factory.EmoteTags.RenameAsync(Channel, 7, "Renamed", Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new EmoteTagMutationResult(EmoteTagMutationStatus.Ok, new EmoteTagDto(7, "Renamed")));

        var response = await SendAsync("PATCH", Base + "/7", """{"name":"Renamed"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", (await ReadJsonAsync(response)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Delete_Answers204WithoutABody()
    {
        _factory.EmoteTags.DeleteAsync(Channel, 7, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(EmoteTagMutationStatus.Ok);

        var response = await SendAsync("DELETE", Base + "/7");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "/abc/entries")]
    [InlineData("PATCH", "/abc")]
    [InlineData("DELETE", "/abc")]
    [InlineData("POST", "/abc/entries")]
    [InlineData("POST", "/abc/entries/remove")]
    public async Task ANonNumericTagId_Answers404WithoutABody_AndNeverCallsTheService(string method, string suffix)
    {
        var response = await SendAsync(method, Base + suffix, "{}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Empty(_factory.EmoteTags.ReceivedCalls());
    }

    [Fact]
    public async Task AddEntries_Answer200WithTheCounts_AndForwardsTheIds()
    {
        _factory.EmoteTags.AddEntriesAsync(Channel, 7, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new EmoteTagAddEntriesResult(EmoteTagAddEntriesStatus.Ok, 2, 1, ["GONE"]));

        var response = await SendAsync("POST", Base + "/7/entries", """{"sevenTvEmoteIds":["A","B","C","GONE"]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal((2, 1), (body.GetProperty("addedCount").GetInt32(), body.GetProperty("alreadyTaggedCount").GetInt32()));
        Assert.Equal("GONE", body.GetProperty("skippedNotInSetIds")[0].GetString());
        await _factory.EmoteTags.Received(1).AddEntriesAsync(
            Channel, 7, Arg.Is<IReadOnlyList<string>?>(ids => ids!.SequenceEqual(new[] { "A", "B", "C", "GONE" })), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(EmoteTagAddEntriesStatus.ChannelNotFound, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotFound)]
    [InlineData(EmoteTagAddEntriesStatus.TagNotFound, HttpStatusCode.NotFound, ApiErrorCodes.TagNotFound)]
    [InlineData(EmoteTagAddEntriesStatus.EmoteIdsEmpty, HttpStatusCode.BadRequest, ApiErrorCodes.EmoteIdsEmpty)]
    [InlineData(EmoteTagAddEntriesStatus.EmoteIdsInvalid, HttpStatusCode.BadRequest, ApiErrorCodes.EmoteIdsInvalid)]
    [InlineData(EmoteTagAddEntriesStatus.EntryLimitReached, HttpStatusCode.Conflict, ApiErrorCodes.TagEntryLimitReached)]
    public async Task AddEntries_MapEveryFailureStatus(EmoteTagAddEntriesStatus status, HttpStatusCode expected, string code)
    {
        _factory.EmoteTags.AddEntriesAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new EmoteTagAddEntriesResult(status, 0, 0, []));

        var response = await SendAsync("POST", Base + "/7/entries", """{"sevenTvEmoteIds":["A"]}""");

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(code, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task AddAndRemove_WithANullBody_ReachTheServiceWithoutIds()
    {
        _factory.EmoteTags.AddEntriesAsync(Channel, 7, null, Arg.Any<CancellationToken>())
            .Returns(new EmoteTagAddEntriesResult(EmoteTagAddEntriesStatus.EmoteIdsEmpty, 0, 0, []));
        _factory.EmoteTags.RemoveEntriesAsync(Channel, 7, null, Arg.Any<CancellationToken>())
            .Returns(new EmoteTagRemoveEntriesResult(EmoteTagRemoveEntriesStatus.EmoteIdsEmpty, 0));

        foreach (var path in new[] { "/7/entries", "/7/entries/remove" })
        {
            var response = await SendAsync("POST", Base + path, "null");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ApiErrorCodes.EmoteIdsEmpty, await ReadErrorCodeAsync(response));
        }
    }

    [Fact]
    public async Task RemoveEntries_Answer200WithTheCount()
    {
        _factory.EmoteTags.RemoveEntriesAsync(Channel, 7, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new EmoteTagRemoveEntriesResult(EmoteTagRemoveEntriesStatus.Ok, 2));

        var response = await SendAsync("POST", Base + "/7/entries/remove", """{"sevenTvEmoteIds":["A","B"]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await ReadJsonAsync(response)).GetProperty("removedCount").GetInt32());
    }

    [Theory]
    [InlineData(EmoteTagRemoveEntriesStatus.ChannelNotFound, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotFound)]
    [InlineData(EmoteTagRemoveEntriesStatus.TagNotFound, HttpStatusCode.NotFound, ApiErrorCodes.TagNotFound)]
    [InlineData(EmoteTagRemoveEntriesStatus.EmoteIdsEmpty, HttpStatusCode.BadRequest, ApiErrorCodes.EmoteIdsEmpty)]
    [InlineData(EmoteTagRemoveEntriesStatus.EmoteIdsInvalid, HttpStatusCode.BadRequest, ApiErrorCodes.EmoteIdsInvalid)]
    public async Task RemoveEntries_MapEveryFailureStatus(EmoteTagRemoveEntriesStatus status, HttpStatusCode expected, string code)
    {
        _factory.EmoteTags.RemoveEntriesAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new EmoteTagRemoveEntriesResult(status, 0));

        var response = await SendAsync("POST", Base + "/7/entries/remove", """{"sevenTvEmoteIds":["A"]}""");

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(code, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task AnOversizedIdList_IsTheServicesEmoteIdsInvalid_AndSurfacesAs400()
    {
        // The cap itself lives in the service (EmoteTagLimits.MaxIdsPerRequest); the endpoint's part is
        // that the body of that size binds and the status maps to the existing code.
        var ids = Enumerable.Repeat("A", EmoteTagLimits.MaxIdsPerRequest + 1).ToList();
        _factory.EmoteTags.AddEntriesAsync(Channel, 7, Arg.Is<IReadOnlyList<string>?>(l => l!.Count == ids.Count), Arg.Any<CancellationToken>())
            .Returns(new EmoteTagAddEntriesResult(EmoteTagAddEntriesStatus.EmoteIdsInvalid, 0, 0, []));

        var response = await SendAsync("POST", Base + "/7/entries", JsonSerializer.Serialize(new { sevenTvEmoteIds = ids }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.EmoteIdsInvalid, await ReadErrorCodeAsync(response));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("errorCode").GetString();

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string? body = null, string? userId = null)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        // A fresh id per request: the rate limiter partitions by the NameIdentifier claim.
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId ?? Guid.NewGuid().ToString("N"));
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }
}
