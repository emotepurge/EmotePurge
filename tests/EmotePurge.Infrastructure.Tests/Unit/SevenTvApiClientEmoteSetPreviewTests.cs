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
/// Pins <see cref="SevenTvApiClient.GetEmoteSetPreviewAsync"/> — the v4 preview query the
/// foreign-channel-import spec (F1 step 3, F3) added. Three things this file exists to prove:
/// pagination actually walks every page, the <c>MaxSetEntryPages</c> guard reports itself as
/// <c>truncated</c> instead of quietly cutting the list short, and a confirmed 7TV rate limit
/// (<c>extensions.status: 429</c>, disguised as HTTP 200) is never mistaken for the legitimate empty
/// set the state table treats as success — AK 7, "der teuerste Einzelfehler der ganzen Spec".
/// </summary>
public class SevenTvApiClientEmoteSetPreviewTests
{
    private const string SetId = "01FRY81K4800085N93FNKSBYXS";

    [Fact]
    public async Task MultiplePages_AreAllWalked_AndConcatenated()
    {
        // Two pages, three items total — pageCount 2 tells the client there is a second page even
        // though the first only carries one item, and totalCount matches the concatenated result
        // exactly, so nothing here should read as truncated.
        var handler = new PagedStubHandler(page => page switch
        {
            1 => Page(totalCount: 3, pageCount: 2, ("e1", "AliasOne", "DefaultOne", 10, 1, true)),
            2 => Page(totalCount: 3, pageCount: 2, ("e2", "AliasTwo", "DefaultTwo", 20, 2, true), ("e3", "AliasThree", "DefaultThree", 30, 3, true)),
            _ => throw new InvalidOperationException($"unexpected page {page}")
        });
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        Assert.Equal([1, 2], handler.RequestedPages);
        Assert.Equal(3, result.Preview!.TotalCount);
        Assert.False(result.Preview.Truncated);
        Assert.Equal(["e1", "e2", "e3"], result.Preview.Items.Select(i => i.SevenTvEmoteId));
    }

    [Fact]
    public async Task PageCountAtOrBelowOne_StopsAfterTheFirstPage_NoOverfetch()
    {
        var handler = new PagedStubHandler(page => page == 1
            ? Page(totalCount: 1, pageCount: 1, ("e1", "Alias", "Default", null, null, true))
            : throw new InvalidOperationException($"unexpected page {page}"));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        Assert.Single(handler.RequestedPages);
        Assert.False(result.Preview!.Truncated);
    }

    /// <summary>
    /// F3: the runaway guard (<c>MaxSetEntryPages</c> = 10 in <see cref="SevenTvApiClient"/>) must
    /// not cut the list short in silence. Every one of the ten allowed pages reports eleven pages
    /// total, so the loop stops at the cap while 7TV still says there is more — that has to come back
    /// as <c>Truncated == true</c> with the real <c>TotalCount</c>, not a quietly short list.
    /// </summary>
    [Fact]
    public async Task ReachingThePageCap_ReportsTruncated_WithTheRealTotalCount()
    {
        var handler = new PagedStubHandler(page => Page(totalCount: 5000, pageCount: 11, ($"e{page}", $"Alias{page}", $"Default{page}", null, null, true)));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        // MaxSetEntryPages: exactly ten requests, never an eleventh even though page_count said 11.
        Assert.Equal(10, handler.RequestedPages.Count);
        Assert.Equal(Enumerable.Range(1, 10), handler.RequestedPages);
        Assert.True(result.Preview!.Truncated);
        Assert.Equal(5000, result.Preview.TotalCount);
        Assert.Equal(10, result.Preview.Items.Count);
    }

