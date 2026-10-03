using System.Net;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The paging query parameters are optional on every list route: a missing value is corrected to the
/// default and an unparsable one is corrected the same way, so binding can never fail — not with a
/// 500, and not with a 400 that would shadow the 403 for a caller who may not read the list.
/// </summary>
public class PagingBindingTests : IClassFixture<ApiFactory>
{
    private const string Channel = "testchannel";

    private readonly ApiFactory _factory;

    public PagingBindingTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ChannelAccess.ClearReceivedCalls();
        factory.AuditLogQuery.ClearReceivedCalls();
        factory.AdminUserQuery.ClearReceivedCalls();
    }

    public static TheoryData<string> AuditRoutes => new()
    {
        "/api/admin/audit-log",
        $"/api/channels/{Channel}/audit-log",
    };

    public static TheoryData<string> GuardedRoutes => new()
    {
        "/api/admin/audit-log",
        "/api/admin/users",
        $"/api/channels/{Channel}/audit-log",
    };

    public static TheoryData<string> ListRoutes => new()
    {
        "/api/admin/audit-log",
        "/api/admin/users",
        $"/api/channels/{Channel}/audit-log",
        $"/api/channels/{Channel}/vote-sessions",
        "/api/vote-sessions/mine",
    };

    [Theory]
    [MemberData(nameof(AuditRoutes))]
    public async Task AuditLog_AppliesTheDefaults_WhenPageAndPageSizeAreMissing(string route)
    {
        AllowEverything();

        var response = await SendAsync(route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.AuditLogQuery.Received(1)
            .ListAsync(1, 20, Arg.Any<AuditLogFilter?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(AuditRoutes))]
    public async Task AuditLog_AppliesTheDefault_WhenOnlyPageSizeIsMissing(string route)
    {
        AllowEverything();

        var response = await SendAsync($"{route}?page=3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.AuditLogQuery.Received(1)
            .ListAsync(3, 20, Arg.Any<AuditLogFilter?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdminUsers_AppliesTheDefaults_WhenPageAndPageSizeAreMissing()
    {
        AllowEverything();

        var response = await SendAsync("/api/admin/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.AdminUserQuery.Received(1).ListAsync(1, 20, Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(AuditRoutes))]
    public async Task AuditLog_CorrectsAPageThatIsNotANumber_InsteadOfFailingToBind(string route)
    {
        AllowEverything();

        var response = await SendAsync($"{route}?page=abc&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.AuditLogQuery.Received(1)
            .ListAsync(1, 10, Arg.Any<AuditLogFilter?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("1.5")]
    [InlineData("")]
    public async Task AuditLog_CorrectsAPageSizeThatIsNotAnInt32(string pageSize)
    {
        AllowEverything();

        var response = await SendAsync($"/api/admin/audit-log?page=2&pageSize={pageSize}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.AuditLogQuery.Received(1)
            .ListAsync(2, 20, Arg.Any<AuditLogFilter?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(GuardedRoutes))]
    public async Task GuardedRoutes_Answer403_NotABindingFailure_ForACallerWhoMayNotRead(string route)
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(false);
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync($"{route}?page=abc");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ListRoutes))]
    public async Task ListRoutes_Answer401_ForAnAnonymousCaller_WhateverThePaging(string route)
    {
        var missing = await SendAsync(route, userId: null);
        var invalid = await SendAsync($"{route}?page=abc&pageSize=xyz", userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
    }

    private void AllowEverything()
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(true);
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string? userId = "paging-user")
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (userId is not null)
        {
            // Unique per request: the rate limiter partitions by this claim.
            request.Headers.Add(TestAuthHandler.UserIdHeader, $"{userId}-{Guid.NewGuid():N}");
            request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        }

        return await client.SendAsync(request);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("errorCode").GetString();
    }
}
