using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Worker.Tests;

/// <summary>
/// AK 14 (spec 2026-09-20, section 5): the same "one snapshot per message, count under (resolved
/// emote id, snapshot.EmoteSetId)" algorithm <c>TwitchChatManager</c> runs
/// (<c>TwitchChatManager.cs:1024-1055</c>), driven directly against the real
/// <see cref="EmoteMatchCache"/> and <see cref="EmoteUsageCounter"/> instead of through TwitchLib
/// (verified live instead, rule 11/16) or through <c>SevenTvSyncService</c>/Postgres
/// (<c>Integration/SevenTvSyncServiceTests.cs</c> covers the DB-driven switch itself, and the
/// same-named-pair case). Both production classes are container-free — <see cref="EmoteMatchCache"/>
/// is an in-memory dictionary with no Postgres/Redis touch — which is exactly why this case belongs
/// in the container-free Worker suite rather than needing Testcontainers.
/// </summary>
public class EmoteMatchCacheSwitchCountingTests
{
    private const string ChannelName = "switchtest";
    private const string SetA = "set-a";
    private const string SetB = "set-b";

    [Fact]
    public void MessagesAroundACacheSwap_SplitIntoTwoUsageKeys()
    {
        // Three messages for the same emote — before, during (after the swap, before any flush) and
        // after — land as two keys: the first under the old set, the other two under the new one.
        var cache = new EmoteMatchCache();
        var counter = new EmoteUsageCounter();
        cache.ReplaceChannel(ChannelName, SetA, new Dictionary<string, string> { ["combo"] = "emote-1" });

        CountMessage(cache, counter, "combo"); // before

        cache.ReplaceChannel(ChannelName, SetB, new Dictionary<string, string> { ["combo"] = "emote-1" });

        CountMessage(cache, counter, "combo"); // during
        CountMessage(cache, counter, "combo"); // after

        var drained = counter.DrainAndReset();
        Assert.Equal(2, drained.Count);
        Assert.Equal(1, drained[new UsageCounterKey("emote-1", SetA)].Human);
        Assert.Equal(2, drained[new UsageCounterKey("emote-1", SetB)].Human);
    }

    // The counting half of TwitchChatManager's per-message loop: one snapshot read, one resolved
    // id, one Increment call — reproduced here without TwitchLib or a chat-message type.
    private static void CountMessage(EmoteMatchCache cache, EmoteUsageCounter counter, string emoteName)
    {
        var snapshot = cache.GetChannelSnapshot(ChannelName);
        counter.Increment(snapshot.NameToEmoteId[emoteName], snapshot.EmoteSetId, UsageCategory.Human);
    }
}