    /// <summary>
    /// AK 7, the central distinction of the whole spec: HTTP 200 carrying a GraphQL error with
    /// <c>extensions.status: 429</c> must map to <see cref="SevenTvPreviewLookupStatus.RateLimited"/>,
    /// never to the legitimate empty-set success the very next test proves is a real, distinct case.
    /// </summary>
    [Fact]
    public async Task RateLimitExtensionsStatus_IsReportedAsRateLimited_NotAsAnEmptySet()
    {
        const string rateLimitedPayload =
            """{"data":null,"errors":[{"message":"too many requests","extensions":{"code":"RATE_LIMITED","status":429}}]}""";
        var handler = new PagedStubHandler(_ => rateLimitedPayload);
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.RateLimited, result.Status);
        Assert.Null(result.Preview);
    }

    /// <summary>The other half of the AK 7 distinction: a genuinely empty active set is success, not
    /// an error — the exact case a 429 must never be confused with.</summary>
    [Fact]
    public async Task GenuinelyEmptySet_IsOk_WithZeroItems_NotRateLimited()
    {
        var handler = new PagedStubHandler(_ => Page(totalCount: 0, pageCount: 1));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        Assert.Empty(result.Preview!.Items);
        Assert.False(result.Preview.Truncated);
    }

    /// <summary>
    /// A GraphQL error with no rate-limit status (a genuine upstream failure) must still map to
    /// <c>Unavailable</c>, distinct from both <c>RateLimited</c> above and the empty-set success.
    /// </summary>
    [Fact]
    public async Task GraphQlErrorWithoutRateLimitStatus_IsUnavailable()
    {
        const string errorPayload =
            """{"data":null,"errors":[{"message":"LOAD_ERROR set not found","extensions":{"code":"LOAD_ERROR","status":404}}]}""";
        var handler = new PagedStubHandler(_ => errorPayload);
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Preview);
    }

    /// <summary>
    /// The set-local alias and the emote's global default name are two different fields with two
    /// different meanings (spec DTO contract, section 4) and must never collapse into one — a
    /// regression here would silently make every renamed-in-this-set emote look like it kept its
    /// upstream name.
    /// </summary>
    [Fact]
    public async Task AliasAndDefaultName_AreKeptDistinct_WhenTheyDiffer()
    {
        var handler = new PagedStubHandler(_ => Page(totalCount: 1, pageCount: 1, ("e1", "renamedInThisSet", "OriginalUploadName", 42, 7, true)));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        var item = Assert.Single(result.Preview!.Items);
        Assert.Equal("renamedInThisSet", item.Alias);
        Assert.Equal("OriginalUploadName", item.DefaultName);
        Assert.Equal(42, item.TopAllTime);
        Assert.Equal(7, item.Trending);
        // Built from the emote id, never fetched via the (far more expensive) Emote.images list —
        // see BuildForeignImageUrl. This emote is animated, hence the still rendition.
        Assert.Equal("https://cdn.7tv.app/emote/e1/4x_static.webp", item.ImageUrl);
    }

    /// <summary>
    /// The image url has to follow the emote's animated flag, because 7TV only ever materialises the
    /// "_static" rendition for an animated source — it is the flattened first frame, and a still
    /// emote has nothing to flatten. Asking for it anyway answers 404, which is what this pins:
    /// measured live 2026-09-09 against HandOfBlood's set, 305 of its 956 emotes (31.9 %) are stills
    /// and every one of them 404'd on 4x_static.webp while answering 200 on 4x.webp. The bug survived
    /// its first review precisely because only the animated branch was covered here.
    /// </summary>
    [Theory]
    [InlineData(true, "https://cdn.7tv.app/emote/e1/4x_static.webp")]
    [InlineData(false, "https://cdn.7tv.app/emote/e1/4x.webp")]
    public async Task ImageUrl_FollowsTheAnimatedFlag(bool animated, string expected)
    {
        var handler = new PagedStubHandler(_ => Page(totalCount: 1, pageCount: 1, ("e1", "Alias", "Default", null, null, animated)));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        var item = Assert.Single(result.Preview!.Items);
        Assert.Equal(expected, item.ImageUrl);
    }

    /// <summary>
    /// The url built above is only as good as the flag it reads, so the query must actually ask for
    /// it. Without this the stub would keep handing the flag over even after a regression dropped
    /// <c>flags { animated }</c> from the real query — every parser assertion above would stay green
    /// while production went back to guessing.
    /// </summary>
    [Fact]
    public async Task PreviewQuery_AsksForTheAnimatedFlag()
    {
        var handler = new PagedStubHandler(_ => Page(totalCount: 0, pageCount: 1));
        var client = CreateClient(handler);

        await client.GetEmoteSetPreviewAsync(SetId);

        var query = Assert.Single(handler.SentQueries);
        Assert.Contains("flags { animated }", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A payload without the flag must still yield a url that loads. The v4 schema types
    /// <c>Emote.flags</c> and its <c>animated</c> member as non-null, so this is a guard rather than
    /// an observed behaviour — but the fallback direction is the whole point: 4x.webp exists for
    /// every emote (on an animated one it simply carries all frames), while the other guess renders
    /// nothing at all for a third of a typical set.
    /// </summary>
    [Fact]
    public async Task MissingAnimatedFlag_FallsBackToTheRenditionThatAlwaysExists()
    {
        var handler = new PagedStubHandler(_ => PageWithoutFlags("e1"));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        var item = Assert.Single(result.Preview!.Items);
        Assert.Equal("https://cdn.7tv.app/emote/e1/4x.webp", item.ImageUrl);
    }

    // Strips the flags object back out of an otherwise normal single-item page, so the guard above is
    // exercised against the same shape everything else here uses.
    private static string PageWithoutFlags(string emoteId)
    {
        var root = JsonNode.Parse(Page(totalCount: 1, pageCount: 1, (emoteId, "Alias", "Default", null, null, true)))!;
        root["data"]!["emote_sets"]!["emote_set"]!["emotes"]!["items"]![0]!["emote"]!.AsObject().Remove("flags");
        return root.ToJsonString();
    }

    // Built through JsonNode rather than a hand-assembled string: the response nests five levels
    // deep (data.emote_sets.emote_set.emotes.items[].emote.scores), and getting the brace-counting
    // right in a raw string literal for that shape is exactly the kind of thing worth not doing by
    // hand. name/capacity default to values that reproduce the pre-F6 shape exactly (no name, a
    // capacity that already normalises to null) so every call site above this line stays unchanged.
    private static string Page(
        int totalCount, int pageCount, params (string Id, string Alias, string DefaultName, int? TopAllTime, int? TrendingDay, bool Animated)[] items) =>
        Page(totalCount, pageCount, name: null, capacity: 0, items);

    private static string Page(
        int totalCount, int pageCount, string? name, int capacity,
        params (string Id, string Alias, string DefaultName, int? TopAllTime, int? TrendingDay, bool Animated)[] items)
    {
        var itemsArray = new JsonArray();
        foreach (var item in items)
        {
            itemsArray.Add(new JsonObject
            {
                ["alias"] = item.Alias,
                ["emote"] = new JsonObject
                {
                    ["id"] = item.Id,
                    ["default_name"] = item.DefaultName,
                    ["flags"] = new JsonObject
                    {
                        ["animated"] = item.Animated,
                    },
                    ["scores"] = new JsonObject
                    {
                        ["top_all_time"] = item.TopAllTime ?? 0,
                        ["trending_day"] = item.TrendingDay ?? 0,
                    },
                },
            });
        }

        var root = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["emote_sets"] = new JsonObject
                {
                    ["emote_set"] = new JsonObject
                    {
                        ["name"] = name,
                        ["capacity"] = capacity,
                        ["emotes"] = new JsonObject
                        {
                            ["total_count"] = totalCount,
                            ["page_count"] = pageCount,
                            ["items"] = itemsArray,
                        },
                    },
                },
            },
        };

        return root.ToJsonString();
    }

    /// <summary>F6/AK 28: name and a non-zero capacity are read off the set object and land on the
    /// assembled preview alongside the paginated entries.</summary>
    [Fact]
    public async Task NameAndCapacity_AreReadFromTheSetObject()
    {
        var handler = new PagedStubHandler(_ => Page(
            totalCount: 1, pageCount: 1, name: "HandOfBlood's set", capacity: 956,
            ("e1", "Alias", "Default", null, null, true)));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        Assert.Equal("HandOfBlood's set", result.Preview!.Name);
        Assert.Equal(956, result.Preview.Capacity);
    }

    /// <summary>F6: a reported capacity of <c>0</c> normalises to <c>null</c>, the same idiom the
    /// tracked-channel sync path (<c>SevenTvApiClient.cs</c>, the "0 ? … : null" comment) and the
    /// set-list path already use — a bare <c>0</c> would make the UI claim the set is full.</summary>
    [Fact]
    public async Task ZeroCapacity_NormalisesToNull()
    {
        var handler = new PagedStubHandler(_ => Page(
            totalCount: 0, pageCount: 1, name: "Empty set", capacity: 0));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        Assert.Null(result.Preview!.Capacity);
    }

    /// <summary>
    /// AK 28/#74: two set entries that (due to 7TV's own merge bug, #74) share one
    /// <c>SevenTvEmoteId</c> are kept as two separate rows, never deduplicated — <c>totalCount</c>
    /// counts both, because it stands for the occupied slots of the *target* set, not for distinct
    /// emotes.
    /// </summary>
    [Fact]
    public async Task DuplicateSevenTvEmoteIds_AreKeptAsTwoSeparateEntries_NotDeduplicated()
    {
        var handler = new PagedStubHandler(_ => Page(
            totalCount: 2, pageCount: 1,
            ("e1", "AliasOne", "Default", null, null, true),
            ("e1", "AliasTwo", "Default", null, null, true)));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        Assert.Equal(2, result.Preview!.TotalCount);
        Assert.Equal(2, result.Preview.Items.Count);
        Assert.Equal(["e1", "e1"], result.Preview.Items.Select(i => i.SevenTvEmoteId));
        Assert.Equal(["AliasOne", "AliasTwo"], result.Preview.Items.Select(i => i.Alias));
    }


    /// <summary>
    /// E5b, the unit the provider-wide rate limit is actually counted in: one permit per page, not
    /// one per lookup. A ten-page walk is ten upstream requests, and a budget charged once around the
    /// whole thing would have let all ten travel on a single permit — twelve times the documented
    /// rate once the two resolution calls are counted too.
    /// </summary>
    [Fact]
    public async Task EveryPage_ChargesExactlyOneRequestPermit()
    {
        var handler = new PagedStubHandler(page => Page(totalCount: 10, pageCount: 10, ($"e{page}", $"Alias{page}", $"Default{page}", null, null, true)));
        var budget = new RecordingForeignUpstreamRequestBudget();
        var client = CreateClient(handler, budget);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        Assert.Equal(10, handler.RequestedPages.Count);
        Assert.Equal(10, budget.Charges);
    }

    /// <summary>
    /// A refused permit really does mean "no request was made": the permit is taken before the page
    /// is requested, and the outcome is its own status rather than a quietly short list — a partial
    /// set answered as if it were whole is the exact failure F3 exists to prevent.
    /// </summary>
    [Fact]
    public async Task ARefusedPermit_StopsThePagination_WithoutIssuingTheRequest()
    {
        var handler = new PagedStubHandler(page => Page(totalCount: 2500, pageCount: 5, ($"e{page}", $"Alias{page}", $"Default{page}", null, null, true)));
        var budget = new RecordingForeignUpstreamRequestBudget(grantCount: 2);
        var client = CreateClient(handler, budget);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.BudgetExhausted, result.Status);
        Assert.Null(result.Preview);
        Assert.Equal([1, 2], handler.RequestedPages);
    }

    /// <summary>
    /// A reset hint in the error payload is used when it is there. Whether 7TV actually sends one is
    /// unconfirmed (see <c>SevenTvGqlErrorExtensionsDto.Headers</c>) — which is the point of reading
    /// it opportunistically instead of arguing about it: if it shows up, the breaker honors it; if it
    /// never does, nothing below changes.
    /// </summary>
    [Fact]
    public async Task AResetHintInTheErrorExtensions_BecomesTheRetryAfter()
    {
        var handler = new PagedStubHandler(_ => RateLimitedPayload(resetHintSeconds: 3583));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(3583), result.RetryAfter);
    }

    /// <summary>The documented order: reset hint first, <c>Retry-After</c> second, the breaker's own
    /// 60 s default last (E4).</summary>
    [Fact]
    public async Task AResetHint_BeatsTheRetryAfterHeader()
    {
        var handler = new PagedStubHandler(_ => RateLimitedPayload(resetHintSeconds: 3583), retryAfterHeaderSeconds: 30);
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(TimeSpan.FromSeconds(3583), result.RetryAfter);
    }

    [Fact]
    public async Task WithoutAResetHint_TheRetryAfterHeaderIsUsed()
    {
        var handler = new PagedStubHandler(_ => RateLimitedPayload(resetHintSeconds: null), retryAfterHeaderSeconds: 30);
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
    }

    /// <summary>
    /// A value that cannot plausibly be "seconds remaining" — a Unix timestamp, say — is dropped
    /// rather than reinterpreted. Guessing at a unit we have never verified would, when wrong, hold
    /// the circuit breaker shut for decades; falling back to the 60 s default costs one extra probe.
    /// </summary>
    [Fact]
    public async Task AnImplausibleResetHint_IsIgnored_LeavingTheDefaultOpenDurationToApply()
    {
        var handler = new PagedStubHandler(_ => RateLimitedPayload(resetHintSeconds: 1893456000));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.RateLimited, result.Status);
        Assert.Null(result.RetryAfter);
    }

    // The semantic-429 payload, optionally carrying the unverified extensions.headers reset hint.
    private static string RateLimitedPayload(int? resetHintSeconds)
    {
        var extensions = new JsonObject { ["code"] = "RATE_LIMITED", ["status"] = 429 };
        if (resetHintSeconds is { } seconds)
        {
            extensions["headers"] = new JsonObject { ["x-ratelimit-search-reset"] = seconds };
        }

        return new JsonObject
        {
            ["data"] = null,
            ["errors"] = new JsonArray { new JsonObject { ["message"] = "too many requests", ["extensions"] = extensions } },
        }.ToJsonString();
    }

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

    /// <summary>Answers every POST with the response the given function derives from the request's
    /// GraphQL <c>page</c> variable, and records the pages actually requested.</summary>
    private sealed class PagedStubHandler(Func<int, string> responseForPage, int? retryAfterHeaderSeconds = null) : HttpMessageHandler
    {
        public List<int> RequestedPages { get; } = [];

        public List<string> SentQueries { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var page = doc.RootElement.GetProperty("variables").GetProperty("page").GetInt32();
            RequestedPages.Add(page);
            SentQueries.Add(doc.RootElement.GetProperty("query").GetString() ?? string.Empty);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseForPage(page), Encoding.UTF8, "application/json"),
            };
            if (retryAfterHeaderSeconds is { } seconds)
            {
                response.Headers.Add("Retry-After", seconds.ToString());
            }

            return response;
        }
    }
}
