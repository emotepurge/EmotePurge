using EmotePurge.Core.Chat;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Worker.Tests;

/// <summary>
/// Covers <see cref="EmoteUsageCounter.PendingEmoteCount"/>, added for the admin monitoring page,
/// and the three-way category split (#73) introduced alongside <see cref="IBotChatterDetector"/> and
/// Shared Chat. <see cref="EmoteUsageCounter.PendingEmoteCount"/> counts distinct buffered emotes,
/// not the sum of their hits — the tests pin that down, because the two only differ once an emote is
/// seen twice. <see cref="UsageCategoryRule"/> is covered here too: it lives in the same file as
/// <see cref="UsageCategory"/> and is the one place the room-vs-bot precedence is decided.
///
/// Since 2026-09-20 (spec section 5) the buffering key is <see cref="UsageCounterKey"/> — an emote
/// together with the 7TV set the match cache had active when the hit was matched, not the emote
/// alone. Most tests below fix an arbitrary <c>SetA</c> throughout, because they are about the
/// human/bot/shared-chat split, not the set dimension; the set-specific behaviour has its own tests.
/// </summary>
public class EmoteUsageCounterTests
{
    private const string SetA = "set-a";
    private const string SetB = "set-b";

    [Fact]
    public void PendingEmoteCount_StartsAtZero()
    {
        Assert.Equal(0, new EmoteUsageCounter().PendingEmoteCount);
    }

    [Fact]
    public void PendingEmoteCount_CountsDistinctEmotesNotHits()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a", SetA, UsageCategory.Human);
        counter.Increment("a", SetA, UsageCategory.Human);
        counter.Increment("a", SetA, UsageCategory.Human);
        counter.Increment("b", SetA, UsageCategory.Human);

