using EmotePurge.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EmotePurge.Api.Tests;

public class LogCaptureRegistrationTests
{
    [Fact]
    public void ApiHost_FirstOwnHostedService_IsLogCapture()
    {
        using var factory = new ApiFactory();
        _ = factory.Server;

        var first = factory.Services.GetServices<IHostedService>()
            .First(s => s.GetType().Namespace?.StartsWith("EmotePurge.", StringComparison.Ordinal) == true);

        // The factory runs with LogCapture:Enabled=false, so the heartbeat stands in for the writer.
        Assert.IsType<LogCaptureHeartbeat>(first);
    }
}
