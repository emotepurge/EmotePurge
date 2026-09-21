using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="SevenTvApiClient.GetEmoteSetListForTwitchUserAsync"/> — the v4
/// <c>userByConnection</c> query behind all three set-list routes (spec 2026-09-20, 6.1/E7, AK 21).
/// </summary>
/// <remarks>
/// <para>
/// <b>The fixture is a real answer, not a drawing.</b> Every payload below is the shape 7TV
/// returned live on 2026-09-20 for <c>platformId: 49140130</c> (Sonde 7) — four sets, the personal
/// one among them, each with its owner — and the "no such account" payload is the one it returned
/// for <c>platformId: 999999999999</c>. F17 is why that matters: a wrong query does not come back
/// looking wrong, it comes back looking like 7TV being permanently down, and a fixture invented
/// alongside the query would feed both sides of the test from the same wrong assumption.
/// </para>
/// <para>
/// The three failure shapes are kept apart on purpose (AK 21). In none of them is the answer a
/// quietly empty list: "this account has no sets" and "we could not read the sets" are different
/// facts, and only the first may ever reach a picker as an empty offer.
/// </para>
/// </remarks>
public class SevenTvApiClientEmoteSetListTests
{
    private const string TwitchId = "49140130";
    private const string AccountId = "01GQA28FCR0002Q9KS8SKQKVXX";
    private const string ActiveSetId = "01GV88A38G0006FW5TVZVMG507";

    /// <summary>
    /// The measured answer, set for set: four sets where v3 reports three, the missing one being
    /// the personal set a picker has to be able to see in order to refuse it. The assertions on the
    /// query text are part of the case rather than a separate one — without them the stub would
    /// keep answering happily after a regression dropped <c>kind</c> or <c>style</c>, and every
    /// mapping assertion here would stay green while production went blind.
    /// </summary>
    [Fact]
    public async Task TheMeasuredAnswer_IsMappedSetForSet()
    {
        var handler = new StubHandler(_ => MeasuredAnswer());
        var budget = new RecordingForeignUpstreamRequestBudget();
        var client = CreateClient(handler, budget);

        var result = await client.GetEmoteSetListForTwitchUserAsync(TwitchId);

        Assert.Equal(SevenTvEmoteSetListLookupStatus.Ok, result.Status);
        Assert.Equal(ActiveSetId, result.Listing!.ActiveEmoteSetId);
        Assert.Equal(
            [ActiveSetId, "01HMHSTX2G000CNKGAKWBJQA56", "01J94NYQR0000D15QN0BDGN85E", "01J94Y3JDR0005G1FWF2H9ZHJT"],
            result.Listing.Sets.Select(s => s.Id));
        Assert.Equal(
            ["HandOfBlood's Emotes", "Personal Emotes", "Halloween Set", "Christmas Set"],
            result.Listing.Sets.Select(s => s.Name));
        Assert.Equal(["NORMAL", "PERSONAL", "NORMAL", "NORMAL"], result.Listing.Sets.Select(s => s.Kind));
        Assert.Equal([1000, 5, 1000, 1000], result.Listing.Sets.Select(s => s.Capacity));
        Assert.All(result.Listing.Sets, set => Assert.Equal("HandOfBlood", set.OwnerDisplayName));

        // The three fields the answer above is only as good as: the connection lookup itself, the
        // kind that decides selectability, and the active set id that saves a second request.
        var query = Assert.Single(handler.SentQueries);
        Assert.Contains("userByConnection(platform: TWITCH, platformId: $pid)", query, StringComparison.Ordinal);
        Assert.Contains("style { activeEmoteSetId }", query, StringComparison.Ordinal);
        Assert.Contains("kind", query, StringComparison.Ordinal);
        Assert.Contains("owner { id mainConnection { platformDisplayName } }", query, StringComparison.Ordinal);

        // One account, one request, one permit (F14, AK 24).
        Assert.Equal(1, budget.Charges);
    }

