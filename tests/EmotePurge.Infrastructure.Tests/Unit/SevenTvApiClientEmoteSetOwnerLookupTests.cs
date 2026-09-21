using System.Net;
using System.Text;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="SevenTvApiClient.LookUpEmoteSetOwnerAsync"/> — the budgeted owner lookup the
/// set-centric import's owner check falls back to (spec 2026-09-20, section 32). Unlike
/// <see cref="SevenTvApiClient.GetEmoteSetOwnerIdAsync"/>, which folds every failure into
/// <c>null</c>, it has to keep "7TV knows no such set" (404) apart from "7TV did not answer" (503),
/// and it must never reach 7TV without a permit.
/// </summary>
public class SevenTvApiClientEmoteSetOwnerLookupTests
{
    private const string EmoteSetId = "01GV88A38G0006FW5TVZVMG507";
    private const string OwnerId = "01GQA28FCR0002Q9KS8SKQKVXX";

    [Fact]
    public async Task AnOwnerInTheAnswer_IsOk_AndCostsOnePermit()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OwnerAnswer(OwnerId));
        var budget = new RecordingForeignUpstreamRequestBudget();

        var result = await CreateClient(handler, budget).LookUpEmoteSetOwnerAsync(EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnerLookupStatus.Ok, result.Status);
        Assert.Equal(OwnerId, result.OwnerSevenTvUserId);
        Assert.Equal(1, budget.Charges);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task ARefusedPermit_IsBudgetExhausted_AndSendsNothing()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OwnerAnswer(OwnerId));

        var result = await CreateClient(handler, new RecordingForeignUpstreamRequestBudget(grantCount: 0))
            .LookUpEmoteSetOwnerAsync(EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnerLookupStatus.BudgetExhausted, result.Status);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task AReadableAnswerWithoutAnOwner_IsNotFound()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK, """{"data":{"emote_set":null},"errors":[{"message":"Unknown Emote Set"}]}""");

        var result = await CreateClient(handler).LookUpEmoteSetOwnerAsync(EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnerLookupStatus.NotFound, result.Status);
        Assert.Null(result.OwnerSevenTvUserId);
    }

    /// <summary>
    /// Only the readable <c>data.emote_set: null</c> above means "unknown set". An answer that
    /// carries a non-429 GraphQL error and no <c>emote_set</c> at all — <c>data: null</c>, or a
    /// <c>data</c> object without the field — is 7TV failing, not 7TV saying no (second review round
    /// on K2, P2): read as <c>NotFound</c>, it answered a legitimate report with 404.
    /// </summary>
    [Theory]
    [InlineData("""{"data":null,"errors":[{"message":"internal server error"}]}""")]
    [InlineData("""{"data":{},"errors":[{"message":"internal server error"}]}""")]
    [InlineData("""{"errors":[{"message":"internal server error"}]}""")]
    public async Task AnErrorsOnlyAnswer_IsUnavailable_NotNotFound(string body)
    {
        var result = await CreateClient(new StubHandler(HttpStatusCode.OK, body)).LookUpEmoteSetOwnerAsync(EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnerLookupStatus.Unavailable, result.Status);
    }

    /// <summary>
    /// Both disguises of an overload are <c>RateLimited</c>, never <c>NotFound</c>: an overload read
    /// as "no such set" would answer a legitimate report with 404.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "")]
    [InlineData(HttpStatusCode.OK, """{"data":null,"errors":[{"message":"too many requests","extensions":{"code":"RATE_LIMITED","status":429}}]}""")]
    public async Task AnOverload_IsRateLimited(HttpStatusCode statusCode, string body)
    {
        var result = await CreateClient(new StubHandler(statusCode, body)).LookUpEmoteSetOwnerAsync(EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnerLookupStatus.RateLimited, result.Status);
    }

    [Fact]
    public async Task AServerError_IsUnavailable_NotNotFound()
    {
        var result = await CreateClient(new StubHandler(HttpStatusCode.BadGateway, "")).LookUpEmoteSetOwnerAsync(EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnerLookupStatus.Unavailable, result.Status);
    }

    private static string OwnerAnswer(string ownerId) =>
        "{\"data\":{\"emote_set\":{\"owner_id\":\"" + ownerId + "\"}}}";

    private static SevenTvApiClient CreateClient(HttpMessageHandler handler, IForeignUpstreamRequestBudget? budget = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") };
        return new SevenTvApiClient(
            httpClient,
            new RecordingRateLimitTelemetry(),
            budget ?? new RecordingForeignUpstreamRequestBudget(),
            new RecordingLogger<SevenTvApiClient>());
    }

    private sealed class StubHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
