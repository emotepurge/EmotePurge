using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The pending slot key of plan #373 (E4/E16): one producer, one recogniser, never a real slot id.
public class PendingSlotKeyTests
{
    [Fact]
    public void For_ThenTryGetEmoteId_RoundTripsTheEmoteId()
    {
        var emoteId = Guid.NewGuid().ToString();

        var key = PendingSlotKey.For(emoteId);

        Assert.Equal("pending-slot:" + emoteId, key);
        Assert.True(PendingSlotKey.TryGetEmoteId(key, out var parsed));
        Assert.Equal(emoteId, parsed);
        Assert.True(PendingSlotKey.IsPending(key));
    }

    [Theory]
    [InlineData("3f2b8c1e-9d4a-4b7e-8f61-2c0a5e9d7b13")] // Guid.NewGuid().ToString() / gen_random_uuid()::text
    [InlineData("seed-slot")] // test-style ids
    [InlineData("")]
    [InlineData("pending-slot:")] // the bare prefix names no emote
    [InlineData("PENDING-SLOT:e1")] // ordinal, case-sensitive
    [InlineData(" pending-slot:e1")]
    public void TryGetEmoteId_RejectsEverythingThatIsNotAPendingKey(string slotKey)
    {
        Assert.False(PendingSlotKey.TryGetEmoteId(slotKey, out var emoteId));
        Assert.Null(emoteId);
        Assert.False(PendingSlotKey.IsPending(slotKey));
    }

    [Fact]
    public void ARealSlotId_IsNeverPending()
    {
        for (var i = 0; i < 1000; i++)
        {
            Assert.False(PendingSlotKey.IsPending(Guid.NewGuid().ToString()));
        }
    }

    [Fact]
    public void For_RefusesAMissingEmoteId()
    {
        Assert.Throws<ArgumentException>(() => PendingSlotKey.For(""));
        Assert.Throws<ArgumentNullException>(() => PendingSlotKey.For(null!));
    }
}
