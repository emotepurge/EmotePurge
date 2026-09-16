using EmotePurge.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The success-path counterpart to RedisReaderFailureModeTests, which covers only the two error
// branches (missing key, unreachable Redis). There is no Integration/ counterpart for this reader —
// unlike ModRoleCache and TwitchLiveStatusStore, it has never been exercised against a real Redis
// container — so this is the only place the deserialization itself is verified.
public class WorkerHealthReaderTests
{
    [Fact]
    public async Task ReadAsync_ValidSnapshotJson_DeserializesToTheExpectedSnapshot()
    {
        var connectAttempted = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var lastMessage = new DateTime(2026, 9, 1, 12, 5, 0, DateTimeKind.Utc);
        var json = $$"""
            {
                "isConnected": true,
                "lastMessageReceivedUtc": "{{lastMessage:O}}",
                "connectAttemptedUtc": "{{connectAttempted:O}}",
                "workerInstanceId": "worker-abc"
            }
            """;

        var database = Substitute.For<IDatabase>();
        database.StringGetAsync(Arg.Any<RedisKey>()).Returns((RedisValue)json);

        var connectionMultiplexer = Substitute.For<IConnectionMultiplexer>();
        connectionMultiplexer.GetDatabase().Returns(database);

        var reader = new WorkerHealthReader(connectionMultiplexer, NullLogger<WorkerHealthReader>.Instance);

        var result = await reader.ReadAsync();

        Assert.NotNull(result);
        Assert.True(result.IsConnected);
        Assert.Equal(lastMessage, result.LastMessageReceivedUtc);
        Assert.Equal(connectAttempted, result.ConnectAttemptedUtc);
        Assert.Equal("worker-abc", result.WorkerInstanceId);
        // Trailing fields not in the JSON must fall back to their record defaults, not throw or null
        // out the whole snapshot — this is the rolling-deploy contract documented on the record.
        Assert.False(result.SevenTvEnabled);
        Assert.Null(result.SevenTvLastFrameUtc);
    }
}
