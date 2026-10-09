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

        Assert.Equal([new ChatLogBackfillAggregate("e1", Day(2), 1, 0, 0)], counter.Aggregates());
    }

    [Fact]
    public void OwnBotByBadge_CountsIntoBotUseCount()
    {
        var counter = Counter();

        counter.Count(Message("Kappa", badges: [new KeyValuePair<string, string>("bot-badge", "1")]));

        Assert.Equal([new ChatLogBackfillAggregate("e1", Day(2), 0, 1, 0)], counter.Aggregates());
    }

    [Fact]
    public void OwnBotById_CountsIntoBotUseCount()
    {
        var counter = Counter();

        counter.Count(Message("Kappa", userId: NightbotId));

        Assert.Equal([new ChatLogBackfillAggregate("e1", Day(2), 0, 1, 0)], counter.Aggregates());
    }

    [Fact]
    public void ForeignRoom_CountsIntoSharedChat_EvenForABot()
    {
        var counter = Counter();

        counter.Count(Message("Kappa", sourceRoomId: "other-room"));
        counter.Count(Message("Kappa", userId: NightbotId, sourceRoomId: "other-room"));

        Assert.Equal([new ChatLogBackfillAggregate("e1", Day(2), 0, 0, 2)], counter.Aggregates());
    }

    [Fact]
    public void OwnRoomWithASourceRoomTag_StaysOwn()
    {
        var counter = Counter();

        counter.Count(Message("Kappa", sourceRoomId: Room));

        Assert.Equal([new ChatLogBackfillAggregate("e1", Day(2), 1, 0, 0)], counter.Aggregates());
    }

    [Fact]
    public void IndeterminateMarkers_CountIntoSharedChat()
    {
        var counter = Counter();

        counter.Count(Message("Kappa", hasOtherSourceMarkers: true));

        Assert.Equal([new ChatLogBackfillAggregate("e1", Day(2), 0, 0, 1)], counter.Aggregates());
    }

    [Fact]
    public void AHitBeforeTheEntryDay_IsDiscarded_FromTheEntryDayOn_ItCounts()
    {
        var counter = Counter(addedToSetDay: new Dictionary<string, DateOnly?> { ["e1"] = Day(3) });

        counter.Count(Message("Kappa", day: 2));
        counter.Count(Message("Kappa", day: 3));
        counter.Count(Message("Kappa", day: 4));

        Assert.Equal(
            [new ChatLogBackfillAggregate("e1", Day(3), 1, 0, 0), new ChatLogBackfillAggregate("e1", Day(4), 1, 0, 0)],
            counter.Aggregates());
    }

    [Fact]
    public void ANullEntryDay_IsNoGate_OnAnyDayOfTheBlock()
    {
        var counter = Counter(addedToSetDay: new Dictionary<string, DateOnly?> { ["e1"] = null });

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
            [new ChatLogBackfillAggregate("e1", Day(2), 1, 0, 0), new ChatLogBackfillAggregate("e2", Day(2), 1, 0, 0)],
            counter.Aggregates());
    }

    [Fact]
    public void AMessageDatedOutsideTheBlock_IsDropped()
    {
        var counter = Counter();

        counter.Count(Message("Kappa", at: Block.FromUtc.AddTicks(-1)));
        counter.Count(Message("Kappa", at: Block.ToUtcExclusive));
        counter.Count(Message("Kappa", at: Block.ToUtcExclusive.AddTicks(-1)));

        Assert.Equal([new ChatLogBackfillAggregate("e1", Day(7), 1, 0, 0)], counter.Aggregates());
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
    public void Aggregates_AreOrderedByEmoteIdThenDay_AndAddUpPerCell()
    {
        var counter = Counter();

        counter.Count(Message("PogU", day: 5));
        counter.Count(Message("Kappa", day: 4));
        counter.Count(Message("PogU", day: 1));
        counter.Count(Message("Kappa", day: 4, userId: "u2"));
        counter.Count(Message("Kappa", day: 4, userId: NightbotId));

        Assert.Equal(
            [
                new ChatLogBackfillAggregate("e1", Day(4), 2, 1, 0),
                new ChatLogBackfillAggregate("e2", Day(1), 1, 0, 0),
                new ChatLogBackfillAggregate("e2", Day(5), 1, 0, 0)
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

    [Fact]
    public void AnEmptyBlock_HasNoAggregates()
    {
        Assert.Empty(Counter().Aggregates());
    }

    private static DateOnly Day(int dayOfJune) => new(2026, 6, dayOfJune);

    private static ChatLogBackfillBlockCounter Counter(IReadOnlyDictionary<string, DateOnly?>? addedToSetDay = null) =>
        new(
            Block,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Kappa"] = "e1", ["PogU"] = "e2" },
            addedToSetDay ?? new Dictionary<string, DateOnly?>(),
            Detector.IsBot);

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
