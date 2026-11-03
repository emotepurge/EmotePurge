using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Logging;
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
        // Both pages, then page 1 once more: the verification re-read of a multi-page read (#457).
        Assert.Equal([1, 2, 1], handler.RequestedPages);
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
    /// Vorentscheidung 4 (K2 task brief)/spec 6.4: a well-formed HTTP 200 with no <c>errors</c> block
    /// whose <c>emoteSet</c> is <c>null</c> is 7TV's own "this id does not exist" answer, not a
    /// failure to reach or parse 7TV. Before this distinction existed it fell into the very same
    /// <c>Unavailable</c> bucket as <see cref="GraphQlErrorWithoutRateLimitStatus_IsUnavailable"/>
    /// above — indistinguishable from a genuine outage, which meant a manipulated
    /// <c>?emoteSetId=</c> answered 503 instead of 404 and fed the circuit breaker a failure for an
    /// id nobody but the caller controls.
    /// </summary>
    [Fact]
    public async Task UnknownSetId_WithAWellFormed200AndNoErrors_IsReportedAsNotFound_NotUnavailable()
    {
        const string unknownSetPayload = """{"data":{"emote_sets":{"emote_set":null}}}""";
        var handler = new PagedStubHandler(_ => unknownSetPayload);
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.NotFound, result.Status);
        Assert.Null(result.Preview);
    }

    /// <summary>
    /// A partial GraphQL answer — <c>emoteSet: null</c> next to a non-429 <c>errors</c> block — is 7TV
    /// failing, not 7TV naming no such set. Reading it as NotFound would book a breaker success for
    /// an error response.
    /// </summary>
    [Fact]
    public async Task PartialAnswer_WithNullSetAndAnErrorsBlock_IsUnavailable_NotNotFound()
    {
        const string partialPayload =
            """{"data":{"emote_sets":{"emote_set":null}},"errors":[{"message":"boom","extensions":{"code":"INTERNAL","status":500}}]}""";
        var client = CreateClient(new PagedStubHandler(_ => partialPayload));

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Preview);
    }

    /// <summary>The confirmed-429 branch runs ahead of the NotFound check, also in the partial shape.</summary>
    [Fact]
    public async Task PartialAnswer_WithNullSetAndA429Error_IsRateLimited()
    {
        const string partialPayload =
            """{"data":{"emote_sets":{"emote_set":null}},"errors":[{"message":"too many requests","extensions":{"code":"RATE_LIMITED","status":429}}]}""";
        var client = CreateClient(new PagedStubHandler(_ => partialPayload));

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.RateLimited, result.Status);
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

    /// <summary>
    /// #346: the preview query selects the set entry's date, aliased like every other v4 field here.
    /// Pinned on the query text because a regression that dropped it would leave every parser
    /// assertion green (the stubs hand the field over regardless) while production lost the gate.
    /// </summary>
    [Fact]
    public async Task PreviewQuery_AsksForTheSetEntryDate()
    {
        var handler = new PagedStubHandler(_ => Page(totalCount: 0, pageCount: 1));
        var client = CreateClient(handler);

        await client.GetEmoteSetPreviewAsync(SetId);

        var query = Assert.Single(handler.SentQueries);
        Assert.Contains("items { alias added_at: addedAt emote {", query, StringComparison.Ordinal);
    }

    // The recorded answer still says page_count 43 (a 845-emote set); the stub would be asked for 42
    // more pages that the five-item cut does not have, so the test sees it as the single page it is.
    private static JsonNode LiveAddedAtFixture()
    {
        var payload = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Unit", "TestData", "emote-set-preview-added-at.json")))!;
        payload["data"]!["emote_sets"]!["emote_set"]!["emotes"]!["page_count"] = 1;
        return payload;
    }

    /// <summary>
    /// #346, AC 3, against the recorded live answer: <c>emote-set-preview-added-at.json</c> is the
    /// unchanged response 7TV gave on 2026-10-09 to exactly this client's preview query (set
    /// 01J94NYQR0000D15QN0BDGN85E, page 1), cut to its first five items. Every item carries
    /// <c>added_at</c> as an ISO timestamp with milliseconds and a <c>+00:00</c> offset.
    /// </summary>
    [Fact]
    public async Task AddedAt_OfTheRecordedLiveAnswer_IsMappedToTheUtcInstant()
    {
        var client = CreateClient(new PagedStubHandler(_ => LiveAddedAtFixture().ToJsonString()));

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        Assert.Equal("Halloween Set", result.Preview!.Name);
        Assert.Equal(5, result.Preview.Items.Count);
        // "2024-10-01T18:48:45.244+00:00"
        var first = result.Preview.Items[0];
        Assert.Equal("hellowo", first.Alias);
        Assert.Equal(new DateTime(2024, 10, 1, 18, 48, 45, 244, DateTimeKind.Utc), first.AddedAt);
        Assert.Equal(DateTimeKind.Utc, first.AddedAt!.Value.Kind);
        Assert.Equal(new DateTime(2024, 10, 1, 19, 4, 46, 162, DateTimeKind.Utc), result.Preview.Items[4].AddedAt);
        Assert.All(result.Preview.Items, item => Assert.NotNull(item.AddedAt));
    }

    /// <summary>
    /// CONSTRUCTED, not recorded: the live sample showed no item with <c>added_at: null</c> and none
    /// without the field. Both are still mapped to <c>null</c> ("7TV reported none", no gate), which
    /// the spec requires, so they are derived here from the recorded fixture.
    /// </summary>
    [Fact]
    public async Task AddedAt_NullAndAbsentField_AreMappedToNull_ConstructedFromTheRecordedAnswer()
    {
        var payload = LiveAddedAtFixture();
        var items = payload["data"]!["emote_sets"]!["emote_set"]!["emotes"]!["items"]!.AsArray();
        items[1]!["added_at"] = null;
        items[2]!.AsObject().Remove("added_at");
        var client = CreateClient(new PagedStubHandler(_ => payload.ToJsonString()));

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.NotNull(result.Preview!.Items[0].AddedAt);
        Assert.Null(result.Preview.Items[1].AddedAt);
        Assert.Null(result.Preview.Items[2].AddedAt);
        Assert.NotNull(result.Preview.Items[3].AddedAt);
    }

    [Fact]
    public async Task AddedAt_WithAnOffsetTimestamp_IsNormalisedToUtc_AndImplausibleDatesAreNotRejected()
    {
        var payload = JsonNode.Parse(Page(totalCount: 2, pageCount: 1, ("e1", "A", "A", null, null, true), ("e2", "B", "B", null, null, true)))!;
        var items = payload["data"]!["emote_sets"]!["emote_set"]!["emotes"]!["items"]!;
        items[0]!["added_at"] = "2026-03-14T20:00:00+02:00";
        items[1]!["added_at"] = "1970-01-01T00:00:00Z";
        var client = CreateClient(new PagedStubHandler(_ => payload.ToJsonString()));

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(new DateTime(2026, 3, 14, 18, 0, 0, DateTimeKind.Utc), result.Preview!.Items[0].AddedAt);
        Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), result.Preview.Items[1].AddedAt);
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
    /// rate once the two resolution calls are counted too. Since #457 a clean multi-page read also
    /// re-reads pages 1..9 for verification, and each of those requests is charged too: 2p−1.
    /// </summary>
    [Fact]
    public async Task EveryPage_ChargesExactlyOneRequestPermit()
    {
        var handler = new PagedStubHandler(page => Page(totalCount: 10, pageCount: 10, ($"e{page}", $"Alias{page}", $"Default{page}", null, null, true)));
        var budget = new RecordingForeignUpstreamRequestBudget();
        var client = CreateClient(handler, budget);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        Assert.Equal(SevenTvSetReadState.Complete, result.Preview!.ReadState);
        Assert.Equal(19, handler.RequestedPages.Count);
        Assert.Equal(19, budget.Charges);
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

    // ---- #457: the three-way read state (complete | gap | inconsistent) ----

    /// <summary>
    /// AK 1 / E1 / E8c, the measured shape of set 01HCN0ZBX80009XR48886X5MXJ: one page, 7TV counts
    /// 496 and lists 494. protects: the gap verdict, and that a single page costs one request.
    /// fails_when: it reads inconsistent or complete, <c>truncated</c> is false, or a second request
    /// is sent. no_claim: 7TV's real answer (a stub stands in).
    /// </summary>
    [Fact]
    public async Task TheMeasuredShape_OnePage496Counted494Listed_IsAGap_WithTwoOmitted_InOneRequest()
    {
        var handler = new PagedStubHandler(_ => EntryPage(496, 1, Entries(494)));

        var result = await CreateClient(handler).GetEmoteSetPreviewAsync(SetId);

        AssertReadState(result, SevenTvSetReadState.Gap, 2);
        Assert.Equal(496, result.Preview!.TotalCount);
        Assert.Equal(494, result.Preview.Items.Count);
        Assert.Equal([1], handler.RequestedPages);
    }

    /// <summary>AK 2 / E2: an under-count that lists nothing at all is not a gap.</summary>
    [Fact]
    public async Task AnUnderCountListingNothing_IsInconsistent()
    {
        var result = await CreateClient(new PagedStubHandler(_ => EntryPage(5, 1))).GetEmoteSetPreviewAsync(SetId);

        AssertReadState(result, SevenTvSetReadState.Inconsistent, 0);
    }

    /// <summary>AK 2 / E3, a tightening: an over-count used to be <c>truncated: false</c>.</summary>
    [Fact]
    public async Task AnOverCount_IsInconsistent_AndTruncated()
    {
        var result = await CreateClient(new PagedStubHandler(_ => EntryPage(1, 1, Entries(2))))
            .GetEmoteSetPreviewAsync(SetId);

        AssertReadState(result, SevenTvSetReadState.Inconsistent, 0);
    }

    /// <summary>
    /// AK 2 / E4: the total changes between pages. The listed count matches the last page's total,
    /// so only the drift itself can make this fail closed — and no verification is spent on it.
    /// </summary>
    [Fact]
    public async Task ATotalThatDriftsBetweenPages_IsInconsistent_WithoutAVerificationRequest()
    {
        var handler = new PagedStubHandler(page => page == 1
            ? EntryPage(4, 2, ("a", "A", null), ("b", "B", null))
            : EntryPage(3, 2, ("c", "C", null)));

        var result = await CreateClient(handler).GetEmoteSetPreviewAsync(SetId);

        AssertReadState(result, SevenTvSetReadState.Inconsistent, 0);
        Assert.Equal([1, 2], handler.RequestedPages);
    }

    /// <summary>AK 2 / E5: the same (id, alias) on page 1 and page 2 is a pagination shift.</summary>
    [Fact]
    public async Task AnEntryRepeatedOnALaterPage_IsInconsistent()
    {
        var handler = new PagedStubHandler(page => page == 1
            ? EntryPage(3, 2, ("a", "A", null), ("b", "B", null))
            : EntryPage(3, 2, ("a", "A", null)));

        var result = await CreateClient(handler).GetEmoteSetPreviewAsync(SetId);

        AssertReadState(result, SevenTvSetReadState.Inconsistent, 0);
    }

    /// <summary>E6: one emote under two aliases on two pages is two entries, not a repeat (#74).</summary>
    [Fact]
    public async Task OneEmoteUnderTwoAliasesOnTwoPages_IsNotARepeat()
    {
        var handler = new PagedStubHandler(page => page == 1
            ? EntryPage(2, 2, ("e1", "A", null))
            : EntryPage(2, 2, ("e1", "B", null)));

        var result = await CreateClient(handler).GetEmoteSetPreviewAsync(SetId);

        AssertReadState(result, SevenTvSetReadState.Complete, 0);
    }

    /// <summary>AK 2 / E7: the page cap is inconsistent, keeps its log line, and is not verified.</summary>
    [Fact]
    public async Task ThePageCap_IsInconsistent_WithItsLogLine_AndNoVerification()
    {
        var handler = new PagedStubHandler(page => EntryPage(5000, 11, ($"e{page}", $"A{page}", null)));
        var logger = new RecordingLogger<SevenTvApiClient>();

        var result = await CreateClient(handler, logger: logger).GetEmoteSetPreviewAsync(SetId);

        AssertReadState(result, SevenTvSetReadState.Inconsistent, 0);
        Assert.Equal(Enumerable.Range(1, 10), handler.RequestedPages);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Seitendecke"));
    }

    /// <summary>AK 2 / E8: an empty set is complete (backfill keeps its own <c>SetEmpty</c>).</summary>
    [Fact]
    public async Task AnEmptySet_IsComplete()
    {
        var result = await CreateClient(new PagedStubHandler(_ => EntryPage(0, 1))).GetEmoteSetPreviewAsync(SetId);

        AssertReadState(result, SevenTvSetReadState.Complete, 0);
    }

    /// <summary>
    /// AK 3: a gap read writes exactly one English Information line — set id, total, listed and
    /// omitted counts — even across two pages and their verification; a complete read writes none.
    /// fails_when: the line is missing, written per page, or written for a complete read.
    /// </summary>
    [Fact]
    public async Task AGapRead_WritesExactlyOneInformationLine_ACompleteReadNone()
    {
        var gapLogger = new RecordingLogger<SevenTvApiClient>();
        var gapHandler = new PagedStubHandler(page => page == 1
            ? EntryPage(4, 2, ("a", "A", null), ("b", "B", null))
            : EntryPage(4, 2, ("c", "C", null)));
        var completeLogger = new RecordingLogger<SevenTvApiClient>();
        var completeHandler = new PagedStubHandler(page => page == 1
            ? EntryPage(3, 2, ("a", "A", null), ("b", "B", null))
            : EntryPage(3, 2, ("c", "C", null)));

        var gap = await CreateClient(gapHandler, logger: gapLogger).GetEmoteSetPreviewAsync(SetId);
        var complete = await CreateClient(completeHandler, logger: completeLogger).GetEmoteSetPreviewAsync(SetId);

        AssertReadState(gap, SevenTvSetReadState.Gap, 1);
        AssertReadState(complete, SevenTvSetReadState.Complete, 0);
        var line = Assert.Single(gapLogger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.Equal(
            $"7TV lists fewer entries than it counts for emote set {SetId}: totalCount 4, listed 3, omitted 1 (gap read).",
            line.Message);
        Assert.DoesNotContain(completeLogger.Entries, entry => entry.Level == LogLevel.Information);
    }

    // ---- #457: the verification re-read of multi-page reads (AK 2a) ----

    /// <summary>
    /// AK 2a / E8a, Codex Sol's boundary-shift counterexample: between reading page 1 and page 2 an
    /// early entry (a) is removed and another (d) appended. The total stays 3, nothing repeats, three
    /// entries are collected — and c, which slid onto page 1, was never seen. Only the re-read of
    /// page 1 shows it. protects: no unverified multi-page complete. fails_when: this reads complete.
    /// no_claim: a shift contained between a page's first read and its re-read (#454's limit).
    /// </summary>
    [Fact]
    public async Task TheBoundaryShiftCounterexample_IsInconsistent_NotComplete()
    {
        var handler = ScriptedHandler((page, call) => (page, call) switch
        {
            (1, 1) => EntryPage(3, 2, ("a", "A", null), ("b", "B", null)),
            (2, _) => EntryPage(3, 2, ("d", "D", null)),
            _ => EntryPage(3, 2, ("b", "B", null), ("c", "C", null)),
        });

        var result = await CreateClient(handler).GetEmoteSetPreviewAsync(SetId);

        AssertReadState(result, SevenTvSetReadState.Inconsistent, 0);
        Assert.Equal([1, 2, 1], handler.RequestedPages);
    }

    /// <summary>
    /// The member key carries the entry's date (Plan-378 S4, as in #454): two entries of one emote
    /// that swap aliases between the two reads leave the (id, alias) set equal, but not the dates.
    /// </summary>
    [Fact]
    public async Task AnAliasSwapBetweenTwoEntriesOfOneEmote_IsCaughtByTheEntryDates()
    {
        const string Early = "2026-01-01T10:00:00.123+00:00";
        const string Late = "2026-02-01T10:00:00.456+00:00";
        var handler = ScriptedHandler((page, call) => (page, call) switch
        {
            (1, 1) => EntryPage(3, 2, ("e1", "A", Early), ("e1", "B", Late)),
            (2, _) => EntryPage(3, 2, ("e2", "C", Early)),
            _ => EntryPage(3, 2, ("e1", "A", Late), ("e1", "B", Early)),
        });

        var result = await CreateClient(handler).GetEmoteSetPreviewAsync(SetId);

        AssertReadState(result, SevenTvSetReadState.Inconsistent, 0);
    }

    /// <summary>
    /// AK 2a: a clean two-page read costs exactly three requests (2 + 1 re-read) and is complete;
    /// a clean two-page under-count likewise, and is a gap. The re-read may list page 1 in another
    /// order — membership, not order, is compared.
    /// </summary>
    [Theory]
    [InlineData(3, SevenTvSetReadState.Complete, 0)]
    [InlineData(5, SevenTvSetReadState.Gap, 2)]
    public async Task ACleanTwoPageRead_IsVerifiedInExactlyThreeRequests(int totalCount, string readState, int omitted)
    {
        var handler = ScriptedHandler((page, call) => (page, call) switch
        {
            (1, 1) => EntryPage(totalCount, 2, ("a", "A", "2026-01-01T10:00:00Z"), ("b", "B", null)),
            (1, _) => EntryPage(totalCount, 2, ("b", "B", null), ("a", "A", "2026-01-01T10:00:00Z")),
            _ => EntryPage(totalCount, 2, ("c", "C", null)),
        });

        var result = await CreateClient(handler).GetEmoteSetPreviewAsync(SetId);

        AssertReadState(result, readState, omitted);
        Assert.Equal([1, 2, 1], handler.RequestedPages);
    }

    /// <summary>A total that only the re-read sees change is drift too.</summary>
    [Fact]
    public async Task ADriftOnlyTheReReadSees_IsInconsistent()
    {
        var handler = ScriptedHandler((page, call) => (page, call) switch
        {
            (1, 1) => EntryPage(3, 2, ("a", "A", null), ("b", "B", null)),
            (1, _) => EntryPage(4, 2, ("a", "A", null), ("b", "B", null)),
            _ => EntryPage(3, 2, ("c", "C", null)),
        });

        var result = await CreateClient(handler).GetEmoteSetPreviewAsync(SetId);

        AssertReadState(result, SevenTvSetReadState.Inconsistent, 0);
    }

    /// <summary>
    /// AK 2a / E8b: a failing re-read fails the whole preview with its status, exactly like a failing
    /// first-pass page — a half-verified read is never reported as complete or gap.
    /// </summary>
    [Theory]
    [InlineData(true, SevenTvPreviewLookupStatus.RateLimited)]
    [InlineData(false, SevenTvPreviewLookupStatus.Unavailable)]
    public async Task AFailingReRead_FailsThePreview_WithThatStatus(bool rateLimited, SevenTvPreviewLookupStatus expected)
    {
        var failure = rateLimited
            ? RateLimitedPayload(resetHintSeconds: null)
            : """{"data":null,"errors":[{"message":"boom"}]}""";
        var handler = ScriptedHandler((page, call) => (page, call) switch
        {
            (1, 1) => EntryPage(3, 2, ("a", "A", null), ("b", "B", null)),
            (1, _) => failure,
            _ => EntryPage(3, 2, ("c", "C", null)),
        });

        var result = await CreateClient(handler).GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Preview);
        Assert.Equal([1, 2, 1], handler.RequestedPages);
    }

    /// <summary>
    /// AK 2a / E8d and the charging contract of AK 2b: first-pass pages are charged with the preview
    /// reserve and the budget's default wait, the re-read with the reserve and no wait. A refused
    /// re-read permit sends nothing and makes the read inconsistent, not failed, with one log line.
    /// fails_when: a refused permit yields complete or gap, a request is sent without a permit, or a
    /// re-read waits.
    /// </summary>
    [Fact]
    public async Task ARefusedVerificationPermit_IsInconsistent_WithoutWaitingOrSending()
    {
        var handler = new PagedStubHandler(page => page == 1
            ? EntryPage(3, 2, ("a", "A", null), ("b", "B", null))
            : EntryPage(3, 2, ("c", "C", null)));
        var budget = new RecordingForeignUpstreamRequestBudget(grantCount: 2);
        var logger = new RecordingLogger<SevenTvApiClient>();

        var result = await CreateClient(handler, budget, logger).GetEmoteSetPreviewAsync(SetId);

        AssertReadState(result, SevenTvSetReadState.Inconsistent, 0);
        Assert.Equal([1, 2], handler.RequestedPages);
        Assert.Equal(
            [
                (ForeignEmoteSetProviderBudget.PreviewReserve, (TimeSpan?)ForeignEmoteSetProviderBudget.DefaultRequestWaitTimeout),
                (ForeignEmoteSetProviderBudget.PreviewReserve, ForeignEmoteSetProviderBudget.DefaultRequestWaitTimeout),
                (ForeignEmoteSetProviderBudget.PreviewReserve, TimeSpan.Zero),
            ],
            budget.Requests);
        Assert.Single(logger.Entries, entry => entry.Message.Contains("verification re-read", StringComparison.Ordinal));
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
        HttpMessageHandler handler,
        IForeignUpstreamRequestBudget? requestBudget = null,
        RecordingLogger<SevenTvApiClient>? logger = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") };
        return new SevenTvApiClient(
            httpClient,
            new RecordingRateLimitTelemetry(),
            requestBudget ?? new RecordingForeignUpstreamRequestBudget(),
            new RecordingSevenTvSearchBudget(),
            logger ?? new RecordingLogger<SevenTvApiClient>());
    }

    // A page of set entries as the read-state tests need them: id, alias and the entry's date
    // (null = field absent), nothing else that matters to the classification.
    private static string EntryPage(int totalCount, int pageCount, params (string Id, string Alias, string? AddedAt)[] items)
    {
        var itemsArray = new JsonArray();
        foreach (var item in items)
        {
            var entry = new JsonObject
            {
                ["alias"] = item.Alias,
                ["emote"] = new JsonObject { ["id"] = item.Id, ["default_name"] = item.Id },
            };
            if (item.AddedAt is { } addedAt)
            {
                entry["added_at"] = addedAt;
            }

            itemsArray.Add(entry);
        }

        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["emote_sets"] = new JsonObject
                {
                    ["emote_set"] = new JsonObject
                    {
                        ["name"] = "Set",
                        ["capacity"] = 1000,
                        ["emotes"] = new JsonObject
                        {
                            ["total_count"] = totalCount,
                            ["page_count"] = pageCount,
                            ["items"] = itemsArray,
                        },
                    },
                },
            },
        }.ToJsonString();
    }

    private static (string Id, string Alias, string? AddedAt)[] Entries(int count, string prefix = "e") =>
        [.. Enumerable.Range(1, count).Select(i => ($"{prefix}{i}", $"Alias{prefix}{i}", (string?)null))];

    // Answers each page with the function of (page, how often that page has been asked for so far,
    // 1-based) — the first pass is call 1, the verification re-read call 2.
    private static PagedStubHandler ScriptedHandler(Func<int, int, string> responseForPageAndCall)
    {
        var calls = new Dictionary<int, int>();
        return new PagedStubHandler(page =>
        {
            calls[page] = calls.GetValueOrDefault(page) + 1;
            return responseForPageAndCall(page, calls[page]);
        });
    }

    private static void AssertReadState(SevenTvEmoteSetPreviewResult result, string readState, int omittedCount)
    {
        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        Assert.Equal(readState, result.Preview!.ReadState);
        Assert.Equal(omittedCount, result.Preview.OmittedCount);
        Assert.Equal(readState != SevenTvSetReadState.Complete, result.Preview.Truncated);
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
