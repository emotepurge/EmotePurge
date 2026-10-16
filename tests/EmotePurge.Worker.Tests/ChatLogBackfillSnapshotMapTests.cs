using EmotePurge.Core.Services;
using EmotePurge.Worker.ChatLogBackfill;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Slot plan #375 (spec 4.3): the run's snapshot as the counter reads it.
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
}
