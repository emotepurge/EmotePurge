using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Tests.Fakes;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class TwitchLiveStatusStoreFailureTests
{
    [Fact]
    public async Task ReadAsyncKeepsLoggingRedisFailuresForExistingCallers()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>())
            .Returns(_ => throw new RedisException("Redis is unavailable."));
        var logger = new RecordingLogger<TwitchLiveStatusStore>();
        var store = new TwitchLiveStatusStore(multiplexer, logger);

        var result = await store.ReadAsync();

        Assert.Null(result);
        Assert.Contains(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }
}