        Assert.Equal(2, counter.PendingEmoteCount);
    }

    [Fact]
    public void DrainAndReset_EmptiesThePendingCount()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a", SetA, UsageCategory.Human);
        counter.Increment("b", SetA, UsageCategory.Human);

        counter.DrainAndReset();

        Assert.Equal(0, counter.PendingEmoteCount);
    }

    [Fact]
    public void Merge_RestoresThePendingCount()
    {
        // The requeue path after a failed flush: the monitoring page must show the backlog again,
        // otherwise a failing flush looks like an idle worker.
        var counter = new EmoteUsageCounter();
        counter.Increment("a", SetA, UsageCategory.Human);
        counter.Increment("b", SetA, UsageCategory.Human);
        var drained = counter.DrainAndReset();

        counter.Merge(drained);

        Assert.Equal(2, counter.PendingEmoteCount);
    }

    [Fact]
    public void Increment_KeepsAllThreeCategoriesOfTheSameEmoteSeparate()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a", SetA, UsageCategory.Human);
        counter.Increment("a", SetA, UsageCategory.Human);
        counter.Increment("a", SetA, UsageCategory.Bot);
        counter.Increment("a", SetA, UsageCategory.SharedChat);
        counter.Increment("a", SetA, UsageCategory.SharedChat);
        counter.Increment("a", SetA, UsageCategory.SharedChat);

        var drained = counter.DrainAndReset();

        Assert.Equal(new EmoteUsageCounts(Human: 2, Bot: 1, SharedChat: 3), drained[new UsageCounterKey("a", SetA)]);
    }

    [Fact]
    public void Merge_AddsAllThreeComponentsOntoExistingEntries()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a", SetA, UsageCategory.Human);
        counter.Increment("a", SetA, UsageCategory.Bot);
        counter.Increment("a", SetA, UsageCategory.SharedChat);

        counter.Merge(new Dictionary<UsageCounterKey, EmoteUsageCounts>
        {
            [new UsageCounterKey("a", SetA)] = new EmoteUsageCounts(Human: 3, Bot: 2, SharedChat: 4),
        });

        var drained = counter.DrainAndReset();

        Assert.Equal(new EmoteUsageCounts(Human: 4, Bot: 3, SharedChat: 5), drained[new UsageCounterKey("a", SetA)]);
    }

    [Fact]
    public void DrainAndReset_ReturnsAllThreeComponentsAndThenEmptiesTheCounter()
    {
        var counter = new EmoteUsageCounter();
        counter.Increment("a", SetA, UsageCategory.Human);
        counter.Increment("b", SetA, UsageCategory.Bot);
        counter.Increment("c", SetA, UsageCategory.SharedChat);

        var drained = counter.DrainAndReset();

        Assert.Equal(new EmoteUsageCounts(Human: 1, Bot: 0, SharedChat: 0), drained[new UsageCounterKey("a", SetA)]);
        Assert.Equal(new EmoteUsageCounts(Human: 0, Bot: 1, SharedChat: 0), drained[new UsageCounterKey("b", SetA)]);
        Assert.Equal(new EmoteUsageCounts(Human: 0, Bot: 0, SharedChat: 1), drained[new UsageCounterKey("c", SetA)]);
        Assert.Equal(0, counter.PendingEmoteCount);
    }

    [Fact]
    public void PendingEmoteCount_CountsAnEmoteSeenOnlyFromBotsAsOneEmote()
    {
        // Semantics unchanged by the split: PendingEmoteCount is the number of distinct emotes
        // buffered, regardless of whether every hit so far came from a bot.
        var counter = new EmoteUsageCounter();
        counter.Increment("a", SetA, UsageCategory.Bot);
        counter.Increment("a", SetA, UsageCategory.Bot);

        Assert.Equal(1, counter.PendingEmoteCount);
    }

    [Fact]
    public void PendingEmoteCount_CountsAnEmoteSeenOnlyFromForeignRoomsAsOneEmote()
    {
        // Same guarantee as the bot-only case above, now for the room split: an emote only ever
        // matched in a Shared Chat mirror still counts as one buffered emote.
        var counter = new EmoteUsageCounter();
        counter.Increment("a", SetA, UsageCategory.SharedChat);
        counter.Increment("a", SetA, UsageCategory.SharedChat);

        Assert.Equal(1, counter.PendingEmoteCount);
    }

    [Fact]
    public void Increment_SameEmoteId_DifferentEmoteSetId_AreCountedAsSeparateKeys()
    {
        // AK 12: the key is (EmoteId, EmoteSetId), not EmoteId alone — a cache swap between two
        // chat messages must not merge what the two messages saw.
        var counter = new EmoteUsageCounter();
        counter.Increment("a", SetA, UsageCategory.Human);
        counter.Increment("a", SetA, UsageCategory.Human);
        counter.Increment("a", SetB, UsageCategory.Human);

        var drained = counter.DrainAndReset();

        Assert.Equal(new EmoteUsageCounts(Human: 2, Bot: 0, SharedChat: 0), drained[new UsageCounterKey("a", SetA)]);
        Assert.Equal(new EmoteUsageCounts(Human: 1, Bot: 0, SharedChat: 0), drained[new UsageCounterKey("a", SetB)]);
    }

    [Fact]
    public void Merge_WithDifferentEmoteSetIdForTheSameEmote_AddsANewEntry_DoesNotCombineWithTheOtherSet()
    {
        // AK 12: a requeued batch from a different observed set must not be folded onto the same
        // emote's entry for the currently buffered set — that would silently misattribute counts
        // across sets, exactly the failure mode the composite key exists to prevent.
        var counter = new EmoteUsageCounter();
        counter.Increment("a", SetA, UsageCategory.Human);

        counter.Merge(new Dictionary<UsageCounterKey, EmoteUsageCounts>
        {
            [new UsageCounterKey("a", SetB)] = new EmoteUsageCounts(Human: 5, Bot: 0, SharedChat: 0),
        });

        var drained = counter.DrainAndReset();

        Assert.Equal(new EmoteUsageCounts(Human: 1, Bot: 0, SharedChat: 0), drained[new UsageCounterKey("a", SetA)]);
        Assert.Equal(new EmoteUsageCounts(Human: 5, Bot: 0, SharedChat: 0), drained[new UsageCounterKey("a", SetB)]);
    }

    [Fact]
    public void PendingEmoteCount_CountsDistinctEmoteSetPairs_NotDistinctEmoteIds()
    {
        // AK 12: the same emote observed under two sets is two pending keys, not one — matches the
        // dictionary having two entries after Increment_SameEmoteId_DifferentEmoteSetId... above.
        var counter = new EmoteUsageCounter();
        counter.Increment("a", SetA, UsageCategory.Human);
        counter.Increment("a", SetB, UsageCategory.Human);

        Assert.Equal(2, counter.PendingEmoteCount);
    }

    [Theory]
    [InlineData(MessageOrigin.Own, false, UsageCategory.Human)]
    [InlineData(MessageOrigin.Own, true, UsageCategory.Bot)]
    [InlineData(MessageOrigin.Foreign, false, UsageCategory.SharedChat)]
    [InlineData(MessageOrigin.Foreign, true, UsageCategory.SharedChat)]
    [InlineData(MessageOrigin.Indeterminate, false, UsageCategory.SharedChat)]
    [InlineData(MessageOrigin.Indeterminate, true, UsageCategory.SharedChat)]
    public void Resolve_PrioritizesRoomOverBot_AndFoldsIndeterminateIntoSharedChat(
        MessageOrigin origin, bool isBot, UsageCategory expected)
    {
        Assert.Equal(expected, UsageCategoryRule.Resolve(origin, isBot));
    }
}
