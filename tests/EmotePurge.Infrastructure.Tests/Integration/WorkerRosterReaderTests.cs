using System.Text.Json;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Real Redis, not a mocked IDatabase: what is being verified here is the wire format — that what the
// worker writes with JsonSerializerOptions.Web comes back through the same options unchanged, and
// that a payload written by a different version of the writer does not throw.
[Collection("Redis")]
public class WorkerRosterReaderTests(RedisFixture fixture)
{
    [Fact]
    public async Task ReadAsync_RoundTripsTheSnapshot()
    {
        var snapshot = new WorkerRosterSnapshot(
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            "a1b2c3d4",
            new DateTime(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc),
            BootRecoveryCompleted: true,
            Truncated: false,
            [
                new WorkerRosterChannel(
                    "handofblood",
                    IrcJoinConfirmed: true,
                    new DateTime(2026, 8, 1, 11, 59, 0, DateTimeKind.Utc),
                    "01HSET",
                    SevenTvEmoteSetAcknowledged: true,
                    "01HUSER",
                    SevenTvUserAcknowledged: true,
                    IrcIntent: true),
                new WorkerRosterChannel(
                    "sensitron",
                    IrcJoinConfirmed: false,
                    LastMessageUtc: null,
                    SevenTvEmoteSetId: null,
                    SevenTvEmoteSetAcknowledged: false,
                    SevenTvUserId: null,
                    SevenTvUserAcknowledged: false,
                    IrcIntent: true,
                    IrcUnconfirmedSeconds: 125,
                    IrcJoinFailureReason: "noConfirmation"),
            ],
            WorkerRosterSnapshot.CurrentSchemaVersion);

        await WriteAsync(JsonSerializer.Serialize(snapshot, JsonSerializerOptions.Web));

        var read = await new WorkerRosterReader(fixture.Connection, NullLogger<WorkerRosterReader>.Instance).ReadAsync();

        Assert.NotNull(read);
        Assert.Equal(snapshot.GeneratedAtUtc, read.GeneratedAtUtc);
        Assert.Equal("a1b2c3d4", read.WorkerInstanceId);
        Assert.True(read.BootRecoveryCompleted);
        Assert.False(read.Truncated);
        Assert.Equal(WorkerRosterSnapshot.CurrentSchemaVersion, read.SchemaVersion);
        Assert.Equal(2, read.Channels.Count);

        var joined = read.Channels[0];
        Assert.Equal("handofblood", joined.ChannelName);
        Assert.True(joined.IrcJoinConfirmed);
        Assert.Equal("01HUSER", joined.SevenTvUserId);
        Assert.True(joined.IrcIntent);
        Assert.Null(joined.IrcUnconfirmedSeconds);
        Assert.Null(joined.IrcJoinFailureReason);

        // Nulls survive as nulls. The publisher omits them from the payload entirely, which must
        // read as "unknown/none" rather than as an empty string or the epoch.
        var pending = read.Channels[1];
        Assert.Null(pending.LastMessageUtc);
        Assert.Null(pending.SevenTvEmoteSetId);
        Assert.Null(pending.SevenTvUserId);
        Assert.True(pending.IrcIntent);
        Assert.Equal(125, pending.IrcUnconfirmedSeconds);
        Assert.Equal("noConfirmation", pending.IrcJoinFailureReason);
    }

    [Fact]
    public async Task ReadAsync_EmptyRosterStillCarriesTheSchemaMarker()
    {
        var empty = new WorkerRosterSnapshot(
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            "a1b2c3d4",
            new DateTime(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc),
            BootRecoveryCompleted: true,
            Truncated: false,
            [],
            WorkerRosterSnapshot.CurrentSchemaVersion);

        await WriteAsync(JsonSerializer.Serialize(empty, JsonSerializerOptions.Web));

        var read = await new WorkerRosterReader(fixture.Connection, NullLogger<WorkerRosterReader>.Instance).ReadAsync();

        Assert.NotNull(read);
        Assert.Empty(read.Channels);
        Assert.Equal(WorkerRosterSnapshot.CurrentSchemaVersion, read.SchemaVersion);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_WhenTheKeyIsAbsent()
    {
        // The key expired or the worker never started. That absence is the answer the API reports,
        // not an error it has to handle.
        await fixture.Connection.GetDatabase().KeyDeleteAsync(WorkerHealthKeys.Roster);

        Assert.Null(await new WorkerRosterReader(fixture.Connection, NullLogger<WorkerRosterReader>.Instance).ReadAsync());
    }

    [Fact]
    public async Task ReadAsync_ToleratesAPayloadFromADifferentWriterVersion()
    {
        // Rolling deploys put an older worker's payload in front of a newer API. Unknown properties
        // are ignored, missing ones fall back to their defaults, and a missing channel array must
        // read as "no rows" rather than throwing or handing every consumer a null list.
        await WriteAsync("""
            {"generatedAtUtc":"2026-08-01T12:00:00Z","workerInstanceId":"old","somethingNew":42}
            """);

        var read = await new WorkerRosterReader(fixture.Connection, NullLogger<WorkerRosterReader>.Instance).ReadAsync();

        Assert.NotNull(read);
        Assert.Equal("old", read.WorkerInstanceId);
        Assert.False(read.BootRecoveryCompleted);
        Assert.Empty(read.Channels);
        Assert.Equal(0, read.SchemaVersion);
    }

    [Fact]
    public async Task ReadAsync_LegacyChannelRowReadsWithoutJoinHealthFields()
    {
        await WriteAsync("""
            {"generatedAtUtc":"2026-08-01T12:00:00Z","workerInstanceId":"old","channels":[
              {"channelName":"a","ircJoinConfirmed":true,"sevenTvEmoteSetAcknowledged":false,"sevenTvUserAcknowledged":false}]}
            """);

        var read = await new WorkerRosterReader(fixture.Connection, NullLogger<WorkerRosterReader>.Instance).ReadAsync();

        Assert.NotNull(read);
        Assert.Equal(0, read.SchemaVersion);
        var row = Assert.Single(read.Channels);
        Assert.False(row.IrcIntent);
        Assert.Null(row.IrcUnconfirmedSeconds);
        Assert.Null(row.IrcJoinFailureReason);
    }

    private Task WriteAsync(string payload)
        => fixture.Connection.GetDatabase().StringSetAsync(WorkerHealthKeys.Roster, payload);
}
