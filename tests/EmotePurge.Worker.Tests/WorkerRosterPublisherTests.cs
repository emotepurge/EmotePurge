using System.Text.Json;
using System.Text.Json.Serialization;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Worker;
using EmotePurge.Worker.SevenTv;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Container-free: the publisher's snapshot assembly is pure once the chat manager is substituted.
public class WorkerRosterPublisherTests
{
    [Fact]
    public void BuildSnapshot_MapsTrackerFieldsAndMarksIrcRowsWithIntent()
    {
        var snapshot = Build(
            [
                new TwitchRosterEntry("confirmed", true, null),
                new TwitchRosterEntry("silent", false, null, 125, "noConfirmation"),
            ]);

        Assert.Equal(WorkerRosterSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
        var confirmed = snapshot.Channels.Single(c => c.ChannelName == "confirmed");
        Assert.True(confirmed.IrcIntent);
        Assert.Null(confirmed.IrcUnconfirmedSeconds);
        Assert.Null(confirmed.IrcJoinFailureReason);

        var silent = snapshot.Channels.Single(c => c.ChannelName == "silent");
        Assert.True(silent.IrcIntent);
        Assert.False(silent.IrcJoinConfirmed);
        Assert.Equal(125, silent.IrcUnconfirmedSeconds);
        Assert.Equal("noConfirmation", silent.IrcJoinFailureReason);
    }

    [Fact]
    public void BuildSnapshot_SevenTvOnlyRowHasNoIrcIntent()
    {
        var registry = new SevenTvSubscriptionRegistry();
        registry.SetDesired("onlyseventv", "01SET", "01USER");
        registry.SetDesired("both", "01SET2", null);

        var snapshot = Build([new TwitchRosterEntry("both", false, null, 7, "noConfirmation")], registry);

        var only = snapshot.Channels.Single(c => c.ChannelName == "onlyseventv");
        Assert.False(only.IrcIntent);
        Assert.Null(only.IrcUnconfirmedSeconds);
        Assert.Null(only.IrcJoinFailureReason);

        // The union keeps the IRC side's fields when the 7TV side overwrites the row.
        var both = snapshot.Channels.Single(c => c.ChannelName == "both");
        Assert.True(both.IrcIntent);
        Assert.Equal(7, both.IrcUnconfirmedSeconds);
        Assert.Equal("01SET2", both.SevenTvEmoteSetId);
    }

    [Fact]
    public void BuildSnapshot_EmptyRosterStillCarriesTheSchemaMarker()
    {
        var snapshot = Build([]);

        Assert.Empty(snapshot.Channels);
        Assert.Equal(WorkerRosterSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
    }

    [Fact]
    public void Payload_OmitsNullJoinFieldsOfAConfirmedRow()
    {
        var snapshot = Build([new TwitchRosterEntry("confirmed", true, null)]);
        var options = new JsonSerializerOptions(JsonSerializerOptions.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var json = JsonSerializer.Serialize(snapshot, options);

        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"ircIntent\":true", json);
        Assert.DoesNotContain("ircUnconfirmedSeconds", json);
        Assert.DoesNotContain("ircJoinFailureReason", json);
    }

    private static WorkerRosterSnapshot Build(
        IReadOnlyList<TwitchRosterEntry> roster,
        SevenTvSubscriptionRegistry? registry = null)
    {
        var chat = Substitute.For<ITwitchChatManager>();
        chat.GetRoster().Returns(roster);
        var gate = new BootRecoveryGate();
        var publisher = new WorkerRosterPublisher(
            NullLogger<WorkerRosterPublisher>.Instance,
            chat,
            registry ?? new SevenTvSubscriptionRegistry(),
            gate,
            new WorkerIdentity(),
            Substitute.For<IRedisPublisher>(),
            Substitute.For<IConnectionMultiplexer>());
        return publisher.BuildSnapshot();
    }
}