    /// <summary>
    /// Capacity <c>0</c> reads as "not reported", exactly as on the channel-state path: an absent
    /// field and a genuine zero are indistinguishable here, and showing either as a capacity would
    /// make the UI claim the set is full. An owner without a main connection leaves the display
    /// name null rather than inventing one — the field is decoration, and nothing ever compares it.
    /// </summary>
    [Fact]
    public async Task ZeroCapacityAndAMissingOwnerConnection_ReadAsNull()
    {
        var handler = new StubHandler(_ => Answer(
            activeEmoteSetId: null,
            sets: new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "01AAA",
                    ["name"] = "Nameless Owner",
                    ["capacity"] = 0,
                    ["kind"] = "NORMAL",
                    ["owner"] = new JsonObject { ["id"] = AccountId, ["mainConnection"] = null },
                },
            }));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetListForTwitchUserAsync(TwitchId);

        Assert.Equal(SevenTvEmoteSetListLookupStatus.Ok, result.Status);
        Assert.Null(result.Listing!.ActiveEmoteSetId);
        var set = Assert.Single(result.Listing.Sets);
        Assert.Null(set.Capacity);
        Assert.Null(set.OwnerDisplayName);
    }

    /// <summary>
    /// Spec section 32: the set-centric import's owner check answers from these lists, so the two
    /// ids it compares — the account's own (<c>userByConnection.id</c>) and each set's owner
    /// (<c>owner.id</c>) — have to survive the mapping. Both were in the measured query all along
    /// and cost no extra field.
    /// </summary>
    [Fact]
    public async Task TheMeasuredAnswer_CarriesTheAccountId_AndEverySetsOwnerId()
    {
        var client = CreateClient(new StubHandler(_ => MeasuredAnswer()));

        var result = await client.GetEmoteSetListForTwitchUserAsync(TwitchId);

        Assert.Equal(AccountId, result.Listing!.SevenTvUserId);
        Assert.All(result.Listing.Sets, set => Assert.Equal(AccountId, set.OwnerSevenTvUserId));
    }

    /// <summary>
    /// An account without an id cannot vouch for owning anything — the owner check compares against
    /// exactly that id — so the answer is a failure, not an account that owns nothing.
    /// </summary>
    [Fact]
    public async Task AUserWithoutAnId_IsUnavailable()
    {
        var handler = new StubHandler(_ => new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["users"] = new JsonObject
                {
                    ["userByConnection"] = new JsonObject
                    {
                        ["style"] = new JsonObject { ["activeEmoteSetId"] = ActiveSetId },
                        ["emoteSets"] = new JsonArray(),
                    },
                },
            },
        }.ToJsonString());
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetListForTwitchUserAsync(TwitchId);

        Assert.Equal(SevenTvEmoteSetListLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Listing);
    }

    /// <summary>
    /// The one failure shape that is not a failure (measured 2026-09-20 with
    /// <c>platformId: 999999999999</c>): <c>userByConnection: null</c> at HTTP 200 with no
    /// <c>errors</c> block means 7TV knows the connection and carries no account for it. It must
    /// stay distinct from every unavailability below, because only this one may answer a caller
    /// with an empty offer.
    /// </summary>
    [Fact]
    public async Task NoAccountForTheConnection_IsNoSevenTvAccount()
    {
        var handler = new StubHandler(_ => """{"data":{"users":{"userByConnection":null}}}""");
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetListForTwitchUserAsync(TwitchId);

        Assert.Equal(SevenTvEmoteSetListLookupStatus.NoSevenTvAccount, result.Status);
        Assert.Null(result.Listing);
    }

    /// <summary>
    /// Third review round, P2, F17: <c>userByConnection: null</c> means "no account" only when it
    /// comes back clean. Next to a non-429 <c>errors</c> block — a partial GraphQL answer, schema
    /// drift on a sibling field, say — it is 7TV failing on this query, not naming an account
    /// missing, and must not be cached as a hit or counted as evidence 7TV is healthy.
    /// </summary>
    [Fact]
    public async Task NullUserWithANonRateLimitError_IsUnavailable_NotNoSevenTvAccount()
    {
        var handler = new StubHandler(_ =>
            """{"data":{"users":{"userByConnection":null}},"errors":[{"message":"internal server error"}]}""");
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetListForTwitchUserAsync(TwitchId);

        Assert.Equal(SevenTvEmoteSetListLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Listing);
    }

    /// <summary>
    /// An account we can see whose sets we cannot read is <c>Unavailable</c>, never an account with
    /// no sets. Schema drift is exactly how this arrives — the member simply stops being there —
    /// and a silent empty list would tell a picker that a streamer has nothing to offer.
    /// </summary>
    [Fact]
    public async Task AUserWithoutAnEmoteSetsMember_IsUnavailable_NotAnEmptyList()
    {
        var handler = new StubHandler(_ => new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["users"] = new JsonObject
                {
                    ["userByConnection"] = new JsonObject
                    {
                        ["id"] = AccountId,
                        ["style"] = new JsonObject { ["activeEmoteSetId"] = ActiveSetId },
                    },
                },
            },
        }.ToJsonString());
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetListForTwitchUserAsync(TwitchId);

        Assert.Equal(SevenTvEmoteSetListLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Listing);
    }

    /// <summary>
    /// A GraphQL error — 7TV answers those with HTTP 200 and a null <c>data</c> — is the shape a
    /// malformed query of ours takes (F17). Unavailable, and deliberately indistinguishable from a
    /// real outage: only a live probe separates the two, which is why the query in this client is
    /// the one that was measured.
    /// </summary>
    [Fact]
    public async Task AGraphQlErrorResponse_IsUnavailable()
    {
        var handler = new StubHandler(_ =>
            """{"data":null,"errors":[{"message":"Cannot query field \"emoteSets\" on type \"User\"","extensions":{"code":"GRAPHQL_VALIDATION_FAILED"}}]}""");
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetListForTwitchUserAsync(TwitchId);

        Assert.Equal(SevenTvEmoteSetListLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Listing);
    }

    /// <summary>No response at all — DNS, TLS, a dropped connection, a timeout.</summary>
    [Fact]
    public async Task ATransportFailure_IsUnavailable()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetListForTwitchUserAsync(TwitchId);

        Assert.Equal(SevenTvEmoteSetListLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Listing);
    }

    /// <summary>
    /// The distinction the circuit breaker is built on (E4a): a confirmed overload, which 7TV
    /// disguises as HTTP 200 with <c>extensions.status: 429</c>, is <c>RateLimited</c> and not the
    /// generic <c>Unavailable</c> — the first opens the breaker at once and for as long as 7TV
    /// asked, the second has to accumulate five times and only ever lasts a minute.
    /// </summary>
    [Fact]
    public async Task ADisguisedRateLimit_IsReportedAsRateLimited_WithItsRetryAfter()
    {
        var handler = new StubHandler(
            _ => """{"data":null,"errors":[{"message":"too many requests","extensions":{"code":"RATE_LIMITED","status":429}}]}""",
            retryAfterHeaderSeconds: 45);
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetListForTwitchUserAsync(TwitchId);

        Assert.Equal(SevenTvEmoteSetListLookupStatus.RateLimited, result.Status);
        Assert.Null(result.Listing);
        Assert.Equal(TimeSpan.FromSeconds(45), result.RetryAfter);
    }

    // The answer measured live on 2026-09-20 against platformId 49140130 (Sonde 7), redacted of
    // nothing that this client reads.
    private static string MeasuredAnswer() => Answer(
        ActiveSetId,
        new JsonArray
        {
            Set(ActiveSetId, "HandOfBlood's Emotes", 1000, "NORMAL"),
            Set("01HMHSTX2G000CNKGAKWBJQA56", "Personal Emotes", 5, "PERSONAL"),
            Set("01J94NYQR0000D15QN0BDGN85E", "Halloween Set", 1000, "NORMAL"),
            Set("01J94Y3JDR0005G1FWF2H9ZHJT", "Christmas Set", 1000, "NORMAL"),
        });

    private static JsonObject Set(string id, string name, int capacity, string kind) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["capacity"] = capacity,
        ["kind"] = kind,
        ["owner"] = new JsonObject
        {
            ["id"] = AccountId,
            ["mainConnection"] = new JsonObject { ["platformDisplayName"] = "HandOfBlood" },
        },
    };

    private static string Answer(string? activeEmoteSetId, JsonArray sets) => new JsonObject
    {
        ["data"] = new JsonObject
        {
            ["users"] = new JsonObject
            {
                ["userByConnection"] = new JsonObject
                {
                    ["id"] = AccountId,
                    ["style"] = new JsonObject { ["activeEmoteSetId"] = activeEmoteSetId },
                    ["emoteSets"] = sets,
                },
            },
        },
    }.ToJsonString();

    private static SevenTvApiClient CreateClient(
        HttpMessageHandler handler, IForeignUpstreamRequestBudget? requestBudget = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") };
        return new SevenTvApiClient(
            httpClient,
            new RecordingRateLimitTelemetry(),
            requestBudget ?? new RecordingForeignUpstreamRequestBudget(),
            new RecordingLogger<SevenTvApiClient>());
    }

    /// <summary>Answers every POST with one body and records the query and variables it was sent.</summary>
    private sealed class StubHandler(Func<string, string> responseForTwitchId, int? retryAfterHeaderSeconds = null)
        : HttpMessageHandler
    {
        public List<string> SentQueries { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            SentQueries.Add(doc.RootElement.GetProperty("query").GetString() ?? string.Empty);
            var pid = doc.RootElement.GetProperty("variables").GetProperty("pid").GetString() ?? string.Empty;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseForTwitchId(pid), Encoding.UTF8, "application/json"),
            };
            if (retryAfterHeaderSeconds is { } seconds)
            {
                response.Headers.Add("Retry-After", seconds.ToString());
            }

            return response;
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
