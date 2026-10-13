using EmotePurge.Core.Services;
using EmotePurge.Worker.ChatLogBackfill;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Slot plan #375 (spec 4.3, E9): the run's snapshot as the counter reads it, and the worker's rule for
// following a settle that rebound snapshot rows — through the row (EmoteId, Name), never by guessing.
public class ChatLogBackfillSnapshotMapTests
{
    private static readonly DateOnly Day = new(2026, 9, 1);

    [Fact]
    public void From_MapsEveryAliasToItsSlot_TheFirstSlotWinsADuplicateAlias_AndGatesPerSlot()
    {
        var map = ChatLogBackfillSnapshotMap.From(
        [
            new ChatLogBackfillSnapshotEmote("a", "x", "ome44", new DateOnly(2026, 5, 2)),
            new ChatLogBackfillSnapshotEmote("b", "x", "hob44", null),
            new ChatLogBackfillSnapshotEmote("c", "y", "hob44", Day)
        ]);

        Assert.Equal(
            new Dictionary<string, string> { ["ome44"] = "a", ["hob44"] = "b" },
            map.NameToSlotId.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary());
        Assert.Equal(["hob44"], map.AmbiguousNames);
        Assert.Equal((new DateOnly(2026, 5, 2), (DateOnly?)null, Day), (map.AddedToSetDay["a"], map.AddedToSetDay["b"], map.AddedToSetDay["c"]));
        Assert.Equal(("x", "y", (string?)null), (map.EmoteIdOf("b"), map.EmoteIdOf("c"), map.EmoteIdOf("zzz")));
    }

    [Fact]
    public void Rebind_MovesOnlyTheRowsWhoseSlotChanged_KeepingTheirOrderAndCounts()
    {
        var previous = Map(("seed", "x", "A"), ("other", "y", "B"));
        var current = Map(("target", "x", "A"), ("other", "y", "B"));

        var result = ChatLogBackfillSnapshotMap.Rebind(
            [new ChatLogBackfillAggregate("seed", "x", Day, 3, 1, 0), new ChatLogBackfillAggregate("other", "y", Day, 2, 0, 1)], previous, current);

        Assert.Null(result.Failure);
        Assert.Equal(1, result.Moved);
        Assert.Equal(
            [new ChatLogBackfillAggregate("target", "x", Day, 3, 1, 0), new ChatLogBackfillAggregate("other", "y", Day, 2, 0, 1)],
            result.Rows);
    }

    public static TheoryData<string, string[]> Unfollowable => new()
    {
        // The row (x, A) is gone: the key-safe rebinding deleted it next to an existing target row.
        { "row gone", ["other|y|B"] },
        // No row of the block moved: the server's SnapshotChanged has no explanation in the snapshot.
        { "row unchanged", ["seed|x|A", "other|y|B"] },
        // The row is no longer unique — the stable key cannot tell the targets apart.
        { "row twice", ["t1|x|A", "t2|x|A", "other|y|B"] },
    };

    [Theory]
    [MemberData(nameof(Unfollowable))]
    public void Rebind_Fails_WhenTheBlocksRowsCannotBeFollowed(string _, string[] currentRows)
    {
        var previous = Map(("seed", "x", "A"), ("other", "y", "B"));
        var current = ChatLogBackfillSnapshotMap.From(
            [.. currentRows.Select(r => r.Split('|')).Select(p => new ChatLogBackfillSnapshotEmote(p[0], p[1], p[2], null))]);

        var result = ChatLogBackfillSnapshotMap.Rebind([new ChatLogBackfillAggregate("seed", "x", Day, 1, 0, 0)], previous, current);

        Assert.Null(result.Rows);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public void Rebind_Fails_ForAnAggregateThatWasNeverASnapshotRow()
    {
        var previous = Map(("seed", "x", "A"));
        var current = Map(("target", "x", "A"));

        Assert.Null(ChatLogBackfillSnapshotMap.Rebind([new ChatLogBackfillAggregate("stray", "x", Day, 1, 0, 0)], previous, current).Rows);
        Assert.Null(ChatLogBackfillSnapshotMap.Rebind([new ChatLogBackfillAggregate("seed", "y", Day, 1, 0, 0)], previous, current).Rows);
    }

    // The row decides, not the slot id: a slot that now belongs to another row of the same emote does not
    // keep the aggregate that was counted for its old row.
    [Fact]
    public void Rebind_FollowsTheRow_EvenWhenItsOldSlotNowBelongsToAnotherRow()
    {
        var previous = Map(("seed", "x", "A"), ("target", "x", "B"));
        var current = Map(("target", "x", "A"), ("b-new", "x", "B"));

        var result = ChatLogBackfillSnapshotMap.Rebind(
            [new ChatLogBackfillAggregate("seed", "x", Day, 1, 0, 0), new ChatLogBackfillAggregate("target", "x", Day, 2, 0, 0)], previous, current);

        Assert.Equal(2, result.Moved);
        Assert.Equal(
            [new ChatLogBackfillAggregate("target", "x", Day, 1, 0, 0), new ChatLogBackfillAggregate("b-new", "x", Day, 2, 0, 0)],
            result.Rows);
    }

    // A guard the snapshot key makes unreachable (one row per slot): two rows ending on one slot would
    // make two aggregates of one cell; the worker fails the run instead of adding them up.
    [Fact]
    public void Rebind_Fails_WhenTwoAggregatesLandOnOneSlotAndDay()
    {
        var previous = Map(("seed", "x", "A"), ("other", "x", "B"));
        var current = Map(("target", "x", "A"), ("target", "x", "B"));

        var result = ChatLogBackfillSnapshotMap.Rebind(
            [new ChatLogBackfillAggregate("seed", "x", Day, 1, 0, 0), new ChatLogBackfillAggregate("other", "x", Day, 1, 0, 0)], previous, current);

        Assert.Null(result.Rows);
    }

    private static ChatLogBackfillSnapshotMap Map(params (string SlotId, string EmoteId, string Name)[] rows) =>
        ChatLogBackfillSnapshotMap.From([.. rows.Select(r => new ChatLogBackfillSnapshotEmote(r.SlotId, r.EmoteId, r.Name, null))]);
}
