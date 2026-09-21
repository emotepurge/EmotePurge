using System.Net;
using System.Net.Http.Headers;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="SevenTvApiClient.LookUpEditorGrantsAsync"/> — the budgeted identity-then-
/// <c>editor_of</c> read of the set-centric import's owner check (spec 2026-09-20, section 32,
/// second review round): one permit per request, and the failures kept apart that the unbudgeted
/// pair folds into one.
/// </summary>
public class SevenTvApiClientEditorGrantsLookupTests
{
    private const string TwitchUserId = "100";

    private const string IdentityAnswer =
        """{"data":{"user_by_connection":{"id":"01ACTOR","connections":[{"platform":"TWITCH","id":"100","emote_set_id":null}]}}}""";

    private const string EditorOfAnswer =
        """{"data":{"user":{"editor_of":[{"user":{"connections":[{"platform":"TWITCH","id":"200","username":"EditedChannel"}]}}]}}}""";

    [Fact]
    public async Task BothAnswers_AreOk_AtOnePermitPerRequest()
    {
        var handler = new SevenTvGqlRouteHandler()
            .Answer(SevenTvGqlRouteHandler.Identity, HttpStatusCode.OK, IdentityAnswer)
            .Answer(SevenTvGqlRouteHandler.EditorOf, HttpStatusCode.OK, EditorOfAnswer);
        var budget = new RecordingForeignUpstreamRequestBudget();

        var result = await CreateClient(handler, budget).LookUpEditorGrantsAsync(TwitchUserId);

        Assert.Equal(SevenTvEditorGrantsLookupStatus.Ok, result.Status);
        Assert.Equal(new SevenTvEditorGrant("EditedChannel", "200"), Assert.Single(result.Grants!));
        Assert.Equal(2, handler.Requests);
        Assert.Equal(2, budget.Charges);
    }

    [Fact]
    public async Task TheFirstPermitRefused_IsBudgetExhausted_AndSendsNothing()
    {
        var handler = new SevenTvGqlRouteHandler().Answer(SevenTvGqlRouteHandler.Identity, HttpStatusCode.OK, IdentityAnswer);

        var result = await CreateClient(handler, new RecordingForeignUpstreamRequestBudget(grantCount: 0))
            .LookUpEditorGrantsAsync(TwitchUserId);

        Assert.Equal(SevenTvEditorGrantsLookupStatus.BudgetExhausted, result.Status);
        Assert.Equal(0, handler.Requests);
    }

    /// <summary>7TV's placeholder user: an answer, and no second request is made for it.</summary>
    [Fact]
    public async Task ThePlaceholderUser_IsNoSevenTvAccount_AfterOneRequest()
    {
        var handler = new SevenTvGqlRouteHandler().Answer(
            SevenTvGqlRouteHandler.Identity,
            HttpStatusCode.OK,
            """{"data":{"user_by_connection":{"id":"00000000000000000000000000","connections":[]}}}""");

        var result = await CreateClient(handler).LookUpEditorGrantsAsync(TwitchUserId);

        Assert.Equal(SevenTvEditorGrantsLookupStatus.NoSevenTvAccount, result.Status);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task AnHttp429_IsRateLimited_WithItsRetryAfter()
    {
        var handler = new SevenTvGqlRouteHandler().Answer(SevenTvGqlRouteHandler.Identity, () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(string.Empty) };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        });

        var result = await CreateClient(handler).LookUpEditorGrantsAsync(TwitchUserId);

        Assert.Equal(SevenTvEditorGrantsLookupStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(90), result.RetryAfter);
    }

    /// <summary>Both requests read the disguised 429 before anything else in the body.</summary>
    [Theory]
    [InlineData(SevenTvGqlRouteHandler.Identity)]
    [InlineData(SevenTvGqlRouteHandler.EditorOf)]
    public async Task ADisguised429_IsRateLimited_OnEitherRequest(string kind)
    {
        const string Disguised = """{"data":null,"errors":[{"message":"slow down","extensions":{"status":429}}]}""";
        var handler = new SevenTvGqlRouteHandler()
            .Answer(SevenTvGqlRouteHandler.Identity, HttpStatusCode.OK, IdentityAnswer)
            .Answer(kind, HttpStatusCode.OK, Disguised);

        var result = await CreateClient(handler).LookUpEditorGrantsAsync(TwitchUserId);

        Assert.Equal(SevenTvEditorGrantsLookupStatus.RateLimited, result.Status);
    }

    /// <summary>An errors-only answer is a failed lookup on either request — never "edits nothing".</summary>
    [Theory]
    [InlineData(SevenTvGqlRouteHandler.Identity)]
    [InlineData(SevenTvGqlRouteHandler.EditorOf)]
    public async Task AnErrorsOnlyAnswer_IsUnavailable_OnEitherRequest(string kind)
    {
        var handler = new SevenTvGqlRouteHandler()
            .Answer(SevenTvGqlRouteHandler.Identity, HttpStatusCode.OK, IdentityAnswer)
            .Answer(SevenTvGqlRouteHandler.EditorOf, HttpStatusCode.OK, EditorOfAnswer)
            .Answer(kind, HttpStatusCode.OK, """{"data":null,"errors":[{"message":"boom"}]}""");

        var result = await CreateClient(handler).LookUpEditorGrantsAsync(TwitchUserId);

        Assert.Equal(SevenTvEditorGrantsLookupStatus.Unavailable, result.Status);
    }

    private static SevenTvApiClient CreateClient(HttpMessageHandler handler, IForeignUpstreamRequestBudget? budget = null) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") },
        new RecordingRateLimitTelemetry(),
        budget ?? new RecordingForeignUpstreamRequestBudget(),
        new RecordingLogger<SevenTvApiClient>());
}
