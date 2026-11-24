using System.Net;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// <c>DELETE …/wishes/{id}?endedAtUtc=…</c> (wish emotes K7a, spec E38, E39, E41, E44, AK113): the access filter that
/// decides on the channel row's Twitch id, the query contract, the bodiless 204, and the HTTP half of the microsecond
/// round trip (the database half lives in the Infrastructure tests). The ladder every wish route shares (400 name, 401,
/// 403 non-manager, 404 switch) runs for this route in <see cref="WishEndpointsTests.Routes"/>.
/// </summary>
public class WishDeleteEndpointsTests : IClassFixture<ApiFactory>
{
    private const string Channel = "handofblood";
    private const string Manager = "manager-1";
    private const string EndedQuery = "2026-11-02T10:00:00.1234567Z";

    private static readonly WishCaptureAccess Confirmed = new("row-1", "1001", IdConfirmed: true);

    private readonly ApiFactory _factory;

    public WishDeleteEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Wishes.ClearReceivedCalls();
        _factory.ChannelAccess.ClearReceivedCalls();

        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(false);
        _factory.ChannelAccess.ResolveWishCaptureAccessAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Confirmed);
        _factory.Wishes.DeleteAsync(
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess>(), Arg.Any<CancellationToken>())
            .Returns(WishCommandResult.Of(WishOutcome.Success));
    }

    [Fact]
    public async Task Success_Answers204WithAnEmptyBody_AndPassesTheConfirmedAccessOn()
    {
        var response = await SendDeleteAsync(_factory, $"endedAtUtc={EndedQuery}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        await _factory.Wishes.Received(1).DeleteAsync(
            Channel, 7, Arg.Any<DateTime>(), Arg.Is<AuditActor>(a => a.TwitchUserId == Manager), Confirmed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BotMode_ConfirmedManagerWithoutActivation_Answers204()
    {
        // E39: no settings filter follows; deleting is no bot-mode decision (D7 concerns channel defaults only).
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendDeleteAsync(_factory, $"endedAtUtc={EndedQuery}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await _factory.Wishes.DidNotReceiveWithAnyArgs().CanChangeSettingsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task UnconfirmedId_Answers403_AndNeverCallsTheService()
    {
        _factory.ChannelAccess.ResolveWishCaptureAccessAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WishCaptureAccess("row-1", "1001", IdConfirmed: false));

        var response = await SendDeleteAsync(_factory, $"endedAtUtc={EndedQuery}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Wishes.ReceivedCalls());
    }

    [Fact]
    public async Task RowWithoutTwitchId_Answers409Unbound_AndNeverCallsTheService()
    {
        _factory.ChannelAccess.ResolveWishCaptureAccessAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WishCaptureAccess("row-1", null, IdConfirmed: false));

        var response = await SendDeleteAsync(_factory, $"endedAtUtc={EndedQuery}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishChannelUnbound, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.Wishes.ReceivedCalls());
    }

    [Fact]
    public async Task UnknownChannel_Answers404ChannelNotFound_AndNeverCallsTheService()
    {
        _factory.ChannelAccess.ResolveWishCaptureAccessAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WishCaptureAccess?)null);

        var response = await SendDeleteAsync(_factory, $"endedAtUtc={EndedQuery}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ChannelNotFound, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.Wishes.ReceivedCalls());
    }

    [Theory]
    [InlineData("")]
    [InlineData("endedAtUtc=")]
    [InlineData("endedAtUtc=not-a-time")]
    public async Task MissingEmptyOrUnreadableQuery_Answers400_AndNeverCallsTheService(string query)
    {
        var response = await SendDeleteAsync(_factory, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.Wishes.ReceivedCalls());
    }

    [Fact]
    public async Task SwitchOff_Answers404BeforeTheQueryIsRead()
    {
        using var disabled = _factory.WithWishesDisabled();

        var response = await SendDeleteAsync(disabled, "");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishesDisabled, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.Wishes.ReceivedCalls());
    }

    [Theory]
    [InlineData(WishOutcome.ChannelNotFound, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotFound)]
    [InlineData(WishOutcome.WishNotFound, HttpStatusCode.NotFound, ApiErrorCodes.WishNotFound)]
    [InlineData(WishOutcome.StateConflict, HttpStatusCode.Conflict, ApiErrorCodes.WishStateConflict)]
    public async Task ServiceFailures_MapThroughTheSharedMapping(WishOutcome outcome, HttpStatusCode status, string code)
    {
        _factory.Wishes.DeleteAsync(
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess>(), Arg.Any<CancellationToken>())
            .Returns(WishCommandResult.Of(outcome));

        var response = await SendDeleteAsync(_factory, $"endedAtUtc={EndedQuery}");

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task AccessDenied_FromTheService_Answers403WithoutABody()
    {
        _factory.Wishes.DeleteAsync(
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess>(), Arg.Any<CancellationToken>())
            .Returns(WishCommandResult.Of(WishOutcome.AccessDenied));

        var response = await SendDeleteAsync(_factory, $"endedAtUtc={EndedQuery}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    // The row "handofblood" carries Twitch id 1001; the caller moderates, per Twitch, a channel of the same login with
    // another BroadcasterId (the login was released and taken over). The manager check matches the login and lets them
    // in; the id question refuses: 403 and no service call. With the matching id the same caller deletes (204).
    [Theory]
    [InlineData("2002", HttpStatusCode.Forbidden)]
    [InlineData("1001", HttpStatusCode.NoContent)]
    public async Task SameLoginOtherTwitchId_IsRefused_WithTheRealAccessService(string listedBroadcasterId, HttpStatusCode expected)
    {
        var moderatorCheck = Substitute.For<IModeratorCheckService>();
        moderatorCheck.IsModeratorAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>()).Returns(true);
        var moderatedChannels = Substitute.For<IModeratedChannelsProvider>();
        moderatedChannels.GetModeratedChannelsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>())
            .Returns(new ModeratedChannelsLookup([new TwitchModeratedChannelInfo(Channel, listedBroadcasterId)], ReauthRequired: false));
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync(Channel, Arg.Any<CancellationToken>())
            .Returns(new Channel { Id = "row-1", ChannelName = Channel, TwitchChannelId = "1001" });

        using var real = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IChannelAccessService>(sp => new ChannelAccessService(
                moderatorCheck,
                moderatedChannels,
                Substitute.For<ISevenTvEditorService>(),
                channels,
                sp.GetRequiredService<IGlobalAdminAllowlist>(),
                NullLogger<ChannelAccessService>.Instance))));

        var response = await SendDeleteAsync(real, $"endedAtUtc={EndedQuery}", userId: "mod-of-new-owner");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Forbidden)
        {
            Assert.Empty(_factory.Wishes.ReceivedCalls());
        }
        else
        {
            await _factory.Wishes.Received(1).DeleteAsync(
                Channel, 7, Arg.Any<DateTime>(), Arg.Any<AuditActor>(), Arg.Is<WishCaptureAccess>(a => a.IdConfirmed), Arg.Any<CancellationToken>());
        }
    }

    // E44, HTTP half (AK109): the string the wish DTO serializes for a microsecond instant, sent back as the query,
    // reaches the service as the same ticks in UTC; one microsecond more is a different instant.
    [Fact]
    public async Task EndedAtUtc_RoundTripsThroughGetAndDelete_WithMicrosecondPrecision()
    {
        var endedAt = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);
        _factory.Wishes.GetAsync(Channel, 7, Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(new WishDetailDto(Wish(endedAt), usage: null));

        var get = await SendAsync(_factory, HttpMethod.Get, $"/api/channels/{Channel}/wishes/7");
        using var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var serialized = json.RootElement.GetProperty("endedAtUtc").GetString()!;
        Assert.Equal("2026-11-02T10:00:00.123456Z", serialized);

        var matching = await SendDeleteAsync(_factory, $"endedAtUtc={Uri.EscapeDataString(serialized)}");
        var oneMicrosecondLater = Uri.EscapeDataString(endedAt.AddTicks(10).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var mismatching = await SendDeleteAsync(_factory, $"endedAtUtc={oneMicrosecondLater}");

        Assert.Equal(HttpStatusCode.NoContent, matching.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, mismatching.StatusCode);
        var received = _factory.Wishes.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IWishService.DeleteAsync))
            .Select(c => (DateTime)c.GetArguments()[2]!)
            .ToList();
        Assert.Equal(2, received.Count);
        Assert.Equal(endedAt.Ticks, received[0].Ticks);
        Assert.Equal(DateTimeKind.Utc, received[0].Kind);
        Assert.Equal(endedAt.Ticks + 10, received[1].Ticks);
        Assert.NotEqual(received[0], received[1]);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("errorCode").GetString();

    private static Task<HttpResponseMessage> SendDeleteAsync(WebApplicationFactory<Program> factory, string query, string? userId = Manager) =>
        SendAsync(factory, HttpMethod.Delete, $"/api/channels/{Channel}/wishes/7" + (query.Length == 0 ? "" : "?" + query), userId);

    private static Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> factory, HttpMethod method, string path, string? userId = Manager)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(method, path);
        if (userId is not null)
        {
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        }

        return client.SendAsync(request);
    }

    private static WishDto Wish(DateTime endedAtUtc) => new(
        Id: 7,
        SevenTvEmoteId: "01H0000000000000000000000A",
        EmoteName: "Kappa",
        Alias: "Kappa",
        Source: WishSource.Manual,
        Status: WishStatus.Removed,
        StatusReason: null,
        RequesterLogin: "viewer",
        DurationDays: 30,
        CreatedAtUtc: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc),
        ApprovedAtUtc: null,
        StartsAtUtc: new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc),
        StartsAtAuthoritative: true,
        ProbationStartsAtUtc: null,
        ExpiresAtUtc: null,
        EndedAtUtc: endedAtUtc,
        SlotId: null,
        SlotAddedAtUtc: null,
        ImageUrl: null,
        LinkOrigin: WishLinkOrigin.Auto,
        ExtensionCount: 0,
        Permanent: false,
        BotOwned: false,
        BotWritePending: false,
        BotNextAttemptAtUtc: null,
        RemovalQueuedAtUtc: null,
        Mode: "manual");
}
