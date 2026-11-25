using System.Net;
using System.Reflection;
using System.Text;
using EmotePurge.Api.Endpoints;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The HTTP half of the bot token's trust boundary for the channel routes of the wish bot mode (plan #487 T11b, spec
/// AK74/AK75): <c>GET …/wishes/bot</c>, <c>activate</c>, <c>DELETE …/wishes/bot</c>, <c>bot-remove</c>, <c>recheck</c>
/// and <c>/permissions</c>. Every answer (status line, headers, body) and every line the Api's loggers write at trace
/// level in every category is searched for a token handed to the route in its body, its payload and signature segments
/// and <c>Bearer</c>; once for every <see cref="WishOutcome"/> a command can answer with (the 401, 403, 404, 409 and 503
/// forms included), once with the service throwing (the exception middleware's answer and line), and once per route
/// without a session or without the manager right. The response types themselves may not even have a member that could
/// carry token material. The services are substituted (container-free suite); what the real services write below the
/// routes is <c>WishBotPassTests.TokenBoundary</c> in Infrastructure.Tests.
/// </summary>
public class WishBotRoutesTokenBoundaryTests : IClassFixture<ApiFactory>
{
    private const string Channel = "handofblood";
    private const string Manager = "manager-1";
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJleHAiOjQxMDI0NDQ4MDAsInN1YiI6Indpc2gtYm90LXJvdXRlLXByb2JlIn0.d2lzaC1ib3Qtcm91dGUtc2lnbmF0dXJl";

    private static readonly (string Method, string Path)[] Routes =
    [
        ("GET", $"/api/channels/{Channel}/wishes/bot"),
        ("POST", $"/api/channels/{Channel}/wishes/bot/activate"),
        ("DELETE", $"/api/channels/{Channel}/wishes/bot"),
        ("POST", $"/api/channels/{Channel}/wishes/7/bot-remove"),
        ("POST", $"/api/channels/{Channel}/wishes/7/recheck"),
        ("GET", $"/api/channels/{Channel}/permissions"),
    ];

    private readonly ApiFactory _factory;

    public WishBotRoutesTokenBoundaryTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.WishBot.ClearSubstitute(ClearOptions.All);
        _factory.Wishes.ClearSubstitute(ClearOptions.All);
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(true);
        _factory.ChannelAccess.IsGlobalAdminById(Arg.Any<TwitchPrincipalInfo>()).Returns(true);
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.WishBot.CheckActivationAccessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WishBotActivationAccess.Allowed);
        _factory.WishBot.IsModeAvailableAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(true);
        _factory.WishBot.GetChannelAsync(Arg.Any<string>(), Arg.Any<WishBotCaller>(), Arg.Any<CancellationToken>())
            .Returns(new WishBotChannelDto(
                true, true, true, false, null, null, true, true, null, new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc), "someadmin",
                new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc), "01M7XAHYKT63HSZMS71AW0C5TG"));
    }

    public static TheoryData<WishOutcome> Outcomes => [.. Enum.GetValues<WishOutcome>()];

    // Every outcome a bot command can end in, through every route that maps it, with the token in the request body.
    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task EveryOutcome_OnEveryRoute_LeaksNothingIntoTheAnswerOrAnyLog(WishOutcome outcome)
    {
        using var logs = new TraceCapture();
        using var factory = Host(logs);
        var result = outcome == WishOutcome.Success ? new WishCommandResult(outcome, Wish: Wish()) : WishCommandResult.Of(outcome);
        _factory.WishBot.ActivateAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>()).Returns(result);
        _factory.WishBot.DeactivateAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(result);
        _factory.Wishes.RequestBotRemovalAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(result);
        _factory.Wishes.RecheckAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(result);

        foreach (var (method, path) in Routes)
        {
            var answer = await SendAsync(factory, method, path, Body());

            Assert.True((int)answer.StatusCode < 600, $"{method} {path} answered {(int)answer.StatusCode}.");
            Assert.NotEqual(HttpStatusCode.InternalServerError, answer.StatusCode);
            AssertClean(answer, Watch());
        }

        AssertClean(logs, Watch());
    }

    // The service fails on every route: the exception middleware answers the language-neutral code and its line names
    // neither the token of the body nor anything of the failure's inner exception beyond what the test put there.
    [Fact]
    public async Task EveryRoute_WhenTheServiceThrows_AnswersTheGenericCode_AndLogsNoToken()
    {
        using var logs = new TraceCapture();
        using var factory = Host(logs);
        var failure = new InvalidOperationException("forced failure in the service", new TimeoutException("inner failure"));
        _factory.WishBot.GetChannelAsync(Arg.Any<string>(), Arg.Any<WishBotCaller>(), Arg.Any<CancellationToken>()).Returns<Task<WishBotChannelDto?>>(_ => throw failure);
        _factory.WishBot.ActivateAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>()).Returns<Task<WishCommandResult>>(_ => throw failure);
        _factory.WishBot.DeactivateAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns<Task<WishCommandResult>>(_ => throw failure);
        _factory.WishBot.IsModeAvailableAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns<Task<bool>>(_ => throw failure);
        _factory.Wishes.RequestBotRemovalAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns<Task<WishCommandResult>>(_ => throw failure);
        _factory.Wishes.RecheckAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns<Task<WishCommandResult>>(_ => throw failure);

        foreach (var (method, path) in Routes)
        {
            var answer = await SendAsync(factory, method, path, Body());

            Assert.Equal(HttpStatusCode.InternalServerError, answer.StatusCode);
            Assert.Contains("unexpected_error", answer.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("forced failure", answer.Everything, StringComparison.Ordinal);
            AssertClean(answer, Watch());
        }

        AssertClean(logs, Watch());
        Assert.Contains(logs.Lines, line => line.Exception is not null && line.Exception.Contains("forced failure in the service", StringComparison.Ordinal));
    }

    // The session's absence, a non-manager and a malformed channel name answer before any handler.
    [Theory]
    [InlineData(null, true, HttpStatusCode.Unauthorized)]
    [InlineData(Manager, false, HttpStatusCode.Forbidden)]
    public async Task EveryRoute_WithoutASessionOrTheManagerRight_LeaksNothing(string? userId, bool manages, HttpStatusCode expected)
    {
        using var logs = new TraceCapture();
        using var factory = Host(logs);
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(manages);

        foreach (var (method, path) in Routes.Where(r => !r.Path.EndsWith("/permissions", StringComparison.Ordinal)))
        {
            var answer = await SendAsync(factory, method, path, Body(), userId);

            Assert.Equal(expected, answer.StatusCode);
            AssertClean(answer, Watch());
        }

        // The name filter answers first with the token's first segments as the channel name: not echoed either.
        var badName = await SendAsync(factory, "GET", $"/api/channels/{Token[..40]}/wishes/bot", Body(), userId);
        Assert.True(badName.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized, $"Unexpected {(int)badName.StatusCode}.");
        Assert.DoesNotContain(Token[..40], badName.Everything, StringComparison.Ordinal);

        AssertClean(logs, Watch());
    }

    // The restricted filters of the two commands that sit behind their own: the answer is a code, never a value.
    [Fact]
    public async Task TheCommandFilters_Refusals_LeakNothing()
    {
        using var logs = new TraceCapture();
        using var factory = Host(logs);
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(false);
        _factory.ChannelAccess.IsGlobalAdminById(Arg.Any<TwitchPrincipalInfo>()).Returns(false);

        foreach (var access in new[] { WishBotActivationAccess.NotListed, WishBotActivationAccess.Forbidden, WishBotActivationAccess.EditorUnavailable })
        {
            _factory.WishBot.CheckActivationAccessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(access);

            AssertClean(await SendAsync(factory, "POST", Routes[1].Path, Body()), Watch());
        }

        AssertClean(await SendAsync(factory, "DELETE", Routes[2].Path, Body()), Watch());
        AssertClean(logs, Watch());
    }

    // The types the routes answer with have no member that could carry a token, a seal or a credential, so the answers
    // above cannot start to hold one without this test noticing the new member first.
    [Fact]
    public void TheResponseTypes_HaveNoMemberForTokenMaterial()
    {
        Type[] types = [typeof(WishBotChannelDto), typeof(WishDto), typeof(WishSettingsDto), typeof(ChannelPermissionsDto)];
        string[] forbidden = ["token", "seal", "jwt", "secret", "credential", "bearer", "password", "authorization"];

        foreach (var type in types)
        {
            foreach (var member in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.DoesNotContain(
                    forbidden, word => member.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private static string Body() => $$"""{"token":"{{Token}}","enabled":true,"Authorization":"Bearer {{Token}}"}""";

    // The whole token, its payload and signature segments and the header word.
    private static string[] Watch()
    {
        var parts = Token.Split('.');
        return ["Bearer", Token, parts[1], parts[2]];
    }

    private static void AssertClean(Answer answer, string[] watch)
    {
        foreach (var value in watch)
        {
            Assert.False(
                answer.Everything.Contains(value, StringComparison.OrdinalIgnoreCase),
                $"The answer ({(int)answer.StatusCode}) contains a forbidden value of {value.Length} characters.");
        }
    }

    private static void AssertClean(TraceCapture logs, string[] watch)
    {
        Assert.NotEmpty(logs.Lines);
        Assert.Contains(logs.Lines, line => line.Level < LogLevel.Information);
        foreach (var line in logs.Lines)
        {
            foreach (var value in watch)
            {
                Assert.False(
                    line.AllText.Contains(value, StringComparison.OrdinalIgnoreCase),
                    $"A {line.Level} line of {line.Category} contains a forbidden value of {value.Length} characters.");
            }
        }
    }

    private WebApplicationFactory<Program> Host(TraceCapture logs) =>
        _factory.WithBotAccountEnabled().WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddFilter<TraceCapture>(null, LogLevel.Trace);
            logging.AddProvider(logs);
        }));

    private static async Task<Answer> SendAsync(WebApplicationFactory<Program> factory, string method, string path, string body, string? userId = Manager)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (userId is not null)
        {
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        }

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var headers = string.Join('\n', response.Headers.Concat(response.Content.Headers).Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));
        return new Answer(response.StatusCode, text, $"{(int)response.StatusCode} {response.ReasonPhrase}\n{headers}\n{text}");
    }

    private sealed record Answer(HttpStatusCode StatusCode, string Body, string Everything);

    private static WishDto Wish() => new(
        Id: 7,
        SevenTvEmoteId: "01H0000000000000000000000A",
        EmoteName: "Kappa",
        Alias: "Kappa",
        Source: WishSource.Manual,
        Status: WishStatus.Presented,
        StatusReason: WishStatusReason.Protected,
        RequesterLogin: null,
        DurationDays: 30,
        CreatedAtUtc: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc),
        ApprovedAtUtc: new DateTime(2026, 11, 1, 11, 0, 0, DateTimeKind.Utc),
        StartsAtUtc: new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc),
        StartsAtAuthoritative: true,
        ProbationStartsAtUtc: null,
        ExpiresAtUtc: new DateTime(2026, 12, 2, 10, 0, 0, DateTimeKind.Utc),
        EndedAtUtc: null,
        SlotId: "slot-1",
        SlotAddedAtUtc: null,
        ImageUrl: null,
        LinkOrigin: WishLinkOrigin.Bot,
        ExtensionCount: 0,
        Permanent: false,
        BotOwned: true,
        BotWritePending: false,
        BotNextAttemptAtUtc: null,
        RemovalQueuedAtUtc: new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc),
        Mode: "bot");
}
