using System.Net;
using System.Text.Json;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Unit.SevenTvWriteClientTestData;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// <see cref="SevenTvWriteClient.ReadSetEntriesAsync"/>: every page or nothing (plan #487 2.4, spec AK76 "unvollständige
/// Seitenfolge beim Lesen → Fehler"). A list the bot decides ownership on must be complete, so every doubt is
/// <c>Unavailable</c>.
/// </summary>
public class SevenTvWriteClientReadTests
{
    [Fact]
    public async Task OnePage_IsOk_WithEveryEntry_AndThePageOneRequest()
    {
        var items = $"[{ReadItem(EmoteId, Alias)},{ReadItem("01FQN716Q00001SJ04CBQJT3Z8", "frenn", addedById: OwnerId)}]";
        var handler = new ScriptedHttpHandler().ThenOk(ReadPage(items, pageCount: 1));

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal(BotId, result.Value[0].AddedById);
        Assert.Equal(OwnerId, result.Value[1].AddedById);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(SevenTvWriteClient.SetEntriesQuery, body.RootElement.GetProperty("query").GetString());
        var variables = body.RootElement.GetProperty("variables");
        Assert.Equal(SetId, variables.GetProperty("id").GetString());
        Assert.Equal(1, variables.GetProperty("page").GetInt32());
        Assert.Equal(500, variables.GetProperty("perPage").GetInt32());
    }

