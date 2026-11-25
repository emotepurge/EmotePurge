using EmotePurge.Api.Auth;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The refusals of <see cref="WishIdAccessFilter"/> that the real route pipeline never reaches, because the group's
/// own filters answer first (name validation, authorization): the filter must still fail closed when it runs alone,
/// as it is a security boundary of an irreversible command.
/// </summary>
public class WishIdAccessFilterTests
{
    [Fact]
    public async Task InvalidChannelName_Answers400_AndNeverCallsTheRoute()
    {
        var (result, nextCalled) = await Invoke(channelName: "not a channel!");

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task MissingChannelName_Answers400_AndNeverCallsTheRoute()
    {
        var (result, nextCalled) = await Invoke(channelName: null);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task NoTwitchPrincipal_Answers401_AndNeverCallsTheRoute()
    {
        var (result, nextCalled) = await Invoke(channelName: "somechannel");

        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(nextCalled);
    }

    private static async Task<(object? Result, bool NextCalled)> Invoke(string? channelName)
    {
        var services = new ServiceCollection()
            .AddSingleton(new WishOptions { Enabled = true })
            .AddSingleton(Substitute.For<IChannelAccessService>())
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        if (channelName is not null)
        {
            httpContext.Request.RouteValues["channelName"] = channelName;
        }

        var nextCalled = false;
        var result = await new WishIdAccessFilter().InvokeAsync(
            new DefaultEndpointFilterInvocationContext(httpContext),
            _ =>
            {
                nextCalled = true;
                return ValueTask.FromResult<object?>(Results.NoContent());
            });
        return (result, nextCalled);
    }
}
