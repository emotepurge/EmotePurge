using EmotePurge.Core.Matching;
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

    // AK 17 / O1 (emote slots spec): while the match map leaves an emote out (a seed of the active set
    // is not settled yet) nothing of it is counted; once the map carries its two slots, one message with
    // both names counts +1 on each slot — deduplicated per slot, not per emote.
    [Fact]
    public void AnEmptyMapCountsNothing_AndAMapWithTwoSlotsOfOneEmoteCountsEachSlotOncePerMessage()
    {
        var cache = new EmoteMatchCache();
        var counter = new EmoteUsageCounter();
        cache.ReplaceChannel(ChannelName, SetA, new Dictionary<string, string>());

        CountChatLine(cache, counter, "hob44 ome44");
        Assert.Empty(counter.DrainAndReset());

        cache.ReplaceChannel(ChannelName, SetA, new Dictionary<string, string> { ["ome44"] = "slot-ome44", ["hob44"] = "slot-hob44" });
        CountChatLine(cache, counter, "ome44 hob44 ome44");

        var drained = counter.DrainAndReset();
        Assert.Equal(2, drained.Count);
        Assert.Equal(1, drained[new UsageCounterKey("slot-ome44", SetA)].Human);
        Assert.Equal(1, drained[new UsageCounterKey("slot-hob44", SetA)].Human);
    }

    // The whole-line variant: the shared matching rule TwitchChatManager applies to a chat message.
    private static void CountChatLine(EmoteMatchCache cache, EmoteUsageCounter counter, string message)
    {
        var snapshot = cache.GetChannelSnapshot(ChannelName);
        foreach (var slotId in EmoteNameMatching.MatchEmoteIds(message, snapshot.NameToSlotId))
        {
            counter.Increment(slotId, snapshot.EmoteSetId, UsageCategory.Human);
        }
    }

    // The counting half of TwitchChatManager's per-message loop: one snapshot read, one resolved
    // id, one Increment call — reproduced here without TwitchLib or a chat-message type.
    private static void CountMessage(EmoteMatchCache cache, EmoteUsageCounter counter, string emoteName)
    {
        var snapshot = cache.GetChannelSnapshot(ChannelName);
        counter.Increment(snapshot.NameToSlotId[emoteName], snapshot.EmoteSetId, UsageCategory.Human);
    }
}
