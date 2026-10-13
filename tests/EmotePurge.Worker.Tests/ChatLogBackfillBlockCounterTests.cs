using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Core.Services;
using EmotePurge.Worker.ChatLogBackfill;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Child 4 AC 2 (#350, spec 4.4, D9). Synthetic ids and text only, like ReplayDayCounterTests. The
// classification cases are the ones ReplayDayCounterTests pins for the harness, run against the real
// BotChatterDetector so "bot by badge" and "bot by id" are the live classifier's answers, not a stub's.
public class ChatLogBackfillBlockCounterTests
{
    private const string Room = "room1";
    private const string NightbotId = "19264788";

    private static readonly ChatLogBackfillBlock Block = new(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 8));
    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoBadges = [];
    private static readonly BotChatterDetector Detector = new(new ConfigurationBuilder().Build());

    [Fact]
    public void OwnHuman_CountsIntoUseCount()
    {
        var counter = Counter();

        counter.Count(Message("Kappa"));

        Assert.Equal([new ChatLogBackfillAggregate("s1", "e1", Day(2), 1, 0, 0)], counter.Aggregates());
    }

    [Fact]
    public void OwnBotByBadge_CountsIntoBotUseCount()
    {
        var counter = Counter();

        counter.Count(Message("Kappa", badges: [new KeyValuePair<string, string>("bot-badge", "1")]));

        Assert.Equal([new ChatLogBackfillAggregate("s1", "e1", Day(2), 0, 1, 0)], counter.Aggregates());
    }

    [Fact]
    public void OwnBotById_CountsIntoBotUseCount()
    {
        var counter = Counter();

        counter.Count(Message("Kappa", userId: NightbotId));

        Assert.Equal([new ChatLogBackfillAggregate("s1", "e1", Day(2), 0, 1, 0)], counter.Aggregates());
    }

    [Fact]
    public void ForeignRoom_CountsIntoSharedChat_EvenForABot()
    {
        var counter = Counter();

        counter.Count(Message("Kappa", sourceRoomId: "other-room"));
        counter.Count(Message("Kappa", userId: NightbotId, sourceRoomId: "other-room"));

        Assert.Equal([new ChatLogBackfillAggregate("s1", "e1", Day(2), 0, 0, 2)], counter.Aggregates());
    }

    [Fact]
    public void OwnRoomWithASourceRoomTag_StaysOwn()
    {
        var counter = Counter();

        counter.Count(Message("Kappa", sourceRoomId: Room));

        Assert.Equal([new ChatLogBackfillAggregate("s1", "e1", Day(2), 1, 0, 0)], counter.Aggregates());
    }

    [Fact]
    public void IndeterminateMarkers_CountIntoSharedChat()
    {
        var counter = Counter();

        counter.Count(Message("Kappa", hasOtherSourceMarkers: true));

        Assert.Equal([new ChatLogBackfillAggregate("s1", "e1", Day(2), 0, 0, 1)], counter.Aggregates());
    }

    [Fact]
    public void AHitBeforeTheEntryDay_IsDiscarded_FromTheEntryDayOn_ItCounts()
    {
        var counter = Counter(kappaEntered: Day(3));

        counter.Count(Message("Kappa", day: 2));
        counter.Count(Message("Kappa", day: 3));
        counter.Count(Message("Kappa", day: 4));

        Assert.Equal(
            [new ChatLogBackfillAggregate("s1", "e1", Day(3), 1, 0, 0), new ChatLogBackfillAggregate("s1", "e1", Day(4), 1, 0, 0)],
            counter.Aggregates());
    }

    [Fact]
    public void ANullEntryDay_IsNoGate_OnAnyDayOfTheBlock()
    {
        var counter = Counter(kappaEntered: null);

        for (var day = 1; day <= 7; day++)
        {
            counter.Count(Message("Kappa", day: day));
        }

        Assert.Equal(7, counter.Aggregates().Count);
        Assert.All(counter.Aggregates(), a => Assert.Equal(1, a.UseCount));
    }

    [Fact]
    public void ARepeatedEmoteInOneMessage_CountsOnce_TwoEmotesCountOnceEach()
    {
        var counter = Counter();

        counter.Count(Message("Kappa Kappa PogU Kappa"));

        Assert.Equal(
            [new ChatLogBackfillAggregate("s1", "e1", Day(2), 1, 0, 0), new ChatLogBackfillAggregate("s2", "e2", Day(2), 1, 0, 0)],
            counter.Aggregates());
    }

    [Fact]
    public void AMessageDatedOutsideTheBlock_IsDropped()
    {
        var counter = Counter();

        counter.Count(Message("Kappa", at: Block.FromUtc.AddTicks(-1)));
        counter.Count(Message("Kappa", at: Block.ToUtcExclusive));
        counter.Count(Message("Kappa", at: Block.ToUtcExclusive.AddTicks(-1)));

        Assert.Equal([new ChatLogBackfillAggregate("s1", "e1", Day(7), 1, 0, 0)], counter.Aggregates());
    }

    [Fact]
    public void MatchingIsTheLiveRule_OrdinalAndSpaceSplit()
    {
        var counter = Counter();

        counter.Count(Message("kappa"));
        counter.Count(Message("Kappa!"));
        counter.Count(Message("xKappa"));

        Assert.Empty(counter.Aggregates());
    }

    [Fact]
    public void Aggregates_AreOrderedBySlotIdThenDay_AndAddUpPerCell()
    {
        var counter = Counter();

        counter.Count(Message("PogU", day: 5));
        counter.Count(Message("Kappa", day: 4));
        counter.Count(Message("PogU", day: 1));
        counter.Count(Message("Kappa", day: 4, userId: "u2"));
        counter.Count(Message("Kappa", day: 4, userId: NightbotId));

        Assert.Equal(
            [
                new ChatLogBackfillAggregate("s1", "e1", Day(4), 2, 1, 0),
                new ChatLogBackfillAggregate("s2", "e2", Day(1), 1, 0, 0),
                new ChatLogBackfillAggregate("s2", "e2", Day(5), 1, 0, 0)
            ],
            counter.Aggregates());
    }

    [Fact]
    public void Aggregates_CarryNoChatterId()
    {
        var counter = Counter();

        counter.Count(Message("Kappa", userId: "998877665"));
        counter.Count(Message("PogU", userId: "112233445", sourceRoomId: "other-room"));

        var printed = string.Join("|", counter.Aggregates().Select(a => a.ToString()));
        Assert.DoesNotContain("998877665", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("112233445", printed, StringComparison.Ordinal);

        // And nothing the instance still holds: every collection field, element by element.
        var held = typeof(ChatLogBackfillBlockCounter)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(f => f.GetValue(counter))
            .OfType<System.Collections.IEnumerable>()
            .SelectMany(e => e.Cast<object?>())
            .Select(o => o?.ToString() ?? string.Empty)
            .ToList();
        Assert.NotEmpty(held);
        Assert.DoesNotContain(held, s => s.Contains("998877665", StringComparison.Ordinal) || s.Contains("112233445", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- Per slot (slot plan #375, AK 23)

    // Two set entries of one emote, ome44 since May and hob44 since September 1: the entry-day gate is the
    // slot's, so a hit on ome44 before September 1 counts and one on hob44 does not.
    [Fact]
    public void TheEntryDayGate_IsPerSlot()
    {
        var block = new ChatLogBackfillBlock(new DateOnly(2026, 8, 28), new DateOnly(2026, 9, 4));
        var counter = new ChatLogBackfillBlockCounter(
            block,
            ChatLogBackfillSnapshotMap.From(
            [
                new ChatLogBackfillSnapshotEmote("s-ome", "x", "ome44", new DateOnly(2026, 5, 2)),
                new ChatLogBackfillSnapshotEmote("s-hob", "x", "hob44", new DateOnly(2026, 9, 1))
            ]),
            Detector.IsBot);

        counter.Count(Message("ome44", at: new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc)));
        counter.Count(Message("hob44", at: new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc)));
        counter.Count(Message("hob44", at: new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(
            [
                new ChatLogBackfillAggregate("s-hob", "x", new DateOnly(2026, 9, 1), 1, 0, 0),
                new ChatLogBackfillAggregate("s-ome", "x", new DateOnly(2026, 8, 30), 1, 0, 0)
            ],
            counter.Aggregates());
    }

    // O1: one hit per value and message is one hit per slot — both aliases of one emote in one message
    // count +1 each, a repeated alias still once.
    [Fact]
    public void BothAliasesOfOneEmoteInOneMessage_CountOncePerSlot()
    {
        var counter = Counter(new ChatLogBackfillSnapshotEmote("s-ome", "x", "ome44", null), new ChatLogBackfillSnapshotEmote("s-hob", "x", "hob44", null));

        counter.Count(Message("ome44 hob44 ome44"));

        Assert.Equal(
            [new ChatLogBackfillAggregate("s-hob", "x", Day(2), 1, 0, 0), new ChatLogBackfillAggregate("s-ome", "x", Day(2), 1, 0, 0)],
            counter.Aggregates());
    }

    // Cells are (slot, day), each aggregate carries its slot's emote: two emotes with one slot each and
    // one emote with two slots give one aggregate per slot and day, never one per emote.
    [Fact]
    public void Aggregates_ArePerSlotAndDay_WithTheSlotsEmote()
    {
        var counter = Counter(
            new ChatLogBackfillSnapshotEmote("a", "x", "ome44", null),
            new ChatLogBackfillSnapshotEmote("b", "x", "hob44", null),
            new ChatLogBackfillSnapshotEmote("c", "y", "Kappa", null));

        counter.Count(Message("ome44 Kappa", day: 3));
        counter.Count(Message("hob44", day: 3, userId: NightbotId));
        counter.Count(Message("hob44", day: 4, sourceRoomId: "other-room"));

        Assert.Equal(
            [
                new ChatLogBackfillAggregate("a", "x", Day(3), 1, 0, 0),
                new ChatLogBackfillAggregate("b", "x", Day(3), 0, 1, 0),
                new ChatLogBackfillAggregate("b", "x", Day(4), 0, 0, 1),
                new ChatLogBackfillAggregate("c", "y", Day(3), 1, 0, 0)
            ],
            counter.Aggregates());
    }

    [Fact]
    public void AnEmptyBlock_HasNoAggregates()
    {
        Assert.Empty(Counter().Aggregates());
    }

    private static DateOnly Day(int dayOfJune) => new(2026, 6, dayOfJune);

    private static ChatLogBackfillBlockCounter Counter(DateOnly? kappaEntered = null) =>
        Counter(new ChatLogBackfillSnapshotEmote("s1", "e1", "Kappa", kappaEntered), new ChatLogBackfillSnapshotEmote("s2", "e2", "PogU", null));

    private static ChatLogBackfillBlockCounter Counter(params ChatLogBackfillSnapshotEmote[] snapshot) =>
        new(Block, ChatLogBackfillSnapshotMap.From(snapshot), Detector.IsBot);

    private static ChatLogMessage Message(
        string text,
        int day = 2,
        DateTime? at = null,
        string? userId = "u1",
        IReadOnlyList<KeyValuePair<string, string>>? badges = null,
        string? sourceRoomId = null,
        bool hasOtherSourceMarkers = false) =>
        new(
            at ?? Day(day).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
            userId,
            badges ?? NoBadges,
            Room,
            sourceRoomId,
            hasOtherSourceMarkers,
            text);
}