    [Fact]
    public async Task EmptySet_IsOk_WithNoEntries()
    {
        var handler = new ScriptedHttpHandler().ThenOk(ReadPage("[]", pageCount: 0));

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task ThreePages_AreAllRead_InOrder()
    {
        var handler = new ScriptedHttpHandler()
            .ThenOk(ReadPage($"[{ReadItem("E1", "a")}]", pageCount: 3, totalCount: 3))
            .ThenOk(ReadPage($"[{ReadItem("E2", "b")}]", pageCount: 3, totalCount: 3))
            .ThenOk(ReadPage($"[{ReadItem("E3", "c")}]", pageCount: 3, totalCount: 3));

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Equal(["E1", "E2", "E3"], result.Value!.Select(entry => entry.EmoteId));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(
            [1, 2, 3],
            handler.Requests.Select(request => JsonDocument.Parse(request.Body).RootElement.GetProperty("variables").GetProperty("page").GetInt32()));
    }

    [Fact]
    public async Task SecondOfThreePagesFails_IsUnavailable_WithoutEntries()
    {
        var handler = new ScriptedHttpHandler()
            .ThenOk(ReadPage($"[{ReadItem("E1", "a")}]", pageCount: 3, totalCount: 3))
            .Then(HttpStatusCode.BadGateway, string.Empty);

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SecondPageRateLimited_IsRateLimited_WithoutEntries()
    {
        var handler = new ScriptedHttpHandler()
            .ThenOk(ReadPage($"[{ReadItem("E1", "a")}]", pageCount: 2, totalCount: 2))
            .ThenOk(ErrorsOnly(Error("slow down", null, 429)));

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(60), result.Status.RetryAfter);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task PageCountAboveTheCap_IsUnavailable_AfterOneRequest()
    {
        var handler = new ScriptedHttpHandler().ThenOk(ReadPage($"[{ReadItem("E1", "a")}]", pageCount: 11));

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PageCountChangingBetweenPages_IsUnavailable()
    {
        var handler = new ScriptedHttpHandler()
            .ThenOk(ReadPage($"[{ReadItem("E1", "a")}]", pageCount: 2, totalCount: 2))
            .ThenOk(ReadPage($"[{ReadItem("E2", "b")}]", pageCount: 3, totalCount: 2));

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task SinglePageListingFewerItemsThanCounted_IsUnavailable()
    {
        var handler = new ScriptedHttpHandler().ThenOk(ReadPage($"[{ReadItem("E1", "a")}]", pageCount: 1, totalCount: 2));

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task MultiPageListingFewerItemsThanCounted_IsUnavailable()
    {
        var handler = new ScriptedHttpHandler()
            .ThenOk(ReadPage($"[{ReadItem("E1", "a")}]", pageCount: 2, totalCount: 3))
            .ThenOk(ReadPage($"[{ReadItem("E2", "b")}]", pageCount: 2, totalCount: 3));

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task TotalCountDriftingBetweenPages_IsUnavailable()
    {
        var handler = new ScriptedHttpHandler()
            .ThenOk(ReadPage($"[{ReadItem("E1", "a")}]", pageCount: 2, totalCount: 2))
            .ThenOk(ReadPage($"[{ReadItem("E2", "b")}]", pageCount: 2, totalCount: 3));

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task UnresolvedEmoteItem_CountsTowardTotalCount_AndIsOk()
    {
        var items = "[" + ReadItem(EmoteId, Alias) + """,{"alias":"gone","emote":null}]""";
        var handler = new ScriptedHttpHandler().ThenOk(ReadPage(items, pageCount: 1, totalCount: 2));

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Single(result.Value!);
    }

    [Theory]
    [InlineData("\"pageCount\":null")]
    [InlineData("\"pageCount\":\"1\"")]
    [InlineData("\"totalCount\":null")]
    [InlineData("\"totalCount\":\"0\"")]
    public async Task NonNumberCount_IsUnavailable(string countProperty)
    {
        var name = countProperty[..countProperty.IndexOf(':', StringComparison.Ordinal)];
        var other = name == "\"pageCount\"" ? "\"totalCount\":0" : "\"pageCount\":0";
        var body = Fill("{\"data\":{\"emoteSets\":{\"emoteSet\":{\"id\":\"@SET@\",\"emotes\":{" + countProperty + "," + other + ",\"items\":[]}}}}}");
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Value);
    }

    public static TheoryData<string> UnusablePages => new()
    {
        // `data: null` next to an error.
        ErrorsOnly(Error("boom", "INTERNAL_SERVER_ERROR", 500)),
        // Data present, but an error inside one item: the list is not complete.
        Fill("""{"data":{"emoteSets":{"emoteSet":{"id":"@SET@","emotes":{"pageCount":1,"items":[]}}}},"errors":@ERRORS@}""")
            .Replace("@ERRORS@", Errors(Error("x", null, null, """["emoteSets","emoteSet","emotes","items",0]""")), StringComparison.Ordinal),
        // A set 7TV does not know.
        """{"data":{"emoteSets":{"emoteSet":null}}}""",
        // Another set's id.
        ReadPage($"[{ReadItem("E1", "a")}]", pageCount: 1, setId: OtherSetId),
        // No total count.
        Fill("""{"data":{"emoteSets":{"emoteSet":{"id":"@SET@","emotes":{"pageCount":0,"items":[]}}}}}"""),
        // No page count.
        Fill("""{"data":{"emoteSets":{"emoteSet":{"id":"@SET@","emotes":{"items":[]}}}}}"""),
        // An item that is not an object, and an item without an emote id.
        ReadPage("[1]", pageCount: 1),
        ReadPage("""[{"alias":"a","emote":{"defaultName":"a"}}]""", pageCount: 1),
        // Cut off.
        "{\"data\":{\"emoteSets\":",
    };

    [Theory]
    [MemberData(nameof(UnusablePages))]
    public async Task UnusablePage_IsUnavailable(string body)
    {
        var handler = new ScriptedHttpHandler().ThenOk(body);

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task EntryWithoutAddedAtOrAddedBy_IsKept_WithNullFields()
    {
        var handler = new ScriptedHttpHandler().ThenOk(ReadPage($"[{ReadItem(EmoteId, Alias, addedAt: null, addedById: null)}]", pageCount: 1));

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        var entry = Assert.Single(result.Value!);
        Assert.Equal(new SevenTvSetEntry(EmoteId, Alias, null, null), entry);
    }

    [Fact]
    public async Task EntryWhoseEmoteDidNotResolve_IsLeftOut()
    {
        var items = "[" + ReadItem(EmoteId, Alias) + """,{"alias":"gone","addedAt":"2026-01-01T00:00:00+00:00","emote":null}]""";
        var handler = new ScriptedHttpHandler().ThenOk(ReadPage(items, pageCount: 1));

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(EmoteId, Assert.Single(result.Value!).EmoteId);
    }

    [Fact]
    public async Task Http401_IsUnauthorized()
    {
        var handler = new ScriptedHttpHandler().Then(HttpStatusCode.Unauthorized, UnauthorizedBody);

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId);

        Assert.Equal(SevenTvWriteOutcome.Unauthorized, result.Outcome);
    }

    // ------------------------------------------------------------------------------------ the deadline (K6d-5, K6e-5)

    // The deadline has passed before page 2: exactly one request, Unavailable without entries.
    [Fact]
    public async Task ADeadlinePassedBeforePage2_SendsExactlyOneRequest_AndIsUnavailable()
    {
        var handler = TwoPages();
        var left = new Queue<TimeSpan>([TimeSpan.FromSeconds(25), TimeSpan.Zero]);

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId, () => left.Dequeue());

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Value);
        Assert.Single(handler.Requests);
    }

    // Under one second left no page is requested any more (K6e-5), already before page 1.
    [Theory]
    [InlineData(999)]
    [InlineData(0)]
    public async Task LessThanASecondLeft_RequestsNoPage(int milliseconds)
    {
        var handler = TwoPages();

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId, () => TimeSpan.FromMilliseconds(milliseconds));

        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    // 3 s left, page 2 answers after 1 s: the read is complete.
    [Fact]
    public async Task ThreeSecondsLeft_APageOfOneSecond_IsReadCompletely()
    {
        var handler = new ScriptedHttpHandler()
            .ThenOk(ReadPage($"[{ReadItem("E1", "a")}]", pageCount: 2, totalCount: 2))
            .ThenOkAfter(TimeSpan.FromSeconds(1), ReadPage($"[{ReadItem("E2", "b")}]", pageCount: 2, totalCount: 2));
        var end = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 3;

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId, () => LeftUntil(end));

        Assert.Equal(SevenTvWriteOutcome.Ok, result.Outcome);
        Assert.Equal(["E1", "E2"], result.Value!.Select(e => e.EmoteId));
    }

    // 3 s left, page 2 would answer after 5 s: cut at the deadline after about 3 s, Unavailable (never a Timeout, never an
    // exception), no third request.
    [Fact]
    public async Task ThreeSecondsLeft_APageOfFiveSeconds_IsCutAtTheDeadline_AsUnavailable()
    {
        var handler = new ScriptedHttpHandler()
            .ThenOk(ReadPage($"[{ReadItem("E1", "a")}]", pageCount: 3, totalCount: 3))
            .ThenOkAfter(TimeSpan.FromSeconds(5), ReadPage($"[{ReadItem("E2", "b")}]", pageCount: 3, totalCount: 3))
            .ThenOk(ReadPage($"[{ReadItem("E3", "c")}]", pageCount: 3, totalCount: 3));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var end = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 3;

        var result = await Client(handler).ReadSetEntriesAsync(Token, SetId, () => LeftUntil(end));

        started.Stop();
        Assert.Equal(SevenTvWriteOutcome.Unavailable, result.Outcome);
        Assert.Equal(2, handler.Requests.Count);
        Assert.InRange(started.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(4.5));
    }

    // Plenty of time left: every page keeps the client's own 10 s, a hanging page is a Timeout as before.
    [Fact]
    public async Task PlentyOfTimeLeft_AHangingPageIsATimeout()
    {
        var handler = new ScriptedHttpHandler().ThenHang();

        var result = await Client(handler, timeout: TimeSpan.FromMilliseconds(200)).ReadSetEntriesAsync(Token, SetId, () => TimeSpan.FromSeconds(25));

        Assert.Equal(SevenTvWriteOutcome.Timeout, result.Outcome);
    }

    private static ScriptedHttpHandler TwoPages() =>
        new ScriptedHttpHandler()
            .ThenOk(ReadPage($"[{ReadItem("E1", "a")}]", pageCount: 2, totalCount: 2))
            .ThenOk(ReadPage($"[{ReadItem("E2", "b")}]", pageCount: 2, totalCount: 2));

    private static TimeSpan LeftUntil(long endTimestamp) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(System.Diagnostics.Stopwatch.GetTimestamp(), endTimestamp);
}
