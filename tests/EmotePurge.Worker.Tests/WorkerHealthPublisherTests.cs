using System.Text.Json;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Worker.SevenTv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Worker.Tests;

// K6 (plan #486 2.3, Sol-P1-2): the worker is the authority for the capture, and its health snapshot says what it does.
// Container-free: Redis is a substitute whose calls are read back.
public sealed class WorkerHealthPublisherTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task TheSnapshot_CarriesTheWorkersOwnCaptureSwitch(bool enabled, bool captureEnabled, bool expected)
    {
        var snapshot = await PublishOnceAsync(new WishOptions { Enabled = enabled, Capture = { Enabled = captureEnabled } });

        Assert.Equal(expected, snapshot.WishCaptureActive);
    }

    // An older worker's payload must read as "unknown", not as "off".
    [Fact]
    public void APayloadWithoutTheField_ReadsAsUnknown()
    {
        var older = JsonSerializer.Deserialize<WorkerHealthSnapshot>(
            """{"isConnected":true,"lastMessageReceivedUtc":null,"connectAttemptedUtc":null}""",
            JsonSerializerOptions.Web);

        Assert.NotNull(older);
        Assert.Null(older.WishCaptureActive);
    }

    private static async Task<WorkerHealthSnapshot> PublishOnceAsync(WishOptions wishOptions)
    {
        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        using var publisher = new WorkerHealthPublisher(
            NullLogger<WorkerHealthPublisher>.Instance,
            Substitute.For<ITwitchChatManager>(),
            Substitute.For<ISevenTvEventClient>(),
            new SevenTvSubscriptionRegistry(),
            new WorkerStats(),
            Substitute.For<IEmoteUsageCounter>(),
            new WorkerIdentity(),
            new ConfigurationBuilder().Build(),
            Substitute.For<IRedisPublisher>(),
            redis,
            wishOptions);

        await publisher.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!database.ReceivedCalls().Any(IsStringSet))
            {
                Assert.True(DateTime.UtcNow < deadline, "The publisher never wrote the snapshot.");
                await Task.Delay(5);
            }
        }
        finally
        {
            await publisher.StopAsync(CancellationToken.None);
        }

        var payload = (RedisValue)database.ReceivedCalls().First(IsStringSet).GetArguments()[1]!;
        return JsonSerializer.Deserialize<WorkerHealthSnapshot>(payload.ToString(), JsonSerializerOptions.Web)!;
    }

    private static bool IsStringSet(NSubstitute.Core.ICall call) => call.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync);
}
