using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Pure, dependency-free — no container, no mock, no live channel needed. Exercises the exact
// in-memory lookup TwitchChatManager.OnMessageReceived hits for every chat message.
//
// Since 2026-09-20 (spec section 5) the cache hands back one EmoteMatchSnapshot object per channel
// (dictionary + observed set id + generation timestamp) instead of a bare dictionary, so a reader
// can never pair one swap's dictionary with another swap's set id.
public class EmoteMatchCacheTests
{
    [Fact]
    public void GetChannelSnapshot_ReturnsEmpty_ForUnknownChannel()
    {
        var cache = new EmoteMatchCache();

        var snapshot = cache.GetChannelSnapshot("unknownchannel");

        Assert.Empty(snapshot.NameToEmoteId);
    }

    [Fact]
    public void ReplaceChannel_ThenGetChannelSnapshot_ReturnsWhatWasStored()
    {
        var cache = new EmoteMatchCache();
        var emotes = new Dictionary<string, string> { ["PogChamp"] = "emote-1", ["Kappa"] = "emote-2" };

        cache.ReplaceChannel("SomeChannel", "set-1", emotes);

        var result = cache.GetChannelSnapshot("somechannel").NameToEmoteId;
        Assert.Equal(2, result.Count);
        Assert.Equal("emote-1", result["PogChamp"]);
    }

    [Fact]
    public void ReplaceChannel_Normalizes_ChannelName_CaseAndWhitespace()
    {
        var cache = new EmoteMatchCache();
        cache.ReplaceChannel("  Sensitron  ", "set-1", new Dictionary<string, string> { ["Foo"] = "emote-1" });

        var result = cache.GetChannelSnapshot("sensitron").NameToEmoteId;

        Assert.Single(result);
    }

    [Fact]
    public void ReplaceChannel_Overwrites_PreviousSetForSameChannel()
    {
        var cache = new EmoteMatchCache();
        cache.ReplaceChannel("channel", "set-1", new Dictionary<string, string> { ["Old"] = "emote-old" });

        cache.ReplaceChannel("channel", "set-1", new Dictionary<string, string> { ["New"] = "emote-new" });

        var result = cache.GetChannelSnapshot("channel").NameToEmoteId;
        Assert.Single(result);
        Assert.True(result.ContainsKey("New"));
        Assert.False(result.ContainsKey("Old"));
    }

    [Fact]
    public void RemoveChannel_ClearsStoredEmotes()
    {
        var cache = new EmoteMatchCache();
        cache.ReplaceChannel("channel", "set-1", new Dictionary<string, string> { ["Foo"] = "emote-1" });

        cache.RemoveChannel("channel");

        Assert.Empty(cache.GetChannelSnapshot("channel").NameToEmoteId);
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
        cache.ReplaceChannel("channel-a", "set-1", new Dictionary<string, string> { ["A"] = "emote-a" });
        cache.ReplaceChannel("channel-b", "set-2", new Dictionary<string, string> { ["B"] = "emote-b" });

        Assert.Single(cache.GetChannelSnapshot("channel-a").NameToEmoteId);
        Assert.Single(cache.GetChannelSnapshot("channel-b").NameToEmoteId);
        Assert.True(cache.GetChannelSnapshot("channel-a").NameToEmoteId.ContainsKey("A"));
    }

    [Fact]
    public void GetChannelSnapshot_ForUnknownChannel_HasEmptyEmoteSetId()
    {
        // AK 13: "" is reserved for the empty snapshot — nothing that has ever actually been
        // populated through ReplaceChannel may carry it.
        var cache = new EmoteMatchCache();

        var snapshot = cache.GetChannelSnapshot("neverjoined");

        Assert.Equal(string.Empty, snapshot.EmoteSetId);
    }

    [Fact]
    public void GetChannelSnapshot_ReturnsSetIdAndGeneration_TogetherWithTheDictionary()
    {
        // AK 13: the three pieces (dictionary, set id, generation) travel as one object from a
        // single ReplaceChannel call — this is what makes the read at the chat-message call site
        // safe against an interleaved cache swap (F2).
        var cache = new EmoteMatchCache();
        var before = DateTimeOffset.UtcNow;

        cache.ReplaceChannel("channel", "set-42", new Dictionary<string, string> { ["Foo"] = "emote-1" });

        var after = DateTimeOffset.UtcNow;
        var snapshot = cache.GetChannelSnapshot("channel");

        Assert.Equal("set-42", snapshot.EmoteSetId);
        Assert.True(snapshot.GeneratedAtUtc >= before && snapshot.GeneratedAtUtc <= after);
        Assert.True(snapshot.NameToEmoteId.ContainsKey("Foo"));
    }

    [Fact]
    public void ReplaceChannel_Overwrite_ReplacesSetIdAndGeneration_NotJustTheDictionary()
    {
        // AK 13: a second ReplaceChannel call must swap the whole snapshot, not just merge in a new
        // dictionary while leaving a stale set id or generation behind.
        var cache = new EmoteMatchCache();
        cache.ReplaceChannel("channel", "set-old", new Dictionary<string, string> { ["Old"] = "emote-old" });
        var firstGeneration = cache.GetChannelSnapshot("channel").GeneratedAtUtc;

        cache.ReplaceChannel("channel", "set-new", new Dictionary<string, string> { ["New"] = "emote-new" });
        var snapshot = cache.GetChannelSnapshot("channel");

        Assert.Equal("set-new", snapshot.EmoteSetId);
        Assert.True(snapshot.GeneratedAtUtc >= firstGeneration);
    }

    [Fact]
    public async Task ReplaceChannel_ConcurrentSwaps_NeverExposeAMixedPairOfDictionaryAndSetId()
    {
        // AK 13 (swap consistency): a reader racing 1,000 alternating swaps of the same channel
        // must always see one of the two known (dictionary, set id) pairings, never a dictionary
        // from one swap combined with the set id of the other — the failure mode F2 warns about,
        // here forced under real concurrency instead of argued about.
        var cache = new EmoteMatchCache();
        var dictA = new Dictionary<string, string> { ["A"] = "emote-a" };
        var dictB = new Dictionary<string, string> { ["B"] = "emote-b" };
        const string channel = "concurrent-channel";
        const int swaps = 1_000;
        using var cts = new CancellationTokenSource();

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < swaps; i++)
            {
                cache.ReplaceChannel(channel, i % 2 == 0 ? "set-a" : "set-b", i % 2 == 0 ? dictA : dictB);
            }

            cts.Cancel();
        });

        var mixedPairObserved = false;
        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var snapshot = cache.GetChannelSnapshot(channel);
                if (snapshot.NameToEmoteId.Count == 0)
                {
                    continue;
                }

                var isSetA = ReferenceEquals(snapshot.NameToEmoteId, dictA);
                var isSetB = ReferenceEquals(snapshot.NameToEmoteId, dictB);
                var claimsSetA = snapshot.EmoteSetId == "set-a";
                var claimsSetB = snapshot.EmoteSetId == "set-b";

                if ((isSetA && !claimsSetA) || (isSetB && !claimsSetB))
                {
                    mixedPairObserved = true;
                    break;
                }
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.False(mixedPairObserved);
    }
}
