using EmotePurge.Core.Messaging;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The wire format is the contract between Worker, Api and browser — the frontend mirrors these exact
// field names. Asserting the raw JSON string (not just a roundtrip) is the point: a changed naming
// policy would still roundtrip inside .NET while silently breaking every client.
public class LiveEventTests
{
    [Fact]
    public void Serialize_UsesCamelCase_AndOmitsUnsetMembers()
    {
        Assert.Equal(
            """{"type":"usage.flushed","channel":"handofblood"}""",
            new LiveEvent(LiveEvents.UsageFlushed, "handofblood").Serialize());
    }

    [Fact]
    public void Serialize_IncludesSessionId_ForVoteEvents()
    {
        Assert.Equal(
            """{"type":"vote.changed","channel":"handofblood","sessionId":42}""",
            new LiveEvent(LiveEvents.VoteChanged, "handofblood", 42).Serialize());
    }

    [Fact]
    public void Serialize_EmitsTypeOnly_ForChannelLessEvents()
    {
        Assert.Equal("""{"type":"worker.health"}""", new LiveEvent(LiveEvents.WorkerHealth).Serialize());
    }

    [Fact]
    public void SerializeThenTryParse_RoundTripsEveryMember()
    {
        var original = new LiveEvent(LiveEvents.VoteChanged, "somechannel", 7);

        var parsed = LiveEvent.TryParse(original.Serialize());

        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("12")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"channel":"handofblood"}""")]
    [InlineData("""{"type":""}""")]
    public void TryParse_ReturnsNull_ForAnythingThatIsNotALiveEvent(string? payload)
    {
        // Anything at all may publish onto a Redis channel; every one of these must be droppable
        // without an exception reaching the fan-out.
        Assert.Null(LiveEvent.TryParse(payload));
    }

    [Fact]
    public void TryParse_IgnoresUnknownFields()
    {
        // Forward compatibility: a newer producer may add fields, and an older Api must keep
        // delivering the event instead of discarding it.
        var parsed = LiveEvent.TryParse("""{"type":"channel.synced","channel":"x","somethingNew":true,"nested":{"a":1}}""");

        Assert.NotNull(parsed);
        Assert.Equal(LiveEvents.ChannelSynced, parsed.Type);
        Assert.Equal("x", parsed.Channel);
        Assert.Null(parsed.SessionId);
    }

    [Fact]
    public void TypeSets_CoverTheEventsTheirStreamsAnnounce()
    {
        Assert.Contains(LiveEvents.WorkerHealth, LiveEvents.AdminTypes);
        Assert.Contains(LiveEvents.ChannelSynced, LiveEvents.AdminTypes);

        // The dashboard polls over REST (60 s) instead of listening to the admin stream: a flush is a
        // per-channel event and must not start flooding admin streams (#381, assumption A-4).
        Assert.DoesNotContain(LiveEvents.UsageFlushed, LiveEvents.AdminTypes);

        Assert.Contains(LiveEvents.UsageFlushed, LiveEvents.ChannelTypes);
        Assert.Contains(LiveEvents.VoteChanged, LiveEvents.ChannelTypes);
        Assert.Contains(LiveEvents.ChannelSynced, LiveEvents.ChannelTypes);

        // The heartbeat is injected by the broker, never published — no stream may forward one it
        // received from Redis.
        Assert.DoesNotContain(LiveEvents.Ping, LiveEvents.AdminTypes);
        Assert.DoesNotContain(LiveEvents.Ping, LiveEvents.ChannelTypes);
    }
}
