using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Pure, dependency-free — no container, no mock, no live channel needed. Exercises the exact
// in-memory lookup TwitchChatManager.OnMessageReceived hits for every chat message.
public class EmoteMatchCacheTests
{
    [Fact]
    public void GetChannelEmotes_ReturnsEmpty_ForUnknownChannel()
    {
        var cache = new EmoteMatchCache();

        var emotes = cache.GetChannelEmotes("unknownchannel");

        Assert.Empty(emotes);
    }

    [Fact]
    public void ReplaceChannel_ThenGetChannelEmotes_ReturnsWhatWasStored()
    {
        var cache = new EmoteMatchCache();
        var emotes = new Dictionary<string, string> { ["PogChamp"] = "emote-1", ["Kappa"] = "emote-2" };

        cache.ReplaceChannel("SomeChannel", emotes);

        var result = cache.GetChannelEmotes("somechannel");
        Assert.Equal(2, result.Count);
        Assert.Equal("emote-1", result["PogChamp"]);
    }

    [Fact]
    public void ReplaceChannel_Normalizes_ChannelName_CaseAndWhitespace()
    {
        var cache = new EmoteMatchCache();
        cache.ReplaceChannel("  Sensitron  ", new Dictionary<string, string> { ["Foo"] = "emote-1" });

        var result = cache.GetChannelEmotes("sensitron");

        Assert.Single(result);
    }

    [Fact]
    public void ReplaceChannel_Overwrites_PreviousSetForSameChannel()
    {
        var cache = new EmoteMatchCache();
        cache.ReplaceChannel("channel", new Dictionary<string, string> { ["Old"] = "emote-old" });

        cache.ReplaceChannel("channel", new Dictionary<string, string> { ["New"] = "emote-new" });

        var result = cache.GetChannelEmotes("channel");
        Assert.Single(result);
        Assert.True(result.ContainsKey("New"));
        Assert.False(result.ContainsKey("Old"));
    }

    [Fact]
    public void RemoveChannel_ClearsStoredEmotes()
    {
        var cache = new EmoteMatchCache();
        cache.ReplaceChannel("channel", new Dictionary<string, string> { ["Foo"] = "emote-1" });

        cache.RemoveChannel("channel");

        Assert.Empty(cache.GetChannelEmotes("channel"));
    }

    [Fact]
    public void RemoveChannel_ForUnknownChannel_DoesNotThrow()
    {
        var cache = new EmoteMatchCache();

        var exception = Record.Exception(() => cache.RemoveChannel("neverjoined"));

        Assert.Null(exception);
    }

    [Fact]
    public void ReplaceChannel_DoesNotAffectOtherChannels()
    {
        var cache = new EmoteMatchCache();
        cache.ReplaceChannel("channel-a", new Dictionary<string, string> { ["A"] = "emote-a" });
        cache.ReplaceChannel("channel-b", new Dictionary<string, string> { ["B"] = "emote-b" });

        Assert.Single(cache.GetChannelEmotes("channel-a"));
        Assert.Single(cache.GetChannelEmotes("channel-b"));
        Assert.True(cache.GetChannelEmotes("channel-a").ContainsKey("A"));
    }

    [Fact]
    public void GetCachedChannelNames_ReturnsNormalizedKeys_AndDropsRemovedChannels()
    {
        var cache = new EmoteMatchCache();
        cache.ReplaceChannel("  Alpha ", new Dictionary<string, string> { ["Foo"] = "emote-1" });
        cache.ReplaceChannel("beta", new Dictionary<string, string>());
        cache.RemoveChannel("BETA");

        Assert.Equal(["alpha"], cache.GetCachedChannelNames());
    }
}
